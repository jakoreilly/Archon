using Archon.Core.Configuration;

namespace Archon.Core.Insights;

/// <summary>The baseline's content as it stood at one commit that touched it.</summary>
public sealed record BaselineSnapshot(string CommitHash, DateTimeOffset When, IReadOnlyList<BaselineEntry> Entries);

/// <summary>One point on the trend: a baseline revision's totals and its change from the one before.</summary>
public sealed record TrendPoint(
    string CommitHash,
    DateTimeOffset When,
    int Total,
    IReadOnlyDictionary<string, int> ByRule,
    int DeltaFromPrevious);

/// <summary>
/// Turns a baseline file's own git history into a time series of finding counts. The baseline
/// already lives in git, so its history is a record of the codebase's accepted-debt trend with no
/// extra storage: a rule's count climbing release over release is visible the same way a rule's
/// count today is. Pure by design, so the series computation can be tested without git in sight —
/// only reading historical revisions needs one.
/// </summary>
public static class TrendAnalyzer
{
    /// <param name="snapshotsOldestFirst">Baseline revisions in chronological order.</param>
    public static IReadOnlyList<TrendPoint> Summarize(IEnumerable<BaselineSnapshot> snapshotsOldestFirst)
    {
        var points = new List<TrendPoint>();
        int? previousTotal = null;
        foreach (BaselineSnapshot snapshot in snapshotsOldestFirst)
        {
            Dictionary<string, int> byRule = snapshot.Entries
                .GroupBy(e => e.RuleId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            int total = snapshot.Entries.Count;
            int delta = previousTotal is null ? 0 : total - previousTotal.Value;
            points.Add(new TrendPoint(snapshot.CommitHash, snapshot.When, total, byRule, delta));
            previousTotal = total;
        }
        return points;
    }
}
