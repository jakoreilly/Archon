using System.Xml.Linq;
using Archon.Core.Findings;
using Archon.Core.Rules;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a cycle in project references, which prevents the projects involved from being built,
/// versioned or reasoned about independently.
///
/// References are read directly from the project files, so no build and no project system are
/// needed. A reference pointing outside the analysed set is followed no further, and a project file
/// that cannot be read is reported rather than silently dropping its edges, since a missing edge
/// could hide a cycle.
/// </summary>
public sealed class ProjectCycleRule : IRule
{
    public const string Id = "AR0040";

    public const string ProjectUnreadable = "AR0041";

    private const string Category = "architecture";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            Id,
            "Project reference cycle",
            Category,
            Severity.Error,
            "Two or more projects reference each other, directly or through intermediaries."),
        new RuleDescriptor(
            ProjectUnreadable,
            "Project file could not be read",
            Category,
            Severity.Information,
            "A project file could not be parsed, so its references were not included in the graph.")
    };

    public RuleScope Scope => RuleScope.Workspace;

    public string Language => RuleLanguages.Any;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        var findings = new List<Finding>();
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Core.Sources.ProjectModel project in context.Workspace.Projects)
        {
            known.Add(Normalise(project.ProjectFilePath));
        }

        foreach (Core.Sources.ProjectModel project in context.Workspace.Projects)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string from = Normalise(project.ProjectFilePath);

            if (!TryReadReferences(project.ProjectFilePath, out List<string> references, out string? error))
            {
                if (context.IsEnabled(ProjectUnreadable))
                {
                    findings.Add(new Finding
                    {
                        RuleId = ProjectUnreadable,
                        FilePath = project.ProjectFilePath,
                        Kind = "ProjectUnreadable",
                        Span = SourceSpan.None,
                        Message = $"Could not read project references: {error}. A cycle through this project would not be detected."
                    });
                }
                continue;
            }

            List<string> targets = edges.TryGetValue(from, out List<string>? existing) ? existing : edges[from] = new List<string>();
            foreach (string reference in references)
            {
                string to = Normalise(reference);
                if (known.Contains(to))
                {
                    targets.Add(to);
                }
            }
        }

        if (!context.IsEnabled(Id))
        {
            return findings;
        }

        foreach (List<string> cycle in StronglyConnectedComponents(edges).Where(c => c.Count > 1))
        {
            var names = cycle.Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).ToList();
            string description = string.Join(" -> ", names) + $" -> {names[0]}";

            foreach (string member in cycle)
            {
                findings.Add(new Finding
                {
                    RuleId = Id,
                    FilePath = member,
                    Kind = "ProjectCycle",
                    Span = SourceSpan.None,
                    Message = $"This project is part of a reference cycle of {cycle.Count} projects: {description}."
                });
            }
        }

        return findings;
    }

    private static bool TryReadReferences(string projectFilePath, out List<string> references, out string? error)
    {
        references = new List<string>();
        error = null;
        try
        {
            XDocument document = XDocument.Load(projectFilePath);
            string directory = Path.GetDirectoryName(projectFilePath) ?? "";

            foreach (XElement element in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                string? include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }
                string relative = include.Replace('\\', Path.DirectorySeparatorChar);
                references.Add(Path.GetFullPath(Path.Combine(directory, relative)));
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Normalise(string path) => Path.GetFullPath(path);

    /// <summary>
    /// Finds strongly connected components iteratively, so a deeply nested graph cannot exhaust the
    /// stack. Every component of more than one node is a cycle, and a component's whole membership
    /// is reported rather than one arbitrary edge, because any single edge in a cycle looks
    /// individually reasonable.
    /// </summary>
    public static List<List<string>> StronglyConnectedComponents(Dictionary<string, List<string>> edges)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowLink = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var components = new List<List<string>>();
        int nextIndex = 0;

        foreach (string start in edges.Keys)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }

            var work = new Stack<(string Node, int Next)>();
            work.Push((start, 0));
            index[start] = lowLink[start] = nextIndex++;
            stack.Push(start);
            onStack.Add(start);

            while (work.Count > 0)
            {
                (string node, int next) = work.Pop();
                List<string> targets = edges.TryGetValue(node, out List<string>? list) ? list : new List<string>();

                bool descended = false;
                for (int i = next; i < targets.Count; i++)
                {
                    string target = targets[i];
                    if (!index.ContainsKey(target))
                    {
                        work.Push((node, i + 1));
                        index[target] = lowLink[target] = nextIndex++;
                        stack.Push(target);
                        onStack.Add(target);
                        work.Push((target, 0));
                        descended = true;
                        break;
                    }
                    if (onStack.Contains(target))
                    {
                        lowLink[node] = Math.Min(lowLink[node], index[target]);
                    }
                }
                if (descended)
                {
                    continue;
                }

                if (lowLink[node] == index[node])
                {
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (!string.Equals(member, node, StringComparison.OrdinalIgnoreCase));
                    components.Add(component);
                }

                if (work.Count > 0)
                {
                    (string parent, int parentNext) = work.Pop();
                    lowLink[parent] = Math.Min(lowLink[parent], lowLink[node]);
                    work.Push((parent, parentNext));
                }
            }
        }

        return components;
    }
}
