using Archon.Core.Rules;

namespace ServiceConventionRules;

/// <summary>
/// What the loader looks for. Every non-abstract <see cref="IRulePack"/> in the assembly is
/// found by reflection and constructed, so this type needs a parameterless constructor and
/// nothing else — there is no attribute to apply and no manifest to keep in step.
/// </summary>
public sealed class ServiceConventionRulePack : IRulePack
{
    public string Name => "service.conventions";

    public IEnumerable<IRule> CreateRules()
    {
        yield return new AmbientEnvironmentRule();
        yield return new HardcodedEndpointRule();
        yield return new AsyncContractRule();
    }
}
