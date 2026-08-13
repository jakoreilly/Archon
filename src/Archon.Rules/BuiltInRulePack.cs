using Archon.Core.Rules;
using Archon.Rules.CSharp;
using Archon.Rules.Sql;

namespace Archon.Rules;

/// <summary>
/// The rules shipped with the suite. Adding a rule means adding one entry here and one class:
/// there is no separate host, package or launcher per rule, which is what allows the whole set to
/// share one parse of each file and to be configured from one place.
/// </summary>
public sealed class BuiltInRulePack : IRulePack
{
    public string Name => "archon.builtin";

    public IEnumerable<IRule> CreateRules()
    {
        yield return new LayerDependencyRule();
        yield return new CaptiveDependencyRule();
        yield return new AsyncSafetyRule();
        yield return new PerfHintRule();
        yield return new ConfigKeyRule();
        yield return new ProjectCycleRule();
        yield return new SelectStarRule();
        yield return new SqlConventionRule();
        yield return new SecurityHotspotRule();
        yield return new ComplexityRule();
        yield return new UnusedSymbolsRule();
        yield return new LogicHygieneRule();
        yield return new DisposalRule();
        yield return new SchemaAwareSqlRule();
    }
}
