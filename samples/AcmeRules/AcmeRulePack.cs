using Archon.Core.Rules;

namespace AcmeRules;

/// <summary>
/// What the loader looks for. Every non-abstract <see cref="IRulePack"/> in the assembly is
/// found by reflection and constructed, so this type needs a parameterless constructor and
/// nothing else — there is no attribute to apply and no manifest to keep in step.
/// </summary>
public sealed class AcmeRulePack : IRulePack
{
    /// <summary>Shown against each rule in the editor's rule list, so findings can be traced to a pack.</summary>
    public string Name => "acme.rules";

    public IEnumerable<IRule> CreateRules()
    {
        yield return new DirectHttpClientRule();
        yield return new ForbiddenNamespaceRule();
    }
}
