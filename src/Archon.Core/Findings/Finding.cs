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
/// excluded so that inserting code above a finding does not resurrect it as new; repeated
/// identical findings in one file are separated by an occurrence ordinal instead.
/// </summary>
public static class Fingerprint
{
    public static string Compute(string ruleId, string relativePath, string message, int occurrence)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        string payload = string.Join('\n', ruleId, normalizedPath, message, occurrence.ToString());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>Assigns fingerprints to a rule's output, numbering duplicates in emission order.</summary>
    public static IReadOnlyList<Finding> Apply(IEnumerable<Finding> findings, string workspaceRoot)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<Finding>();
        foreach (Finding finding in findings)
        {
            string relativePath = ToRelative(finding.FilePath, workspaceRoot);
            string key = string.Concat(finding.RuleId, "", relativePath, "", finding.Message);
            occurrences.TryGetValue(key, out int seen);
            occurrences[key] = seen + 1;
            result.Add(finding with
            {
                Fingerprint = Compute(finding.RuleId, relativePath, finding.Message, seen)
            });
        }
        return result;
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
