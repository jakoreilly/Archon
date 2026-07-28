using Archon.Core.Configuration;
using Archon.Core.Engine;
using Archon.Core.Findings;
using Archon.Core.Insights;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Archon.Rules;

namespace Archon.Host;

/// <summary>
/// The warm state a host keeps between requests: configuration, the rule registry, the baseline
/// and the parse cache. Holding these open is the point of a long-lived process — a save re-reads
/// and re-parses only the file that changed, and every rule then shares that one parse instead of
/// each paying for its own process start and its own pass over the source.
/// </summary>
internal sealed class AnalysisSession
{
    private readonly List<string> _messages = new();

    private AnalysisSession(string root)
    {
        Root = root;
        Sources = new SourceCache();
        Registry = new RuleRegistry();
        Config = new ArchonConfig { WorkspaceRoot = root };
        Baseline = Baseline.Empty;
        Engine = new AnalysisEngine(Registry, Sources);
        CallGraph = new CallGraph(Sources);
    }

    public string Root { get; }

    public SourceCache Sources { get; }

    public CallGraph CallGraph { get; }

    public RuleRegistry Registry { get; private set; }

    public ArchonConfig Config { get; private set; }

    public Baseline Baseline { get; private set; }

    public AnalysisEngine Engine { get; private set; }

    public IReadOnlyList<string> Messages => _messages;

    public static AnalysisSession Create(string root)
    {
        var session = new AnalysisSession(Path.GetFullPath(root));
        session.ReloadConfiguration();
        return session;
    }

    /// <summary>
    /// Re-reads configuration, rule packs and the baseline, keeping any session severity overrides
    /// so that a rule switched off from the editor stays off across a configuration change.
    /// </summary>
    public void ReloadConfiguration()
    {
        _messages.Clear();

        var previousOverrides = new Dictionary<string, Severity>(Config.SessionOverrides, StringComparer.OrdinalIgnoreCase);

        ArchonConfig config = ConfigLoader.Load(Root, out string? configError);
        if (configError is not null)
        {
            _messages.Add(configError);
        }
        foreach ((string ruleId, Severity severity) in previousOverrides)
        {
            config.SessionOverrides[ruleId] = severity;
        }

        var registry = new RuleRegistry();
        registry.Add(new BuiltInRulePack());
        foreach (string pack in config.RulePacks)
        {
            registry.AddFromAssembly(Path.IsPathRooted(pack) ? pack : Path.Combine(config.WorkspaceRoot, pack));
        }

        Baseline baseline = Baseline.Load(BaselinePathFor(config), out string? baselineError);
        if (baselineError is not null)
        {
            _messages.Add(baselineError);
        }

        Config = config;
        Registry = registry;
        Baseline = baseline;
        Engine = new AnalysisEngine(registry, Sources);
        CallGraph.Clear();
    }

    /// <summary>
    /// Drops one file from everything derived from its content, so a caller changing a file has a
    /// single call to make and cannot update the parse cache while leaving the call graph stale.
    /// </summary>
    public void Invalidate(string path)
    {
        Sources.Invalidate(path);
        CallGraph.Invalidate(path);
    }

    /// <summary>Registers unsaved editor text for a file, invalidating what was derived from it.</summary>
    public void SetText(string path, string text)
    {
        Sources.SetText(path, text);
        CallGraph.Invalidate(path);
    }

    public string BaselinePath => BaselinePathFor(Config);

    private static string BaselinePathFor(ArchonConfig config) =>
        Path.IsPathRooted(config.Baseline)
            ? config.Baseline
            : Path.Combine(config.WorkspaceRoot, config.Baseline);

    public WorkspaceModel DiscoverWorkspace() =>
        WorkspaceModel.Discover(Root, Config.EffectiveExcludes());
}
