using System.Text.Json;
using System.Text.Json.Serialization;
using Archon.Core.Findings;

namespace Archon.Core.Configuration;

/// <summary>One recorded finding, retained in readable form so a baseline diff is reviewable.</summary>
public sealed class BaselineEntry
{
    public string Fingerprint { get; set; } = "";

    public string RuleId { get; set; } = "";

    public string File { get; set; } = "";

    public string Message { get; set; } = "";
}

/// <summary>
/// The set of findings a repository has agreed to accept for now. Analysis still reports them,
/// but they do not fail a build, so a large existing codebase can adopt a rule immediately and
/// hold the line against new violations instead of facing thousands of results and switching the
/// rule off. Entries are matched by fingerprint, which excludes line numbers and therefore
/// survives edits elsewhere in the file.
/// </summary>
public sealed class Baseline
{
    private readonly HashSet<string> _fingerprints;

    public Baseline(IEnumerable<BaselineEntry> entries)
    {
        Entries = entries.ToList();
        _fingerprints = Entries.Select(e => e.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<BaselineEntry> Entries { get; }

    public int Count => _fingerprints.Count;

    public static readonly Baseline Empty = new(Array.Empty<BaselineEntry>());

    public bool Contains(Finding finding) => _fingerprints.Contains(finding.Fingerprint);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Reads a baseline, treating an absent or unreadable file as an empty one.</summary>
    public static Baseline Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            return Empty;
        }
        try
        {
            var entries = JsonSerializer.Deserialize<List<BaselineEntry>>(File.ReadAllText(path), Options);
            return new Baseline(entries ?? new List<BaselineEntry>());
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            error = $"Could not read baseline '{path}': {ex.Message}. Treating it as empty.";
            return Empty;
        }
    }

    public static void Save(string path, IEnumerable<Finding> findings, string workspaceRoot)
    {
        var entries = findings
            .Select(f => new BaselineEntry
            {
                Fingerprint = f.Fingerprint,
                RuleId = f.RuleId,
                File = Fingerprint.ToRelative(f.FilePath, workspaceRoot).Replace('\\', '/'),
                Message = f.Message
            })
            .OrderBy(e => e.File, StringComparer.Ordinal)
            .ThenBy(e => e.RuleId, StringComparer.Ordinal)
            .ThenBy(e => e.Fingerprint, StringComparer.Ordinal)
            .ToList();

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Options));
    }
}
