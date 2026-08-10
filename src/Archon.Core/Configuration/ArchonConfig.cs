using System.Text.Json;
using System.Text.Json.Serialization;
using Archon.Core.Findings;

namespace Archon.Core.Configuration;

/// <summary>Layer definitions and permitted dependency direction, shared by every layering rule.</summary>
public sealed class LayerConfig
{
    /// <summary><c>denylist</c> forbids only the listed edges; <c>allowlist</c> permits only them.</summary>
    public string Mode { get; set; } = "denylist";

    /// <summary>Layer name to the namespace prefixes that belong to it.</summary>
    public Dictionary<string, List<string>> Layers { get; set; } = new(StringComparer.Ordinal);

    public List<LayerEdge> Deny { get; set; } = new();

    public List<LayerEdge> Allow { get; set; } = new();

    public bool IsConfigured => Layers.Count > 0;
}

/// <summary>A directed dependency between two named layers.</summary>
public sealed class LayerEdge
{
    public string Id { get; set; } = "";

    public string From { get; set; } = "";

    public string To { get; set; } = "";
}

/// <summary>
/// The one configuration document for every surface. The editor, the command line and any other
/// host read the same file, so a finding reported in one place is reported identically in the
/// others; that equivalence is what makes the results trustworthy enough to gate a build on.
/// </summary>
public sealed class ArchonConfig
{
    /// <summary>Rule id to severity name, or <c>off</c>. Also accepts a category name as the key.</summary>
    public Dictionary<string, string> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path globs never analysed, relative to the workspace root.</summary>
    public List<string> Exclude { get; set; } = new();

    public LayerConfig Layers { get; set; } = new();

    /// <summary>Assembly paths providing additional <c>IRulePack</c> implementations.</summary>
    public List<string> RulePacks { get; set; } = new();

    /// <summary>Per-rule option objects, keyed by rule id.</summary>
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Baseline file path relative to the workspace root.</summary>
    public string Baseline { get; set; } = ".archon-baseline.json";

    [JsonIgnore]
    public string WorkspaceRoot { get; set; } = "";

    [JsonIgnore]
    public string? SourcePath { get; set; }

    /// <summary>Session-only severity overrides, applied above the file and taking precedence.</summary>
    [JsonIgnore]
    public Dictionary<string, Severity> SessionOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Top-level keys present in the file that bind to nothing. Deserialization discards unknown
    /// members silently, so they are collected during loading or they cannot be reported at all.
    /// </summary>
    [JsonIgnore]
    public List<string> UnknownKeys { get; } = new();

    public static readonly string[] DefaultExcludes =
    {
        "**/bin/**", "**/obj/**", "**/node_modules/**", "**/.git/**"
    };

    /// <summary>
    /// The severity words a configuration file may use, ordered from most to least severe. This is
    /// the canonical list: <see cref="TryParseSeverity"/> accepts these plus a few aliases, and both
    /// the schema and the validator present these, so the vocabulary is stated in one place rather
    /// than repeated in three that can drift apart.
    /// </summary>
    public static readonly string[] SeverityNames = { "error", "warning", "information", "hint", "off" };

    /// <summary>
    /// Resolves a rule's effective severity. Precedence is session override, then an explicit
    /// rule-id entry, then a category-wide entry, then the rule's declared default.
    /// </summary>
    public Severity SeverityFor(Rules.RuleDescriptor descriptor)
    {
        if (SessionOverrides.TryGetValue(descriptor.Id, out Severity session))
        {
            return session;
        }
        if (Rules.TryGetValue(descriptor.Id, out string? byId) && TryParseSeverity(byId, out Severity fromId))
        {
            return fromId;
        }
        if (Rules.TryGetValue(descriptor.Category, out string? byCategory) && TryParseSeverity(byCategory, out Severity fromCategory))
        {
            return fromCategory;
        }
        return descriptor.DefaultSeverity;
    }

    public JsonElement? OptionFor(string ruleId) =>
        Options.TryGetValue(ruleId, out JsonElement element) ? element : null;

    public static bool TryParseSeverity(string? text, out Severity severity)
    {
        severity = Severity.Warning;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        switch (text.Trim().ToLowerInvariant())
        {
            case "off":
            case "none":
            case "false":
                severity = Severity.Off;
                return true;
            case "hint":
                severity = Severity.Hint;
                return true;
            case "info":
            case "information":
                severity = Severity.Information;
                return true;
            case "warn":
            case "warning":
                severity = Severity.Warning;
                return true;
            case "error":
                severity = Severity.Error;
                return true;
            default:
                return false;
        }
    }

    public IReadOnlyList<string> EffectiveExcludes()
    {
        var combined = new List<string>(DefaultExcludes);
        combined.AddRange(Exclude);
        return combined;
    }
}

/// <summary>
/// Finds and reads the configuration document. A missing file yields defaults rather than an
/// error, so the tool is usable on an unconfigured repository; a malformed file is reported and
/// then also falls back to defaults, because refusing to run would remove every other finding.
/// </summary>
public static class ConfigLoader
{
    public const string FileName = ".archon.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Walks up from a starting directory to locate the nearest configuration file.</summary>
    public static string? Locate(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    public static ArchonConfig Load(string workspaceRoot, out string? error)
    {
        error = null;
        string? path = Locate(workspaceRoot);
        if (path is null)
        {
            return new ArchonConfig { WorkspaceRoot = Path.GetFullPath(workspaceRoot) };
        }

        try
        {
            string json = File.ReadAllText(path);
            ArchonConfig config = JsonSerializer.Deserialize<ArchonConfig>(json, ReadOptions) ?? new ArchonConfig();
            config.SourcePath = path;
            config.WorkspaceRoot = Path.GetDirectoryName(path) ?? Path.GetFullPath(workspaceRoot);
            config.UnknownKeys.AddRange(UnboundKeys(json));
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            error = $"Could not read '{path}': {ex.Message}. Continuing with default settings.";
            return new ArchonConfig { WorkspaceRoot = Path.GetFullPath(workspaceRoot) };
        }
    }

    /// <summary>
    /// Top-level property names that no setting binds to. Reading the document a second time is the
    /// only way to see them: the deserializer's job is to be permissive about a file it does not
    /// fully recognise, and it discards what it cannot place without recording that it did.
    ///
    /// <c>$schema</c> is excluded because an editor puts it there on purpose.
    /// </summary>
    private static IEnumerable<string> UnboundKeys(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException)
        {
            // Unreachable in practice: deserialization has already succeeded on this text. Reported
            // as "nothing unknown" rather than thrown, because a second parse must never be the
            // thing that fails a load the first parse allowed.
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                bool bound = property.NameEquals("$schema")
                    || ConfigSchema.KnownKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase);
                if (!bound)
                {
                    yield return property.Name;
                }
            }
        }
    }
}
