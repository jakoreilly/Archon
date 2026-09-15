using System.Text.Json;
using System.Text.Json.Serialization;
using Archon.Core.Findings;
using Archon.Core.Sources;

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
/// Settings that apply only to files matching a set of globs, layered over the top-level ones.
/// A test project legitimately writes to the console and repeats string literals; a generated
/// folder should not be held to a complexity threshold. Without this the only options were to
/// switch a rule off everywhere or to exclude the files from every rule, and both throw away
/// findings that were wanted.
/// </summary>
public sealed class ConfigOverride
{
    private GlobMatcher? _matcher;

    /// <summary>Path globs, relative to the workspace root, selecting the files this block applies to.</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>Rule id or category to severity, exactly as the top-level <c>rules</c> map.</summary>
    public Dictionary<string, string> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-rule option objects replacing the top-level entry for matching files.</summary>
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a workspace-relative, forward-slash path is selected by <see cref="Files"/>.</summary>
    public bool Matches(string relativePath)
    {
        // Built on first use rather than at load, because the loader is deserialization and cannot
        // run code; built once, because this is consulted for every finding of every pass.
        _matcher ??= new GlobMatcher(Files);
        return _matcher.IsExcluded(relativePath);
    }
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

    /// <summary>
    /// Path-scoped settings, applied in order so that a later block wins over an earlier one where
    /// both match the same file.
    /// </summary>
    public List<ConfigOverride> Overrides { get; set; } = new();

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
    /// Resolves a rule's effective severity outside any particular file. Precedence is session
    /// override, then an explicit rule-id entry, then a category-wide entry, then the rule's
    /// declared default.
    /// </summary>
    public Severity SeverityFor(Rules.RuleDescriptor descriptor) => SeverityFor(descriptor, relativePath: null);

    /// <summary>
    /// Resolves a rule's effective severity for one file. A session override wins outright; then
    /// each matching <see cref="Overrides"/> block is consulted from last to first, its rule-id entry
    /// before its category entry; then the top-level map the same way; then the declared default.
    /// A block's category entry therefore beats a top-level rule-id entry — the block is the more
    /// specific statement, having named the files — and within any one level an id beats a category.
    /// </summary>
    public Severity SeverityFor(Rules.RuleDescriptor descriptor, string? relativePath)
    {
        if (SessionOverrides.TryGetValue(descriptor.Id, out Severity session))
        {
            return session;
        }
        if (relativePath is not null)
        {
            string normalized = relativePath.Replace('\\', '/');
            for (int i = Overrides.Count - 1; i >= 0; i--)
            {
                if (Overrides[i].Matches(normalized) && TryResolve(Overrides[i].Rules, descriptor, out Severity fromOverride))
                {
                    return fromOverride;
                }
            }
        }
        return TryResolve(Rules, descriptor, out Severity fromRules) ? fromRules : descriptor.DefaultSeverity;
    }

    /// <summary>
    /// Whether any file could see this rule at a level other than off. A rule switched off at the
    /// top level but on for one folder still has to run; a rule off everywhere can be skipped
    /// before it costs anything.
    /// </summary>
    public bool IsEnabledAnywhere(Rules.RuleDescriptor descriptor)
    {
        if (SeverityFor(descriptor) != Severity.Off)
        {
            return true;
        }
        if (SessionOverrides.ContainsKey(descriptor.Id))
        {
            return false;
        }
        return Overrides.Any(o => TryResolve(o.Rules, descriptor, out Severity severity) && severity != Severity.Off);
    }

    private static bool TryResolve(Dictionary<string, string> rules, Rules.RuleDescriptor descriptor, out Severity severity)
    {
        if (rules.TryGetValue(descriptor.Id, out string? byId) && TryParseSeverity(byId, out severity))
        {
            return true;
        }
        if (rules.TryGetValue(descriptor.Category, out string? byCategory) && TryParseSeverity(byCategory, out severity))
        {
            return true;
        }
        severity = Severity.Off;
        return false;
    }

    public JsonElement? OptionFor(string ruleId) => OptionFor(ruleId, relativePath: null);

    /// <summary>
    /// The options a rule reads for one file: the last matching override block that carries an
    /// entry for the rule, else the top-level entry, else nothing. A block replaces the entry whole
    /// rather than merging keys into it, so what a rule reads is always something someone wrote.
    /// </summary>
    public JsonElement? OptionFor(string ruleId, string? relativePath)
    {
        if (relativePath is not null)
        {
            string normalized = relativePath.Replace('\\', '/');
            for (int i = Overrides.Count - 1; i >= 0; i--)
            {
                if (Overrides[i].Matches(normalized) && Overrides[i].Options.TryGetValue(ruleId, out JsonElement fromOverride))
                {
                    return fromOverride;
                }
            }
        }
        return Options.TryGetValue(ruleId, out JsonElement element) ? element : null;
    }

    /// <summary>The workspace-relative, forward-slash form of a path, as override globs are written.</summary>
    public string RelativePathOf(string filePath) =>
        Fingerprint.ToRelative(filePath, WorkspaceRoot).Replace('\\', '/');

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
