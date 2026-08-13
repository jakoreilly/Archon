using Archon.Core.Configuration;

namespace Archon.Core.Insights;

/// <summary>
/// One baseline entry's debt signal: how long ago it was accepted, and how much the file it sits
/// in has changed since — the same idea as <see cref="HotspotEntry"/>, aimed at suppressions
/// instead of raw complexity. <see cref="Introduced"/> is null when history for it could not be
/// found (a shallow clone, a rewritten baseline file), in which case the entry ranks at the
/// bottom rather than being guessed into looking either old or new.
/// </summary>
public sealed record DebtEntry(string Fingerprint, string RuleId, string File, DateTimeOffset? Introduced, int ChurnCommits, int AgeDays)
{
    public int Score => AgeDays * ChurnCommits;
}

/// <summary>
/// Ranks baseline entries by age multiplied by how much their file has changed since they were
/// accepted. Every suppression has a birthday; this is what makes that birthday visible instead
/// of leaving accepted findings to rot unseen. Pure by design — the git reading and the ranking
/// are deliberately separate, so the ranking rule can be tested without a repository in sight.
/// </summary>
public static class DebtAnalyzer
{
    public static IReadOnlyList<DebtEntry> Rank(
        IEnumerable<BaselineEntry> entries,
        IReadOnlyDictionary<string, DateTimeOffset> introducedByFingerprint,
        IReadOnlyDictionary<string, int> churnByFingerprint,
        DateTimeOffset now)
    {
        var ranked = new List<DebtEntry>();
        foreach (BaselineEntry entry in entries)
        {
            bool hasIntroduced = introducedByFingerprint.TryGetValue(entry.Fingerprint, out DateTimeOffset introducedAt);
            int ageDays = hasIntroduced ? Math.Max(0, (int)(now - introducedAt).TotalDays) : 0;
            int churn = churnByFingerprint.GetValueOrDefault(entry.Fingerprint);
            ranked.Add(new DebtEntry(
                entry.Fingerprint,
                entry.RuleId,
                entry.File,
                hasIntroduced ? introducedAt : null,
                churn,
                ageDays));
        }

        return ranked
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.AgeDays)
            .ThenBy(e => e.File, StringComparer.Ordinal)
            .ThenBy(e => e.RuleId, StringComparer.Ordinal)
            .ToList();
    }
}
