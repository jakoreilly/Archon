using System.Collections.Concurrent;
using System.Diagnostics;
using Archon.Core.Configuration;
using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;

namespace Archon.Core.Engine;

/// <summary>Why a rule produced nothing, when the reason is not simply a clean result.</summary>
public sealed record SkippedRule(string RuleId, string Reason);

/// <summary>The outcome of one analysis pass, including what did not run and why.</summary>
public sealed record AnalysisResult
{
    public required IReadOnlyList<Finding> Findings { get; init; }

    /// <summary>Findings present in the baseline. Reported, but excluded from build failure.</summary>
    public required IReadOnlyList<Finding> BaselinedFindings { get; init; }

    /// <summary>
    /// Baseline entries that no finding matched, among those this run was in a position to judge:
    /// the entry's file was analysed with the entry's rule, or the file no longer exists. Each one
    /// is a suppression that has outlived its finding; <c>archon baseline --prune</c> drops them.
    /// </summary>
    public IReadOnlyList<BaselineEntry> StaleBaselineEntries { get; init; } = Array.Empty<BaselineEntry>();

    public required IReadOnlyList<SkippedRule> Skipped { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }

    public required int FilesAnalysed { get; init; }

    public required long ElapsedMilliseconds { get; init; }

    /// <summary>
    /// A description of the narrowing applied after analysis, such as "changed lines since
    /// origin/main", or <c>null</c> when the result is the whole workspace.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>Findings that analysis produced but the narrowing excluded. Zero when unscoped.</summary>
    public int OutsideScope { get; init; }

    public int CountAtLeast(Severity severity) => Findings.Count(f => f.Severity >= severity);
}

/// <summary>
/// Runs rules over cached sources and applies configuration, suppressions and the baseline in one
/// place. Rules therefore contain only detection logic: they never read settings, never check for
/// an ignore comment and never decide a severity, which is what keeps their behaviour identical
/// across every host that runs them.
/// </summary>
public sealed class AnalysisEngine
{
    private readonly RuleRegistry _registry;
    private readonly SourceCache _sources;

    public AnalysisEngine(RuleRegistry registry, SourceCache sources)
    {
        _registry = registry;
        _sources = sources;
    }

    public SourceCache Sources => _sources;

    public RuleRegistry Registry => _registry;

    /// <summary>Analyses one file with the rules that a single file can decide.</summary>
    public AnalysisResult AnalyseFile(string filePath, ArchonConfig config, Baseline baseline, CancellationToken cancellationToken = default)
    {
        WorkspaceModel workspace = WorkspaceModel.ForSingleFile(filePath, config.WorkspaceRoot);
        return Run(workspace, config, baseline, new[] { RuleScope.File }, null, cancellationToken);
    }

    /// <summary>
    /// Analyses one file together with the project that owns it, so project-scope rules run on a
    /// save without paying for a whole-workspace pass. Findings elsewhere in the same project are
    /// included, since that is what those rules exist to see.
    ///
    /// The project workspace is supplied rather than discovered here, so a host that keeps its own
    /// cache of project workspaces — keyed by project directory, and retired only by a structural
    /// change — pays the cost of walking that project's directory once rather than on every save.
    /// </summary>
    public AnalysisResult AnalyseFileInProject(string filePath, WorkspaceModel projectWorkspace, ArchonConfig config, Baseline baseline, CancellationToken cancellationToken = default)
    {
        SourceFile? target = projectWorkspace.Files
            .FirstOrDefault(f => string.Equals(f.Path, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
        return Run(projectWorkspace, config, baseline, new[] { RuleScope.File, RuleScope.Project }, target, cancellationToken);
    }

    /// <summary>Analyses an entire workspace with every scope that has its inputs available.</summary>
    public AnalysisResult AnalyseWorkspace(WorkspaceModel workspace, ArchonConfig config, Baseline baseline, CancellationToken cancellationToken = default)
    {
        var scopes = new[] { RuleScope.File, RuleScope.Project, RuleScope.Workspace, RuleScope.Database };
        return Run(workspace, config, baseline, scopes, null, cancellationToken);
    }

    private AnalysisResult Run(
        WorkspaceModel workspace,
        ArchonConfig config,
        Baseline baseline,
        IReadOnlyList<RuleScope> scopes,
        SourceFile? targetFile,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var produced = new ConcurrentBag<Finding>();
        var skipped = new ConcurrentBag<SkippedRule>();
        var diagnostics = new ConcurrentBag<string>();

        var enabledAnywhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<IRule> active = SelectActive(scopes, config, enabledAnywhere, skipped);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        var pass = new Pass(workspace, config, enabledAnywhere, produced, skipped, cancellationToken);
        Parallel.ForEach(WorkItems(active, workspace, targetFile), parallelOptions, item => Execute(item, pass));

        foreach (string diagnostic in _registry.LoadDiagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        IReadOnlyList<Finding> withIdentity = Fingerprint.Apply(
            produced.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Span.StartLine)
                    .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                    .ThenBy(f => f.Message, StringComparer.Ordinal),
            config.WorkspaceRoot,
            _sources.GetText);

        var reportable = new List<Finding>();
        var baselined = new List<Finding>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suppressionCache = new Dictionary<string, SuppressionIndex>(StringComparer.OrdinalIgnoreCase);

        foreach (Finding finding in withIdentity)
        {
            if (!suppressionCache.TryGetValue(finding.FilePath, out SuppressionIndex? index))
            {
                index = SuppressionIndex.Build(_sources.GetText(finding.FilePath));
                suppressionCache[finding.FilePath] = index;
            }
            if (index.IsSuppressed(finding))
            {
                continue;
            }
            if (baseline.Contains(finding))
            {
                baselined.Add(finding);
                matched.Add(finding.Fingerprint);
                continue;
            }
            reportable.Add(finding);
        }

        IReadOnlyList<BaselineEntry> stale = FindStaleEntries(
            baseline, matched, workspace, config, scopes, targetFile, skipped.Select(s => s.RuleId), pass.Failed.Keys);

        stopwatch.Stop();
        return new AnalysisResult
        {
            Findings = reportable,
            BaselinedFindings = baselined,
            StaleBaselineEntries = stale,
            Skipped = skipped.ToList(),
            Diagnostics = diagnostics.ToList(),
            FilesAnalysed = workspace.Files.Count,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// The rules worth running: in scope, with at least one id some file could see at a level
    /// other than off. Those ids are collected into <paramref name="enabledAnywhere"/>; the
    /// severity a particular finding carries is resolved afterwards against the file it sits in,
    /// because an override block can set it differently per path. Every id not run is recorded
    /// in <paramref name="skipped"/> with the reason.
    /// </summary>
    private List<IRule> SelectActive(
        IReadOnlyList<RuleScope> scopes,
        ArchonConfig config,
        HashSet<string> enabledAnywhere,
        ConcurrentBag<SkippedRule> skipped)
    {
        var active = new List<IRule>();
        foreach (IRule rule in _registry.Rules.Where(r => scopes.Contains(r.Scope)))
        {
            var enabled = new List<RuleDescriptor>();
            foreach (RuleDescriptor descriptor in rule.Descriptors)
            {
                if (!config.IsEnabledAnywhere(descriptor))
                {
                    skipped.Add(new SkippedRule(descriptor.Id, "disabled by configuration"));
                    continue;
                }
                enabledAnywhere.Add(descriptor.Id);
                enabled.Add(descriptor);
            }
            if (enabled.Count == 0)
            {
                continue;
            }
            if (rule.Scope == RuleScope.Database)
            {
                foreach (RuleDescriptor descriptor in enabled)
                {
                    skipped.Add(new SkippedRule(descriptor.Id, "no database connection configured"));
                }
                continue;
            }
            active.Add(rule);
        }
        return active;
    }

    /// <summary>
    /// Baseline entries this run can say no longer match. An entry is judged only where the run
    /// had the means to reproduce it — its rule ran, in a scope this run included, over the file
    /// it names, with the id switched on for that file — so a check of one folder never declares
    /// entries for another folder stale, and a rule that is switched off (at the top level or by
    /// an override block for the file's path) or failed leaves its entries undecided rather than
    /// dropped. A failure is recorded against the rule's first id, so it is looked up through the
    /// rule that owns the entry's id rather than the id alone. An entry whose file no longer
    /// exists is stale whatever ran, since nothing can match it.
    /// </summary>
    private IReadOnlyList<BaselineEntry> FindStaleEntries(
        Baseline baseline,
        HashSet<string> matched,
        WorkspaceModel workspace,
        ArchonConfig config,
        IReadOnlyList<RuleScope> scopes,
        SourceFile? targetFile,
        IEnumerable<string> skippedRuleIds,
        IEnumerable<string> failedRuleIds)
    {
        if (baseline.Count == 0)
        {
            return Array.Empty<BaselineEntry>();
        }

        IEnumerable<SourceFile> covered = targetFile is null ? workspace.Files : new[] { targetFile };
        var coveredFiles = covered.Select(f => config.RelativePathOf(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownFiles = workspace.Files.Select(f => config.RelativePathOf(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = skippedRuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failed = failedRuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A file the run did not cover leaves its entries undecided, unless the file is gone
        // altogether: the workspace never saw it and the disk has no such file either. Workspace
        // membership is checked first because a host may supply text for a file its editor holds.
        bool IsGone(string relativePath) =>
            !knownFiles.Contains(relativePath) && !File.Exists(Path.Combine(config.WorkspaceRoot, relativePath));

        bool RuleRan(string ruleId, string relativePath) =>
            !skipped.Contains(ruleId)
            && _registry.Find(ruleId) is { } registered
            && scopes.Contains(registered.Rule.Scope)
            && !failed.Contains(registered.Rule.PrimaryId())
            && config.SeverityFor(registered.Descriptor, relativePath) != Severity.Off;

        var stale = new List<BaselineEntry>();
        foreach (BaselineEntry entry in baseline.Entries)
        {
            if (matched.Contains(entry.Fingerprint))
            {
                continue;
            }
            string relativePath = Path.IsPathRooted(entry.File)
                ? config.RelativePathOf(entry.File)
                : entry.File.Replace('\\', '/');
            bool isStale = coveredFiles.Contains(relativePath) ? RuleRan(entry.RuleId, relativePath) : IsGone(relativePath);
            if (isStale)
            {
                stale.Add(entry);
            }
        }
        return stale;
    }

    /// <summary>
    /// A rule paired with one file to run it on, or with no file for a rule whose scope is wider
    /// than a single file. Fanning out per file rather than per rule keeps every core busy when
    /// one file-scope rule dominates the run, as a heavy syntax walk over a large repository does.
    /// </summary>
    private sealed record WorkItem(IRule Rule, SourceFile? File);

    private static IEnumerable<WorkItem> WorkItems(IEnumerable<IRule> active, WorkspaceModel workspace, SourceFile? targetFile)
    {
        foreach (IRule rule in active)
        {
            if (rule.Scope != RuleScope.File)
            {
                yield return new WorkItem(rule, null);
                continue;
            }

            IEnumerable<SourceFile> candidates = targetFile is null
                ? workspace.FilesOfLanguage(rule.Language)
                : rule.Language == RuleLanguages.Any || targetFile.Language == rule.Language
                    ? new[] { targetFile }
                    : Array.Empty<SourceFile>();

            foreach (SourceFile file in candidates)
            {
                yield return new WorkItem(rule, file);
            }
        }
    }

    /// <summary>What every work item of one run shares, including the bags its output lands in.</summary>
    private sealed record Pass(
        WorkspaceModel Workspace,
        ArchonConfig Config,
        HashSet<string> EnabledAnywhere,
        ConcurrentBag<Finding> Produced,
        ConcurrentBag<SkippedRule> Skipped,
        CancellationToken CancellationToken)
    {
        /// <summary>
        /// Rules already reported as failed. A rule that throws is reported once, against the
        /// first file it failed on, and its other files still contribute: one malformed file
        /// should cost that file's findings, not every finding the rule would have made elsewhere.
        /// </summary>
        public ConcurrentDictionary<string, string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private void Execute(WorkItem item, Pass pass)
    {
        IRule rule = item.Rule;
        try
        {
            foreach (Finding finding in RunRule(item, pass))
            {
                RegisteredRule? registered = _registry.Find(finding.RuleId);
                if (registered is null || !ReferenceEquals(registered.Rule, rule))
                {
                    pass.Skipped.Add(new SkippedRule(finding.RuleId, $"reported by '{rule.PrimaryId()}' without declaring it"));
                    continue;
                }
                if (!pass.EnabledAnywhere.Contains(finding.RuleId))
                {
                    continue;
                }
                Severity severity = pass.Config.SeverityFor(registered.Descriptor, pass.Config.RelativePathOf(finding.FilePath));
                if (severity == Severity.Off)
                {
                    continue;
                }
                pass.Produced.Add(finding with { Severity = severity, Category = registered.Descriptor.Category });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string where = item.File is null ? "" : $" in {pass.Config.RelativePathOf(item.File.Path)}";
            if (pass.Failed.TryAdd(rule.PrimaryId(), ex.Message))
            {
                pass.Skipped.Add(new SkippedRule(rule.PrimaryId(), $"failed: {ex.Message}{where}"));
            }
        }
    }

    private IEnumerable<Finding> RunRule(WorkItem item, Pass pass)
    {
        if (item.File is null)
        {
            var context = new RuleContext
            {
                Workspace = pass.Workspace,
                Sources = _sources,
                Config = pass.Config,
                IsEnabled = pass.EnabledAnywhere.Contains,
                CancellationToken = pass.CancellationToken
            };
            return item.Rule.Analyze(context).ToList();
        }

        // A file-scope rule is told whether an id is on for the file in hand, so an override block
        // that switches a costly check off for a folder saves the work as well as the report.
        string relativePath = pass.Config.RelativePathOf(item.File.Path);
        var fileContext = new RuleContext
        {
            Workspace = pass.Workspace,
            Sources = _sources,
            Config = pass.Config,
            TargetFile = item.File,
            IsEnabled = ruleId =>
                pass.EnabledAnywhere.Contains(ruleId)
                && _registry.Find(ruleId) is { } registered
                && pass.Config.SeverityFor(registered.Descriptor, relativePath) != Severity.Off,
            CancellationToken = pass.CancellationToken
        };
        return item.Rule.Analyze(fileContext).ToList();
    }
}
