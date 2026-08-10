namespace Archon.Core.Explanations;

/// <summary>A pattern in the snippet library that shows what a rule is asking for.</summary>
public sealed record SnippetPointer(string SnippetId, string Title, string Why)
{
    /// <summary>One line, for a console or a tooltip.</summary>
    public string AsProse() => $"{SnippetId} — {Title}: {Why}";
}

/// <summary>
/// Which library pattern answers "what should this look like instead" for a rule. Detection
/// never consults this: it is prose about a finding that has already been produced, so a
/// missing entry costs nothing and no rule's behaviour depends on the table.
/// </summary>
public static class SnippetCatalog
{
    private static readonly SnippetPointer Lifetime = new(
        "PUB-BOOT-04", "Domain service registration",
        "lifetime is a decision, stated per registration and commented where it is not obvious");

    private static readonly SnippetPointer FireAndForget = new(
        "PUB-ERR-06", "Non-critical side effect",
        "the discard is deliberate and the helper catches inside itself, so nothing throws unobserved");

    private static readonly SnippetPointer BackgroundCatch = new(
        "PUB-JOB-02", "Periodic background service",
        "catch and log inside the loop; never rethrow, because an unhandled exception stops the host");

    private static readonly SnippetPointer Projection = new(
        "PUB-DATA-05", "Projection query",
        "select the columns the caller needs into a projection rather than the whole entity");

    private static readonly SnippetPointer BoundSection = new(
        "PUB-CFG-01", "Bind a required section",
        "every external value arrives through IConfiguration and is bound to an options type");

    private static readonly SnippetPointer StructuredLogging = new(
        "PUB-OBS-03", "Structured logging",
        "log through ILogger with named placeholders rather than writing to the console");

    private static readonly SnippetPointer ImportDirectory = new(
        "PUB-FILE-03", "Import directory scan",
        "the directory comes from bound options, so it differs per environment without a code change");

    private static readonly Dictionary<string, SnippetPointer> ByRuleId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AR0002"] = Lifetime, ["AR0003"] = Lifetime, ["AR0004"] = Lifetime,
        ["SVC0001"] = Lifetime,
        ["AR0010"] = FireAndForget, ["AR0011"] = FireAndForget, ["AR0012"] = FireAndForget,
        ["AR0013"] = BackgroundCatch,
        ["AR0023"] = Projection, ["SQ0001"] = Projection,
        ["AR0030"] = BoundSection,
        ["AR0073"] = StructuredLogging,
        ["SVC0003"] = ImportDirectory
    };

    /// <summary>The pattern for a rule, or null when none is mapped. Never throws.</summary>
    public static SnippetPointer? ForRule(string ruleId) =>
        ByRuleId.TryGetValue(ruleId, out SnippetPointer? pointer) ? pointer : null;
}
