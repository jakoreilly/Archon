using Xunit;

namespace Archon.Tests;

/// <summary>
/// Drives the existing 30 hand-rolled test groups through xUnit, so each is independently
/// discoverable and runnable via `dotnet test` / an IDE Test Explorer, without rewriting any
/// group's body. `Program.cs`'s own `Main` still runs all 30 through the original standalone path.
/// </summary>
public sealed class HarnessGroupTests
{
    public static IEnumerable<object[]> Groups()
    {
        yield return new object[] { "SqlWildcardRules", (Action<Harness>)Program.SqlWildcardRules };
        yield return new object[] { "SqlConventionRules", (Action<Harness>)Program.SqlConventionRules };
        yield return new object[] { "SecurityHotspotRules", (Action<Harness>)Program.SecurityHotspotRules };
        yield return new object[] { "ComplexityRules", (Action<Harness>)Program.ComplexityRules };
        yield return new object[] { "UnusedSymbolsRules", (Action<Harness>)Program.UnusedSymbolsRules };
        yield return new object[] { "LogicHygieneRules", (Action<Harness>)Program.LogicHygieneRules };
        yield return new object[] { "LayerRules", (Action<Harness>)Program.LayerRules };
        yield return new object[] { "LifetimeRules", (Action<Harness>)Program.LifetimeRules };
        yield return new object[] { "AsyncSafetyRules", (Action<Harness>)Program.AsyncSafetyRules };
        yield return new object[] { "PerfHintRules", (Action<Harness>)Program.PerfHintRules };
        yield return new object[] { "ConfigKeyRules", (Action<Harness>)Program.ConfigKeyRules };
        yield return new object[] { "ProjectCycleRules", (Action<Harness>)Program.ProjectCycleRules };
        yield return new object[] { "CallGraphChecks", (Action<Harness>)Program.CallGraphChecks };
        yield return new object[] { "CallGraphMemberChecks", (Action<Harness>)Program.CallGraphMemberChecks };
        yield return new object[] { "SuppressionRules", (Action<Harness>)Program.SuppressionRules };
        yield return new object[] { "BaselineRules", (Action<Harness>)Program.BaselineRules };
        yield return new object[] { "BaselineStabilityRules", (Action<Harness>)Program.BaselineStabilityRules };
        yield return new object[] { "SourceCacheRules", (Action<Harness>)Program.SourceCacheRules };
        yield return new object[] { "ProjectAttributionRules", (Action<Harness>)Program.ProjectAttributionRules };
        yield return new object[] { "ConfigurationRules", (Action<Harness>)Program.ConfigurationRules };
        yield return new object[] { "ScopeRules", (Action<Harness>)Program.ScopeRules };
        yield return new object[] { "RegistryRules", (Action<Harness>)Program.RegistryRules };
        yield return new object[] { "GlobRules", (Action<Harness>)Program.GlobRules };
        yield return new object[] { "SnippetExtractionRules", (Action<Harness>)Program.SnippetExtractionRules };
        yield return new object[] { "SnippetCorpusRules", (Action<Harness>)Program.SnippetCorpusRules };
        yield return new object[] { "ServiceConventionRules", (Action<Harness>)Program.ServiceConventionRules };
        yield return new object[] { "ConventionPackTier2Rules", (Action<Harness>)Program.ConventionPackTier2Rules };
        yield return new object[] { "SnippetCatalogRules", (Action<Harness>)Program.SnippetCatalogRules };
        yield return new object[] { "ConfigValidationRules", (Action<Harness>)Program.ConfigValidationRules };
        yield return new object[] { "ConfigSchemaRules", (Action<Harness>)Program.ConfigSchemaRules };
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void GroupPasses(string name, Action<Harness> run)
    {
        var harness = new Harness();
        run(harness);
        Assert.True(harness.Failures.Count == 0, $"{name}: {string.Join("; ", harness.Failures)}");
    }
}
