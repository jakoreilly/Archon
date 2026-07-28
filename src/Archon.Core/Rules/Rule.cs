using Archon.Core.Configuration;
using Archon.Core.Findings;
using Archon.Core.Sources;

namespace Archon.Core.Rules;

/// <summary>
/// The inputs a rule needs. The engine uses this to decide when a rule must re-run: saving a
/// file re-runs only <see cref="File"/> rules, while wider scopes run on request or in a full
/// pass. A rule declaring more scope than it needs is the main source of avoidable latency.
/// </summary>
public enum RuleScope
{
    /// <summary>Decidable from a single file's own text.</summary>
    File = 0,

    /// <summary>Needs the files of one project.</summary>
    Project = 1,

    /// <summary>Needs every file in the workspace.</summary>
    Workspace = 2,

    /// <summary>Needs a live database connection and is skipped when none is configured.</summary>
    Database = 3
}

/// <summary>Source languages a rule can consume.</summary>
public static class RuleLanguages
{
    public const string CSharp = "csharp";
    public const string Sql = "sql";
    public const string Any = "*";
}

/// <summary>
/// One separately reportable condition, and the unit that configuration, suppression and
/// baselines address. A rule that detects several materially different problems declares one
/// descriptor per problem, so each can carry its own severity and be turned off on its own
/// rather than forcing a single setting onto findings of unequal importance.
/// </summary>
public sealed record RuleDescriptor(
    string Id,
    string Title,
    string Category,
    Severity DefaultSeverity,
    string Description);

/// <summary>
/// Everything a rule may read during one analysis pass. <see cref="TargetFile"/> is set for
/// <see cref="RuleScope.File"/> rules; wider scopes read <see cref="Workspace"/> instead.
/// </summary>
public sealed class RuleContext
{
    public required WorkspaceModel Workspace { get; init; }

    public required SourceCache Sources { get; init; }

    public required ArchonConfig Config { get; init; }

    public SourceFile? TargetFile { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Whether a reported id is switched on for this pass. A rule with an optional and costly
    /// check consults this first so that a disabled condition costs nothing, rather than being
    /// computed and then discarded.
    /// </summary>
    public required Func<string, bool> IsEnabled { get; init; }

    /// <summary>Returns the rule's own options object, or <c>null</c> when unconfigured.</summary>
    public System.Text.Json.JsonElement? OptionsFor(string ruleId) => Config.OptionFor(ruleId);
}

/// <summary>
/// One unit of detection. Implementations must be stateless and safe to call concurrently: the
/// engine may run many rules over the same cached parse trees in parallel. A rule reports only
/// ids it has declared in <see cref="Descriptors"/>; anything else is dropped by the engine.
/// </summary>
public interface IRule
{
    /// <summary>The conditions this rule can report. The first is its primary identity.</summary>
    IReadOnlyList<RuleDescriptor> Descriptors { get; }

    RuleScope Scope { get; }

    /// <summary>One of the <see cref="RuleLanguages"/> values.</summary>
    string Language { get; }

    IEnumerable<Finding> Analyze(RuleContext context);
}

/// <summary>Convenience accessors over a rule's primary descriptor.</summary>
public static class RuleExtensions
{
    public static RuleDescriptor Primary(this IRule rule) => rule.Descriptors[0];

    public static string PrimaryId(this IRule rule) => rule.Descriptors[0].Id;
}

/// <summary>A named group of rules loaded as a unit.</summary>
public interface IRulePack
{
    string Name { get; }

    IEnumerable<IRule> CreateRules();
}
