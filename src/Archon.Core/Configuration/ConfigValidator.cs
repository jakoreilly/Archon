using Archon.Core.Output;
using Archon.Core.Rules;

namespace Archon.Core.Configuration;

/// <summary>
/// Reports configuration entries that will not do what they appear to say.
///
/// Every check here covers something the engine is deliberately permissive about. Resolution
/// treats an unreadable entry as absent — an unknown rule id matches nothing, an unparseable
/// severity falls through to the default, a misspelled layer name matches no edge — because a
/// configuration file that stopped analysis on a typo would be worse than one that ignored it.
/// The cost is that all three failures are invisible: <c>"AR010": "off"</c> reads as switching a
/// rule off and in fact does nothing at all, and nothing said so.
///
/// Validation is therefore separate from resolution rather than built into it. Resolution stays
/// total and silent, and this reports what resolution had to ignore. Callers surface the result
/// as a message and carry on: these are warnings about the configuration, never a refusal to run.
///
/// The registry is a parameter because what counts as a known id depends on which packs loaded.
/// A rule id from an external pack is only spellable after that pack is registered, so validating
/// against the built-in set alone would report every private rule as a mistake.
/// </summary>
public static class ConfigValidator
{
    /// <summary>
    /// The largest edit distance at which a misspelling is reported as a suggestion. Two allows a
    /// transposition or a pair of slips — <c>AR010</c> for <c>AR0010</c>, <c>eror</c> for
    /// <c>error</c> — while staying far enough below the length of a rule id that two genuinely
    /// different ids are never proposed for one another.
    /// </summary>
    private const int MaxSuggestionDistance = 2;

    /// <summary>
    /// Checks a loaded configuration against the rules actually registered. Returns one message per
    /// problem, ordered so that the same file always produces the same list.
    /// </summary>
    public static IReadOnlyList<string> Validate(ArchonConfig config, RuleRegistry registry)
    {
        var messages = new List<string>();

        var ruleIds = registry.Descriptors
            .Select(r => r.Descriptor.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categories = registry.Descriptors
            .Select(r => r.Descriptor.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ValidateUnknownKeys(config, messages);
        ValidateRules(config, registry, ruleIds, categories, messages);
        ValidateOptions(config, ruleIds, messages);
        ValidateLayers(config, messages);
        return messages;
    }

    private static void ValidateUnknownKeys(ArchonConfig config, List<string> messages)
    {
        foreach (string key in config.UnknownKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            string? suggestion = Nearest(key, ConfigSchema.KnownKeys);
            messages.Add(suggestion is null
                ? $"Configuration: '{key}' is not a setting Archon reads, so it is ignored."
                : $"Configuration: '{key}' is not a setting Archon reads — did you mean '{suggestion}'? It is ignored.");
        }
    }

    private static void ValidateRules(
        ArchonConfig config,
        RuleRegistry registry,
        HashSet<string> ruleIds,
        HashSet<string> categories,
        List<string> messages)
    {
        foreach (string key in config.Rules.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            string value = config.Rules[key];
            bool known = ruleIds.Contains(key) || categories.Contains(key);

            if (!known)
            {
                // Rule ids and category names share the one keyspace, so a misspelling is measured
                // against both and the nearer of the two is offered.
                string? suggestion = Nearest(key, ruleIds.Concat(categories));
                messages.Add(suggestion is null
                    ? $"Configuration: '{key}' in \"rules\" is not a known rule id or category, so the entry has no effect."
                    : $"Configuration: '{key}' in \"rules\" is not a known rule id or category — did you mean '{suggestion}'? The entry has no effect.");
                continue;
            }

            if (ArchonConfig.TryParseSeverity(value, out _))
            {
                continue;
            }

            // The key resolves, so the consequence is precise enough to state: this rule, or every
            // rule in this category, keeps the severity it would have had with no entry at all.
            string? severitySuggestion = Nearest(value, ArchonConfig.SeverityNames);
            string effect = ruleIds.Contains(key) && registry.Find(key) is { } registered
                ? $"'{key}' keeps its default of {Reporter.Label(registered.Descriptor.DefaultSeverity)}"
                : $"the '{key}' category keeps each rule's default";
            string didYouMean = severitySuggestion is null ? "" : $" Did you mean '{severitySuggestion}'?";
            messages.Add(
                $"Configuration: \"rules\" entry '{key}' has the value '{value}', which is not a severity."
                + $"{didYouMean} Use one of {string.Join(", ", ArchonConfig.SeverityNames)}. The entry has no effect and {effect}.");
        }
    }

    private static void ValidateOptions(ArchonConfig config, HashSet<string> ruleIds, List<string> messages)
    {
        foreach (string key in config.Options.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (ruleIds.Contains(key))
            {
                continue;
            }
            string? suggestion = Nearest(key, ruleIds);
            messages.Add(suggestion is null
                ? $"Configuration: \"options\" has an entry for '{key}', which is not a known rule id, so it is never read."
                : $"Configuration: \"options\" has an entry for '{key}', which is not a known rule id — did you mean '{suggestion}'? It is never read.");
        }
    }

    private static void ValidateLayers(ArchonConfig config, List<string> messages)
    {
        LayerConfig layers = config.Layers;
        if (!layers.IsConfigured)
        {
            // Layer rules stay silent until layers are declared, so edges without layers are not a
            // half-configured state to warn about — except that the edges themselves cannot work.
            if (layers.Deny.Count > 0 || layers.Allow.Count > 0)
            {
                messages.Add(
                    "Configuration: \"layers\" declares dependency edges but no layers, so no file "
                    + "belongs to a layer and AR0001 stays silent. Add a \"layers\" map of layer name to namespace prefixes.");
            }
            return;
        }

        bool allowlist = string.Equals(layers.Mode, "allowlist", StringComparison.OrdinalIgnoreCase);
        bool denylist = string.Equals(layers.Mode, "denylist", StringComparison.OrdinalIgnoreCase);

        // Anything unrecognised resolves to denylist. That default is the permissive one, so a
        // misspelling of "allowlist" quietly turns "permit only what is listed" into "forbid only
        // what is listed" — every unlisted edge flips from reported to allowed.
        if (!allowlist && !denylist)
        {
            messages.Add(
                $"Configuration: \"layers\".mode is '{layers.Mode}', which is neither 'denylist' nor 'allowlist'. "
                + "It is being treated as denylist, which forbids only the listed edges rather than permitting only them.");
        }

        List<LayerEdge> active = allowlist ? layers.Allow : layers.Deny;
        List<LayerEdge> ignored = allowlist ? layers.Deny : layers.Allow;
        string activeName = allowlist ? "allow" : "deny";
        string ignoredName = allowlist ? "deny" : "allow";

        if (ignored.Count > 0)
        {
            messages.Add(
                $"Configuration: \"layers\".mode is {(allowlist ? "allowlist" : "denylist")}, so the "
                + $"{ignored.Count} entr{(ignored.Count == 1 ? "y" : "ies")} in \"{ignoredName}\" are never read.");
        }

        // Layer names are matched with ordinal equality, so case is significant here in a way it is
        // not for rule ids. 'domain' against a declared 'Domain' matches no edge and reports nothing.
        foreach (LayerEdge edge in active)
        {
            CheckEndpoint(edge, edge.From, "from", activeName, layers, messages);
            CheckEndpoint(edge, edge.To, "to", activeName, layers, messages);
        }
    }

    private static void CheckEndpoint(
        LayerEdge edge,
        string endpoint,
        string endpointName,
        string listName,
        LayerConfig layers,
        List<string> messages)
    {
        if (layers.Layers.ContainsKey(endpoint))
        {
            return;
        }

        string declared = string.Join(", ", layers.Layers.Keys.OrderBy(k => k, StringComparer.Ordinal));
        string label = string.IsNullOrWhiteSpace(edge.Id) ? $"{edge.From}->{edge.To}" : edge.Id;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            messages.Add(
                $"Configuration: layer edge '{label}' in \"{listName}\" has no \"{endpointName}\" layer, so it never matches. "
                + $"Declared layers are {declared}.");
            return;
        }

        string? caseMatch = layers.Layers.Keys
            .FirstOrDefault(k => string.Equals(k, endpoint, StringComparison.OrdinalIgnoreCase));
        string hint = caseMatch is not null
            ? $" Layer names are case-sensitive; the declared layer is '{caseMatch}'."
            : $" Declared layers are {declared}.";

        messages.Add(
            $"Configuration: layer edge '{label}' in \"{listName}\" names '{endpoint}' as its \"{endpointName}\" layer, "
            + $"which is not declared, so the edge never matches.{hint}");
    }

    /// <summary>
    /// The closest candidate within <see cref="MaxSuggestionDistance"/> edits, or null when nothing
    /// is near enough to propose. Ties are broken alphabetically so the message is reproducible.
    /// </summary>
    private static string? Nearest(string value, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string candidate in candidates.OrderBy(c => c, StringComparer.Ordinal))
        {
            int distance = Distance(value, candidate, MaxSuggestionDistance);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return bestDistance <= MaxSuggestionDistance ? best : null;
    }

    /// <summary>
    /// Levenshtein distance, abandoned once every cell of a row exceeds <paramref name="limit"/>.
    /// Returning early matters because this runs against every registered id for each bad key, and
    /// a distance far beyond the limit is not worth computing exactly.
    /// </summary>
    private static int Distance(string left, string right, int limit)
    {
        if (Math.Abs(left.Length - right.Length) > limit)
        {
            return int.MaxValue;
        }

        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            int rowBest = current[0];
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }
            if (rowBest > limit)
            {
                return int.MaxValue;
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }
}
