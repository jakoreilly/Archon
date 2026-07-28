using Archon.Core.Configuration;
using Archon.Core.Engine;
using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Archon.Rules;

namespace Archon.Tests;

/// <summary>Records assertion outcomes and reports them as an exit code.</summary>
internal sealed class Harness
{
    private readonly List<string> _failures = new();
    private int _passed;
    private string _group = "";

    public void Group(string name)
    {
        _group = name;
        Console.WriteLine();
        Console.WriteLine(name);
    }

    public void Check(string description, bool condition)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  pass  {description}");
            return;
        }
        _failures.Add($"{_group}: {description}");
        Console.WriteLine($"  FAIL  {description}");
    }

    public void Equal<T>(string description, T expected, T actual)
    {
        bool equal = EqualityComparer<T>.Default.Equals(expected, actual);
        Check(equal ? description : $"{description} (expected {expected}, got {actual})", equal);
    }

    public int Report()
    {
        Console.WriteLine();
        if (_failures.Count == 0)
        {
            Console.WriteLine($"All {_passed} assertions passed.");
            return 0;
        }
        Console.WriteLine($"{_passed} passed, {_failures.Count} failed:");
        foreach (string failure in _failures)
        {
            Console.WriteLine($"  {failure}");
        }
        return 1;
    }
}

/// <summary>
/// Builds an engine over in-memory sources, so a rule can be exercised without touching the
/// filesystem. Text registered here is indistinguishable to a rule from text read from disk,
/// which is the same mechanism the editor uses for unsaved changes.
/// </summary>
internal sealed class TestWorkspace
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "archon-tests");

    public ArchonConfig Config { get; }

    public TestWorkspace()
    {
        Config = new ArchonConfig { WorkspaceRoot = _root };
    }

    public string Add(string relativePath, string text)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _files[full] = text;
        return full;
    }

    public TestWorkspace WithLayers(string mode, Dictionary<string, List<string>> layers, List<LayerEdge>? deny = null, List<LayerEdge>? allow = null)
    {
        Config.Layers = new LayerConfig
        {
            Mode = mode,
            Layers = layers,
            Deny = deny ?? new List<LayerEdge>(),
            Allow = allow ?? new List<LayerEdge>()
        };
        return this;
    }

    public TestWorkspace WithSeverity(string ruleId, string severity)
    {
        Config.Rules[ruleId] = severity;
        return this;
    }

    /// <summary>Supplies a rule's options object as it would appear in the configuration file.</summary>
    public TestWorkspace WithOption(string ruleId, string json)
    {
        Config.Options[ruleId] = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        return this;
    }

    /// <summary>Runs every scope over the registered files.</summary>
    public AnalysisResult Analyse(Baseline? baseline = null)
    {
        (AnalysisEngine engine, WorkspaceModel workspace) = Build();
        return engine.AnalyseWorkspace(workspace, Config, baseline ?? Baseline.Empty);
    }

    /// <summary>Runs only the rules a single file can decide, as a save-triggered pass does.</summary>
    public AnalysisResult AnalyseFileOnly(string filePath, Baseline? baseline = null)
    {
        (AnalysisEngine engine, _) = Build();
        return engine.AnalyseFile(filePath, Config, baseline ?? Baseline.Empty);
    }

    private (AnalysisEngine Engine, WorkspaceModel Workspace) Build()
    {
        var registry = new RuleRegistry();
        registry.Add(new BuiltInRulePack());

        var cache = new SourceCache();
        var files = new List<SourceFile>();
        foreach ((string path, string text) in _files)
        {
            cache.SetText(path, text);
            string language = Path.GetExtension(path).Equals(".sql", StringComparison.OrdinalIgnoreCase)
                ? RuleLanguages.Sql
                : RuleLanguages.CSharp;
            files.Add(new SourceFile(path, language));
        }

        return (new AnalysisEngine(registry, cache), WorkspaceModel.FromFiles(_root, files));
    }
}

internal static class FindingExtensions
{
    public static int CountOf(this IReadOnlyList<Finding> findings, string ruleId) =>
        findings.Count(f => f.RuleId == ruleId);

    public static Finding? FirstOf(this IReadOnlyList<Finding> findings, string ruleId) =>
        findings.FirstOrDefault(f => f.RuleId == ruleId);
}
