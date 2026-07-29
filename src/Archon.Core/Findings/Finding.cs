using System.Security.Cryptography;
using System.Text;

namespace Archon.Core.Findings;

/// <summary>Effective reporting level of a finding. <c>Off</c> suppresses the rule entirely.</summary>
public enum Severity
{
    Off = 0,
    Hint = 1,
    Information = 2,
    Warning = 3,
    Error = 4
}

/// <summary>A zero-based source region. End positions are exclusive of the final character.</summary>
public readonly record struct SourceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public static SourceSpan Line(int line) => new(line, 0, line, 0);

    public static readonly SourceSpan None = new(0, 0, 0, 0);
}

/// <summary>
/// A single rule result. <see cref="Severity"/> is the effective level after configuration
/// overrides have been applied, not the rule's declared default.
/// </summary>
public sealed record Finding
{
    public required string RuleId { get; init; }

    public required string Message { get; init; }

    public required string FilePath { get; init; }

    public SourceSpan Span { get; init; } = SourceSpan.None;

    public Severity Severity { get; init; } = Severity.Warning;

    public string Category { get; init; } = "general";

    /// <summary>Optional machine-readable sub-classification, surfaced in JSON and SARIF only.</summary>
    public string? Kind { get; init; }

    /// <summary>Populated on demand by an explainer; never produced by a rule itself.</summary>
    public string? Explanation { get; init; }

    /// <summary>Line-independent identity used for baseline matching. Set by the engine.</summary>
    public string Fingerprint { get; init; } = "";
}

/// <summary>
/// Computes finding identities that survive unrelated edits. Line and column are deliberately
/// excluded so that inserting code above a finding does not resurrect it as new.
///
/// A finding is anchored to the text of the line that produced it. An ordinal alone cannot do that
/// job: with three identical findings numbered nought, one and two, fixing the first renumbers the
/// other two, so their fingerprints change and a baseline that had already accepted them reports
/// both as new — the developer is failed by a check for findings they did not touch. Anchoring on
/// the line's own text leaves the survivors alone. An ordinal still separates findings that share
/// a line's text as well as their rule and message, where nothing else tells them apart.
/// </summary>
public static class Fingerprint
{
    /// <summary>Separates the parts of a key, being a character no source line contains.</summary>
    private const string Separator = "";

    public static string Compute(string ruleId, string relativePath, string message, int occurrence) =>
        Compute(ruleId, relativePath, message, anchor: "", occurrence);

    public static string Compute(string ruleId, string relativePath, string message, string anchor, int occurrence)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        string payload = string.Join('\n', ruleId, normalizedPath, message, anchor, occurrence.ToString());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// Assigns fingerprints to a rule's output. <paramref name="sourceText"/> supplies a file's
    /// content so that a finding can be anchored to its line; when it is absent or returns nothing,
    /// the anchor is empty and identity rests on ordinals alone, as it did before.
    /// </summary>
    public static IReadOnlyList<Finding> Apply(
        IEnumerable<Finding> findings,
        string workspaceRoot,
        Func<string, string?>? sourceText = null)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Finding>();

        foreach (Finding finding in findings)
        {
            string relativePath = ToRelative(finding.FilePath, workspaceRoot);
            string anchor = AnchorFor(finding, lines, sourceText);
            string key = string.Concat(
                finding.RuleId, Separator, relativePath, Separator, finding.Message, Separator, anchor);
            occurrences.TryGetValue(key, out int seen);
            occurrences[key] = seen + 1;
            result.Add(finding with
            {
                Fingerprint = Compute(finding.RuleId, relativePath, finding.Message, anchor, seen)
            });
        }
        return result;
    }

    /// <summary>
    /// The trimmed text of the line a finding starts on. It is unaffected by edits elsewhere in the
    /// file, and when the reported line itself changes the finding is genuinely a different one.
    /// </summary>
    private static string AnchorFor(
        Finding finding,
        Dictionary<string, string[]> cache,
        Func<string, string?>? sourceText)
    {
        if (sourceText is null || finding.Span.StartLine < 0)
        {
            return "";
        }
        if (!cache.TryGetValue(finding.FilePath, out string[]? fileLines))
        {
            string? text = sourceText(finding.FilePath);
            fileLines = text is null ? Array.Empty<string>() : text.Split('\n');
            cache[finding.FilePath] = fileLines;
        }
        return finding.Span.StartLine < fileLines.Length
            ? fileLines[finding.Span.StartLine].Trim('\r', ' ', '\t')
            : "";
    }

    public static string ToRelative(string path, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(path))
        {
            return path;
        }
        try
        {
            return Path.GetRelativePath(workspaceRoot, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
