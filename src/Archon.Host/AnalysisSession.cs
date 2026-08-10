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
    private WorkspaceModel? _workspace;

    /// <summary>
    /// One entry per project directory, so a save-triggered pass costs a dictionary lookup once a
    /// project has been discovered. Without this, every save under <c>analyseOn: type</c> paid for
    /// a full recursive walk of the project's directory tree to find nothing that had changed since
    /// the last save — the file set does not change just because a file's content did.
    /// </summary>
    private readonly Dictionary<string, WorkspaceModel> _projectWorkspaces = new(StringComparer.OrdinalIgnoreCase);

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

        // Validated once the registry exists, because an id is only known to be misspelled after
        // the packs that could have declared it have loaded. These reach the editor through the
        // same 'messages' array as a malformed file, so a silently ignored entry is visible in the
        // log rather than only discoverable by noticing a rule did not do what it was told.
        _messages.AddRange(ConfigValidator.Validate(config, registry));

        Config = config;
        Registry = registry;
        Baseline = baseline;
        Engine = new AnalysisEngine(registry, Sources);

        // The call graph is derived from file content alone, so a change of severities or of the
        // baseline cannot invalidate it. Only the file set can, and exclusions are applied when the
        // workspace is rediscovered, at which point the graph drops whatever left the workspace.
        InvalidateWorkspace();
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

    /// <summary>
    /// Forgets which files the workspace contains, for a change that adds or removes one rather
    /// than editing it. Content caches are left alone: a file that is still present has not
    /// changed just because its neighbour appeared.
    /// </summary>
    public void InvalidateWorkspace()
    {
        _workspace = null;
        _projectWorkspaces.Clear();
    }

    /// <summary>
    /// The project owning a file, discovered once per project directory and held until a structural
    /// change retires it. A rebuilt copy is never partially stale: any change that could alter which
    /// files belong to the project also calls <see cref="InvalidateWorkspace"/>, which clears every
    /// entry here alongside the whole-workspace model.
    /// </summary>
    public WorkspaceModel DiscoverProjectOf(string filePath)
    {
        string? projectDirectory = WorkspaceModel.FindProjectDirectory(filePath, Config.WorkspaceRoot);
        if (projectDirectory is null)
        {
            return WorkspaceModel.ForSingleFile(Path.GetFullPath(filePath), Config.WorkspaceRoot);
        }
        if (_projectWorkspaces.TryGetValue(projectDirectory, out WorkspaceModel? cached))
        {
            return cached;
        }
        WorkspaceModel discovered = WorkspaceModel.DiscoverForProjectDirectory(
            projectDirectory, Config.WorkspaceRoot, Config.EffectiveExcludes());
        _projectWorkspaces[projectDirectory] = discovered;
        return discovered;
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

    /// <summary>
    /// The files under analysis, discovered once and held until something adds or removes one.
    ///
    /// Discovery walks the entire tree, and it sits in front of every request that needs the file
    /// set — including the impact query the editor issues for each file it shows. Rediscovering on
    /// each of those made the warm call graph behind it largely beside the point.
    /// </summary>
    public WorkspaceModel DiscoverWorkspace() =>
        _workspace ??= WorkspaceModel.Discover(Root, Config.EffectiveExcludes());
}
