namespace Archon.Core.Insights;

/// <summary>One file's combined risk signal: how hard it is to follow, times how often it changes.</summary>
public sealed record HotspotEntry(string File, int Complexity, int ChurnCommits)
{
    public int Score => Complexity * ChurnCommits;
}

/// <summary>
/// Ranks files by complexity multiplied by churn. Neither signal alone predicts trouble well:
/// complex code nobody touches is stable, and simple code that changes constantly is easy to
/// review. Files that are both complicated and frequently edited are where a change is most
/// likely to introduce a defect, which is the classic hotspot heuristic. Pure by design, so it
/// can be tested without a git repository or a parsed source file in sight.
/// </summary>
public static class HotspotAnalyzer
{
    public static IReadOnlyList<HotspotEntry> Rank(
        IReadOnlyDictionary<string, int> complexityByFile,
        IReadOnlyDictionary<string, int> churnByFile,
        int top)
    {
        var entries = new List<HotspotEntry>();
        foreach ((string file, int complexity) in complexityByFile)
        {
            if (complexity <= 0)
            {
                continue;
            }
            if (!churnByFile.TryGetValue(file, out int commits) || commits <= 0)
            {
                continue;
            }
            entries.Add(new HotspotEntry(file, complexity, commits));
        }

        return entries
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.Complexity)
            .ThenBy(e => e.File, StringComparer.Ordinal)
            .Take(Math.Max(0, top))
            .ToList();
    }
}
