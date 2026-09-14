using Archon.Core.Engine;
using Archon.Core.Findings;

namespace Archon.Core.Insights;

/// <summary>
/// The lines a branch has added or changed, so a check can be confined to what a change touched.
///
/// A full-repository report is the right thing to gate a build on, because it is the whole truth;
/// it is the wrong thing to put in front of someone reviewing a pull request, because most of it
/// is about code they did not write. This answers the narrower question — of the findings, which
/// fall on a line this change wrote? — from the diff alone, without moving any rule off the
/// whole-workspace pass: a workspace-scope rule still sees every file, and only its report is
/// filtered afterwards.
///
/// Lines are zero-based here, matching <see cref="SourceSpan"/>; the diff parser converts from
/// git's one-based hunks at the boundary.
/// </summary>
public sealed class ChangeSet
{
    private readonly Dictionary<string, List<(int Start, int End)>> _ranges;
    private readonly HashSet<string> _wholeFiles;

    private ChangeSet(Dictionary<string, List<(int Start, int End)>> ranges, HashSet<string> wholeFiles)
    {
        _ranges = ranges;
        _wholeFiles = wholeFiles;
    }

    /// <summary>How many files carry at least one changed line.</summary>
    public int FileCount => _ranges.Count + _wholeFiles.Count(f => !_ranges.ContainsKey(f));

    /// <summary>
    /// The working tree's changes since the point a branch diverged from <paramref name="baseRef"/>.
    ///
    /// The comparison is against the merge base rather than against the ref itself, so it shows
    /// what this branch did and not everything that landed on the base since the branch was cut.
    /// Uncommitted edits are included, because a developer running this locally wants to know about
    /// the line they just wrote, and in a pipeline the tree and the head are the same thing.
    /// Untracked files count as wholly changed for the same reason.
    /// </summary>
    public static ChangeSet? Since(string repositoryRoot, string baseRef, out string? error)
    {
        error = null;
        string? mergeBase = GitHistory.MergeBase(repositoryRoot, baseRef);
        if (mergeBase is null)
        {
            error = $"'{baseRef}' is not a ref git can resolve in {repositoryRoot}, or has no common history with HEAD.";
            return null;
        }

        string? diff = GitHistory.DiffSince(repositoryRoot, mergeBase);
        if (diff is null)
        {
            error = $"git diff against {mergeBase[..Math.Min(12, mergeBase.Length)]} failed.";
            return null;
        }

        ChangeSet parsed = Parse(diff);
        foreach (string untracked in GitHistory.UntrackedFiles(repositoryRoot))
        {
            parsed._wholeFiles.Add(untracked);
        }
        return parsed;
    }

    /// <summary>
    /// Reads a unified diff produced with zero context lines. Only the post-image side matters: a
    /// hunk header <c>@@ -a,b +c,d @@</c> says that <c>d</c> lines starting at line <c>c</c> of the
    /// new file were added or rewritten, and those are the lines a finding can be blamed on. A
    /// hunk with <c>d</c> of zero is a pure deletion and marks nothing.
    /// </summary>
    public static ChangeSet Parse(string unifiedDiff)
    {
        var ranges = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        string? currentFile = null;

        foreach (string rawLine in unifiedDiff.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string target = line[4..];
                currentFile = target == "/dev/null" ? null : StripPrefix(target);
                continue;
            }
            if (currentFile is null || !TryParseHunk(line, out int start, out int count) || count == 0)
            {
                continue;
            }

            if (!ranges.TryGetValue(currentFile, out List<(int Start, int End)>? fileRanges))
            {
                fileRanges = new List<(int Start, int End)>();
                ranges[currentFile] = fileRanges;
            }
            fileRanges.Add((start - 1, start - 1 + count - 1));
        }

        return new ChangeSet(ranges, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the post-image part of a hunk header, <c>+c,d</c> in <c>@@ -a,b +c,d @@</c>. The count
    /// is omitted when it is one, which is the common case for a single edited line.
    /// </summary>
    private static bool TryParseHunk(string line, out int start, out int count)
    {
        start = 0;
        count = 1;
        if (!line.StartsWith("@@ ", StringComparison.Ordinal))
        {
            return false;
        }
        int plus = line.IndexOf('+', 3);
        int close = plus < 0 ? -1 : line.IndexOf(" @@", plus, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }
        string[] parts = line[(plus + 1)..close].Split(',');
        if (!int.TryParse(parts[0], out start))
        {
            return false;
        }
        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedCount))
        {
            count = parsedCount;
        }
        return true;
    }

    /// <summary>Whether a line of a repository-relative file was added or changed.</summary>
    public bool Contains(string relativePath, int line)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (_wholeFiles.Contains(normalized))
        {
            return true;
        }
        return _ranges.TryGetValue(normalized, out List<(int Start, int End)>? fileRanges)
            && fileRanges.Any(r => line >= r.Start && line <= r.End);
    }

    /// <summary>Whether any line of a span was added or changed.</summary>
    public bool Intersects(string relativePath, SourceSpan span)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (_wholeFiles.Contains(normalized))
        {
            return true;
        }
        if (!_ranges.TryGetValue(normalized, out List<(int Start, int End)>? fileRanges))
        {
            return false;
        }
        int last = Math.Max(span.StartLine, span.EndLine);
        return fileRanges.Any(r => span.StartLine <= r.End && last >= r.Start);
    }

    /// <summary>
    /// The subset of a result that falls on changed lines. Baselined findings are filtered the
    /// same way, so the counts a report prints stay about the change; everything else about the
    /// result — what ran, what was skipped, how long it took — is unchanged, because it still
    /// happened.
    /// </summary>
    public AnalysisResult Filter(AnalysisResult result, string repositoryRoot, string description)
    {
        bool InChange(Finding finding) =>
            Intersects(Fingerprint.ToRelative(finding.FilePath, repositoryRoot), finding.Span);

        var kept = result.Findings.Where(InChange).ToList();
        return result with
        {
            Findings = kept,
            BaselinedFindings = result.BaselinedFindings.Where(InChange).ToList(),
            Scope = description,
            OutsideScope = result.Findings.Count - kept.Count
        };
    }

    /// <summary>Removes git's <c>b/</c> prefix, present unless the diff was made with <c>--no-prefix</c>.</summary>
    private static string StripPrefix(string path)
    {
        if (path.StartsWith("b/", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        // A path containing a space or a non-ASCII character is quoted and escaped in C style.
        // Unquoting fully is more than this needs; the common cases are handled and the rest
        // simply fail to match a finding, which errs toward reporting less rather than crashing.
        if (path.Length > 1 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return path;
    }
}
