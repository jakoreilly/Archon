using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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

    /// <summary>
    /// Covering-test counts already computed against the current edge set. Every method in a file
    /// walks the same caller graph, and neighbouring methods usually share most of it, so without
    /// this a file of fifty methods pays for fifty traversals of the whole graph.
    /// </summary>
    private readonly Dictionary<(string Key, int Depth), (int Count, bool Bounded)> _coverage = new();

    public CallGraph(SourceCache sources) => _sources = sources;

    /// <summary>Drops one file's methods, so the next query re-reads and re-indexes only that file.</summary>
    public void Invalidate(string path)
    {
        lock (_gate)
        {
            _methodsByFile.Remove(Path.GetFullPath(path));
            InvalidateEdges();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _methodsByFile.Clear();
            _projectDirectories.Clear();
            InvalidateEdges();
        }
    }

    private void InvalidateEdges()
    {
        _callersByKey = null;
        _coverage.Clear();
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

                if (!_coverage.TryGetValue((method.Key, depthLimit), out (int Count, bool Bounded) coverage))
                {
                    coverage = CountCoveringTests(method, callers, depthLimit);
                    _coverage[(method.Key, depthLimit)] = coverage;
                }
                (int coveringTests, bool bounded) = coverage;

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
            InvalidateEdges();
        }

        List<string> stale = _methodsByFile.Keys.Where(k => !present.Contains(k)).ToList();
        foreach (string path in stale)
        {
            _methodsByFile.Remove(path);
            InvalidateEdges();
        }
    }

    /// <summary>
    /// Indexes every member that can hold a call, not only ordinary methods. Constructors, property
    /// and event accessors and local functions all make calls, and a codebase that injects its
    /// dependencies makes a great many of them from constructors: counting only method bodies
    /// reports nothing for a method that is in constant use.
    /// </summary>
    private List<MethodNode> IndexFile(string path)
    {
        var methods = new List<MethodNode>();
        ParsedCSharp? parsed = _sources.GetCSharp(path);
        if (parsed is null)
        {
            return methods;
        }

        string projectDirectory = ProjectDirectoryOf(path);

        void Add(SyntaxToken identifier, int arity, SyntaxNode? body, bool isTest)
        {
            LinePosition position = parsed.Tree.GetLineSpan(identifier.Span).StartLinePosition;
            methods.Add(new MethodNode(
                identifier.Text,
                arity,
                path,
                projectDirectory,
                position.Line,
                position.Character,
                isTest,
                body is null ? Array.Empty<string>() : CalleeKeys(body)));
        }

        foreach (SyntaxNode node in parsed.Root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    Add(
                        method.Identifier,
                        method.ParameterList.Parameters.Count,
                        method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression,
                        IsTestMethod(method.AttributeLists));
                    break;

                // A constructor is reached by `new T(...)` rather than by an invocation, which
                // CalleeKeys records under the type name so the two meet on the same key.
                case ConstructorDeclarationSyntax constructor:
                    Add(
                        constructor.Identifier,
                        constructor.ParameterList.Parameters.Count,
                        constructor.Body ?? (SyntaxNode?)constructor.ExpressionBody?.Expression,
                        isTest: false);
                    break;

                case LocalFunctionStatementSyntax local:
                    Add(
                        local.Identifier,
                        local.ParameterList.Parameters.Count,
                        local.Body ?? (SyntaxNode?)local.ExpressionBody?.Expression,
                        isTest: false);
                    break;

                // A property is indexed once, covering every accessor, because a call site writes
                // the property name rather than the accessor. Auto-properties are skipped: they
                // hold no calls, so a node for one would only put an empty lens on every field of
                // every data carrier.
                case BasePropertyDeclarationSyntax property when HasBody(property):
                    SyntaxToken name = property switch
                    {
                        PropertyDeclarationSyntax declared => declared.Identifier,
                        EventDeclarationSyntax declared => declared.Identifier,
                        IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
                        _ => default
                    };
                    if (name != default)
                    {
                        Add(name, 0, property, isTest: false);
                    }
                    break;
            }
        }
        return methods;
    }

    private static bool IsTestMethod(SyntaxList<AttributeListSyntax> attributeLists) =>
        attributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => TestAttributes.Contains(SimpleAttributeName(attribute.Name.ToString())));

    /// <summary>Whether a property, indexer or event declares any code, as opposed to being generated.</summary>
    private static bool HasBody(BasePropertyDeclarationSyntax property)
    {
        if (property is PropertyDeclarationSyntax { ExpressionBody: not null })
        {
            return true;
        }
        return property.AccessorList?.Accessors
            .Any(a => a.Body is not null || a.ExpressionBody is not null) ?? false;
    }

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

        // A local function is indexed as a member in its own right, so its calls belong to it and
        // not also to whatever encloses it. Counting them twice would report two callers for a
        // single call site; the enclosing member still reaches them through the graph.
        foreach (SyntaxNode node in body.DescendantNodesAndSelf(
                     n => ReferenceEquals(n, body) || n is not LocalFunctionStatementSyntax))
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
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
                    break;

                // `new T(a, b)` reaches T's constructor, which is indexed under the type name, so
                // recording it here is what gives a constructor any callers at all.
                case ObjectCreationExpressionSyntax creation:
                    string? type = creation.Type switch
                    {
                        IdentifierNameSyntax identifier => identifier.Identifier.Text,
                        GenericNameSyntax generic => generic.Identifier.Text,
                        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                        _ => null
                    };
                    if (type is not null)
                    {
                        keys.Add($"{type}/{creation.ArgumentList?.Arguments.Count ?? 0}");
                    }
                    break;
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
