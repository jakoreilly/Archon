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

    public required IReadOnlyList<SkippedRule> Skipped { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }

    public required int FilesAnalysed { get; init; }

    public required long ElapsedMilliseconds { get; init; }

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
    /// </summary>
    public AnalysisResult AnalyseFileInProject(string filePath, ArchonConfig config, Baseline baseline, CancellationToken cancellationToken = default)
    {
        WorkspaceModel workspace = WorkspaceModel.DiscoverProjectOf(filePath, config.WorkspaceRoot, config.EffectiveExcludes());
        SourceFile? target = workspace.Files
            .FirstOrDefault(f => string.Equals(f.Path, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
        return Run(workspace, config, baseline, new[] { RuleScope.File, RuleScope.Project }, target, cancellationToken);
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

        var severities = new Dictionary<string, Severity>(StringComparer.OrdinalIgnoreCase);
        var active = new List<IRule>();

        foreach (IRule rule in _registry.Rules)
        {
            if (!scopes.Contains(rule.Scope))
            {
                continue;
            }

            var enabled = new List<RuleDescriptor>();
            foreach (RuleDescriptor descriptor in rule.Descriptors)
            {
                Severity severity = config.SeverityFor(descriptor);
                severities[descriptor.Id] = severity;
                if (severity == Severity.Off)
                {
                    skipped.Add(new SkippedRule(descriptor.Id, "disabled by configuration"));
                    continue;
                }
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

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        Parallel.ForEach(active, parallelOptions, rule =>
        {
            try
            {
                foreach (Finding finding in RunRule(rule, workspace, config, severities, targetFile, cancellationToken))
                {
                    RegisteredRule? registered = _registry.Find(finding.RuleId);
                    if (registered is null || !ReferenceEquals(registered.Rule, rule))
                    {
                        skipped.Add(new SkippedRule(finding.RuleId, $"reported by '{rule.PrimaryId()}' without declaring it"));
                        continue;
                    }
                    if (!severities.TryGetValue(finding.RuleId, out Severity severity) || severity == Severity.Off)
                    {
                        continue;
                    }
                    produced.Add(finding with { Severity = severity, Category = registered.Descriptor.Category });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped.Add(new SkippedRule(rule.PrimaryId(), $"failed: {ex.Message}"));
            }
        });

        foreach (string diagnostic in _registry.LoadDiagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        IReadOnlyList<Finding> withIdentity = Fingerprint.Apply(
            produced.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Span.StartLine)
                    .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                    .ThenBy(f => f.Message, StringComparer.Ordinal),
            config.WorkspaceRoot);

        var reportable = new List<Finding>();
        var baselined = new List<Finding>();
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
                continue;
            }
            reportable.Add(finding);
        }

        stopwatch.Stop();
        return new AnalysisResult
        {
            Findings = reportable,
            BaselinedFindings = baselined,
            Skipped = skipped.ToList(),
            Diagnostics = diagnostics.ToList(),
            FilesAnalysed = workspace.Files.Count,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
        };
    }

    private IEnumerable<Finding> RunRule(
        IRule rule,
        WorkspaceModel workspace,
        ArchonConfig config,
        IReadOnlyDictionary<string, Severity> severities,
        SourceFile? targetFile,
        CancellationToken cancellationToken)
    {
        bool IsEnabled(string ruleId) =>
            severities.TryGetValue(ruleId, out Severity severity) && severity != Severity.Off;

        if (rule.Scope != RuleScope.File)
        {
            var context = new RuleContext
            {
                Workspace = workspace,
                Sources = _sources,
                Config = config,
                IsEnabled = IsEnabled,
                CancellationToken = cancellationToken
            };
            return rule.Analyze(context).ToList();
        }

        IEnumerable<SourceFile> candidates = targetFile is null
            ? workspace.FilesOfLanguage(rule.Language)
            : rule.Language == RuleLanguages.Any || targetFile.Language == rule.Language
                ? new[] { targetFile }
                : Array.Empty<SourceFile>();

        var findings = new List<Finding>();
        foreach (SourceFile file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new RuleContext
            {
                Workspace = workspace,
                Sources = _sources,
                Config = config,
                TargetFile = file,
                IsEnabled = IsEnabled,
                CancellationToken = cancellationToken
            };
            findings.AddRange(rule.Analyze(context));
        }
        return findings;
    }
}
