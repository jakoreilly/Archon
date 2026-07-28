using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Archon.Core.Rules;
using Archon.Core.Sources;

namespace Archon.Core.Insights;

/// <summary>A method declaration and the call targets found in its body.</summary>
public sealed record MethodNode(
    string Name,
    int Arity,
    string FilePath,
    string ProjectDirectory,
    int Line,
    int Column,
    bool IsTest,
    IReadOnlyList<string> CalleeKeys)
{
    public string Key => $"{Name}/{Arity}";
}

/// <summary>A call site that reaches a method directly.</summary>
public sealed record CallerLocation(string MethodName, string FilePath, int Line, int Column);

/// <summary>
/// How widely a single method is reached. Counts are approximations, and every field is reported
/// alongside the qualifier that makes that clear at the point of use.
/// </summary>
public sealed record MethodImpact(
    string MethodName,
    int Arity,
    int Line,
    int Column,
    int ReferenceCount,
    int ProjectCount,
    int CoveringTestCount,
    bool DepthBounded,
    IReadOnlyList<CallerLocation> Callers);

/// <summary>The impact of every method in one file, with the size of the graph it was measured against.</summary>
public sealed record ImpactResult(IReadOnlyList<MethodImpact> Methods, int MethodCount, int FileCount);

/// <summary>
/// A reverse call graph over the whole workspace, held between requests and updated one file at a
/// time.
///
/// A call is matched on name and argument count rather than on a resolved symbol, because no
/// compilation is available. Overloads that share a name and argument count are therefore counted
/// together, extension methods are attributed to the name as written, and calls made through
/// reflection, dynamic dispatch or a container are invisible. Every number this produces is an
/// approximation of how far a change reaches, never an exact reference count.
///
/// Parsing goes through the shared source cache, so a file already parsed for a rule is not parsed
/// again here, and a save re-parses only the file that changed.
/// </summary>
public sealed class CallGraph
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestCase", "TestCaseAttribute",
        "TestMethod", "TestMethodAttribute"
    };

    private readonly SourceCache _sources;
    private readonly Dictionary<string, List<MethodNode>> _methodsByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _projectDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Dictionary<string, List<MethodNode>>? _callersByKey;

    public CallGraph(SourceCache sources) => _sources = sources;

    /// <summary>Drops one file's methods, so the next query re-reads and re-indexes only that file.</summary>
    public void Invalidate(string path)
    {
        lock (_gate)
        {
            _methodsByFile.Remove(Path.GetFullPath(path));
            _callersByKey = null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _methodsByFile.Clear();
            _projectDirectories.Clear();
            _callersByKey = null;
        }
    }

    public ImpactResult Impact(
        WorkspaceModel workspace,
        string filePath,
        int maxDepth,
        int maxCallers,
        CancellationToken cancellationToken = default)
    {
        string target = Path.GetFullPath(filePath);
        int depthLimit = Math.Max(1, maxDepth);

        lock (_gate)
        {
            Synchronise(workspace, cancellationToken);
            Dictionary<string, List<MethodNode>> callers = CallersByKey();

            var impacts = new List<MethodImpact>();
            if (!_methodsByFile.TryGetValue(target, out List<MethodNode>? methods))
            {
                return new ImpactResult(impacts, MethodCountLocked(), _methodsByFile.Count);
            }

            foreach (MethodNode method in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<MethodNode> direct = callers.TryGetValue(method.Key, out List<MethodNode>? found)
                    ? found
                    : new List<MethodNode>();

                (int coveringTests, bool bounded) = CountCoveringTests(method, callers, depthLimit);

                impacts.Add(new MethodImpact(
                    method.Name,
                    method.Arity,
                    method.Line,
                    method.Column,
                    direct.Count,
                    direct.Select(c => c.ProjectDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    coveringTests,
                    bounded,
                    DescribeCallers(direct, maxCallers)));
            }
            return new ImpactResult(impacts, MethodCountLocked(), _methodsByFile.Count);
        }
    }

    /// <summary>
    /// Brings the index in line with the workspace: files not yet indexed are parsed, and files no
    /// longer in the workspace are dropped so a deleted file cannot keep contributing edges.
    /// </summary>
    private void Synchronise(WorkspaceModel workspace, CancellationToken cancellationToken)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFile file in workspace.FilesOfLanguage(RuleLanguages.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(file.Path);
            present.Add(path);
            if (_methodsByFile.ContainsKey(path))
            {
                continue;
            }
            _methodsByFile[path] = IndexFile(path);
            _callersByKey = null;
        }

        List<string> stale = _methodsByFile.Keys.Where(k => !present.Contains(k)).ToList();
        foreach (string path in stale)
        {
            _methodsByFile.Remove(path);
            _callersByKey = null;
        }
    }

    private List<MethodNode> IndexFile(string path)
    {
        var methods = new List<MethodNode>();
        ParsedCSharp? parsed = _sources.GetCSharp(path);
        if (parsed is null)
        {
            return methods;
        }

        string projectDirectory = ProjectDirectoryOf(path);
        foreach (MethodDeclarationSyntax declaration in parsed.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            LinePosition position = parsed.Tree.GetLineSpan(declaration.Identifier.Span).StartLinePosition;
            SyntaxNode? body = declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody?.Expression;

            methods.Add(new MethodNode(
                declaration.Identifier.Text,
                declaration.ParameterList.Parameters.Count,
                path,
                projectDirectory,
                position.Line,
                position.Character,
                IsTestMethod(declaration),
                body is null ? Array.Empty<string>() : CalleeKeys(body)));
        }
        return methods;
    }

    private static bool IsTestMethod(MethodDeclarationSyntax declaration) =>
        declaration.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => TestAttributes.Contains(SimpleAttributeName(attribute.Name.ToString())));

    private static string SimpleAttributeName(string text)
    {
        int generic = text.IndexOf('<', StringComparison.Ordinal);
        string trimmed = generic >= 0 ? text[..generic] : text;
        int lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }

    private static List<string> CalleeKeys(SyntaxNode body)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (InvocationExpressionSyntax invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            string? name = invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
                GenericNameSyntax generic => generic.Identifier.Text,
                _ => null
            };
            if (name is not null)
            {
                keys.Add($"{name}/{invocation.ArgumentList.Arguments.Count}");
            }
        }
        return keys.ToList();
    }

    private Dictionary<string, List<MethodNode>> CallersByKey()
    {
        if (_callersByKey is not null)
        {
            return _callersByKey;
        }

        var callers = new Dictionary<string, List<MethodNode>>(StringComparer.Ordinal);
        foreach (MethodNode caller in _methodsByFile.Values.SelectMany(m => m))
        {
            foreach (string callee in caller.CalleeKeys)
            {
                if (!callers.TryGetValue(callee, out List<MethodNode>? list))
                {
                    list = new List<MethodNode>();
                    callers[callee] = list;
                }
                list.Add(caller);
            }
        }
        _callersByKey = callers;
        return callers;
    }

    /// <summary>
    /// Walks callers breadth-first to a bounded depth, counting distinct test methods reached.
    /// Reports whether the bound was hit, so a truncated count can be presented as a lower bound
    /// rather than as the answer.
    /// </summary>
    private static (int Count, bool Bounded) CountCoveringTests(
        MethodNode target,
        Dictionary<string, List<MethodNode>> callers,
        int maxDepth)
    {
        var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal) { target.Key };
        var frontier = new Queue<(string Key, int Depth)>();
        frontier.Enqueue((target.Key, 0));
        bool bounded = false;

        while (frontier.Count > 0)
        {
            (string key, int depth) = frontier.Dequeue();
            if (depth >= maxDepth)
            {
                bounded |= callers.ContainsKey(key);
                continue;
            }
            if (!callers.TryGetValue(key, out List<MethodNode>? incoming))
            {
                continue;
            }
            foreach (MethodNode caller in incoming)
            {
                if (caller.IsTest)
                {
                    tests.Add($"{caller.FilePath}::{caller.Line}");
                }
                if (visited.Add(caller.Key))
                {
                    frontier.Enqueue((caller.Key, depth + 1));
                }
            }
        }
        return (tests.Count, bounded);
    }

    private static List<CallerLocation> DescribeCallers(List<MethodNode> callers, int limit)
    {
        return callers
            .Select(c => new CallerLocation(c.Name, c.FilePath, c.Line, c.Column))
            .DistinctBy(c => $"{c.FilePath}::{c.Line}::{c.Column}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Line)
            .Take(Math.Max(0, limit))
            .ToList();
    }

    /// <summary>
    /// Attributes a file to the nearest ancestor directory holding a project file. This is a cheap
    /// stand-in for real project membership: a file excluded from its nearest project, or included
    /// from elsewhere by a wildcard, is attributed by location rather than by the project system.
    /// </summary>
    private string ProjectDirectoryOf(string filePath)
    {
        string directory = Path.GetDirectoryName(filePath) ?? filePath;
        if (_projectDirectories.TryGetValue(directory, out string? cached))
        {
            return cached;
        }

        string? current = directory;
        string resolved = directory;
        while (current is not null)
        {
            if (Directory.Exists(current) && Directory.GetFiles(current, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
            {
                resolved = current;
                break;
            }
            current = Path.GetDirectoryName(current);
        }
        _projectDirectories[directory] = resolved;
        return resolved;
    }

    private int MethodCountLocked() => _methodsByFile.Values.Sum(m => m.Count);
}
