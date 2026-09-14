using Xunit;

namespace Archon.Tests;

/// <summary>
/// Drives the hand-rolled test groups through xUnit, so each is independently discoverable and
/// runnable via `dotnet test` or an IDE Test Explorer, without rewriting any group's body. The
/// groups come from <see cref="Program.Groups"/>, the same list the standalone `Main` runs, so
/// the two runners cannot cover different sets; <see cref="EveryGroupIsListed"/> fails when a
/// group method exists that neither runner would reach.
/// </summary>
public sealed class HarnessGroupTests
{
    public static IEnumerable<object[]> Groups() =>
        Program.Groups.Select(group => new object[] { group.Name, group.Run });

    [Theory]
    [MemberData(nameof(Groups))]
    public void GroupPasses(string name, Action<Harness> run)
    {
        var harness = new Harness();
        run(harness);
        Assert.True(harness.Failures.Count == 0, $"{name}: {string.Join("; ", harness.Failures)}");
    }

    [Fact]
    public void EveryGroupIsListed() =>
        Assert.Empty(Program.UnlistedGroups());
}
