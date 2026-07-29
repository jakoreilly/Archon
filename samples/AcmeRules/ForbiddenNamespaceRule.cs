using System.Text.Json;
using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AcmeRules;

/// <summary>
/// A configurable rule, and one that reports two separate conditions.
///
/// Two descriptors rather than one, because "you used a banned namespace" and "your ban list is
/// malformed" are problems of unequal importance: a team wants the first as an error and the
/// second as information, and one shared severity would force them together. Each id can be
/// configured, suppressed and baselined on its own.
/// </summary>
public sealed class ForbiddenNamespaceRule : IRule
{
    public const string Id = "ACME0002";

    /// <summary>Reported when the rule's own options cannot be read.</summary>
    public const string MalformedOptions = "ACME0003";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            Id,
            "Forbidden namespace imported",
            "architecture",
            Severity.Warning,
            "A file imports a namespace this repository has decided not to depend on. Configure the "
                + "list under options.ACME0002.namespaces."),
        new RuleDescriptor(
            MalformedOptions,
            "Forbidden namespace list could not be read",
            "architecture",
            Severity.Information,
            "options.ACME0002.namespaces is present but is not an array of strings, so nothing was "
                + "checked against it.")
    };

    public RuleScope Scope => RuleScope.File;

    public string Language => RuleLanguages.CSharp;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        if (context.TargetFile is not { } file)
        {
            yield break;
        }

        (IReadOnlyList<string> forbidden, bool malformed) = Configured(context);

        if (malformed && context.IsEnabled(MalformedOptions))
        {
            yield return new Finding
            {
                RuleId = MalformedOptions,
                Message = "options.ACME0002.namespaces must be an array of strings.",
                FilePath = file.Path
            };
        }

        // Nothing configured means nothing to check. A rule with no inputs stays silent rather
        // than inventing a default, in the same way the layering rule does.
        if (forbidden.Count == 0 || !context.IsEnabled(Id))
        {
            yield break;
        }

        if (context.Sources.GetCSharp(file.Path) is not { } parsed)
        {
            yield break;
        }

        foreach (UsingDirectiveSyntax directive in parsed.Root
                     .DescendantNodes()
                     .OfType<UsingDirectiveSyntax>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            string? imported = directive.Name?.ToString();
            if (imported is null)
            {
                continue;
            }

            string? matched = forbidden.FirstOrDefault(f =>
                imported.Equals(f, StringComparison.Ordinal) ||
                imported.StartsWith(f + ".", StringComparison.Ordinal));

            if (matched is null)
            {
                continue;
            }

            yield return new Finding
            {
                RuleId = Id,
                Message = $"'{imported}' is under the forbidden namespace '{matched}'.",
                FilePath = file.Path,
                Span = DirectHttpClientRule.SpanOf(parsed, directive)
            };
        }
    }

    /// <summary>
    /// Reads this rule's own options object from <c>.archon.json</c>. A rule is handed only its
    /// own entry, so it cannot read another rule's settings by accident.
    /// </summary>
    private static (IReadOnlyList<string> Namespaces, bool Malformed) Configured(RuleContext context)
    {
        if (context.OptionsFor(Id) is not { ValueKind: JsonValueKind.Object } options)
        {
            return (Array.Empty<string>(), false);
        }
        if (!options.TryGetProperty("namespaces", out JsonElement configured))
        {
            return (Array.Empty<string>(), false);
        }
        if (configured.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<string>(), true);
        }

        var names = new List<string>();
        foreach (JsonElement entry in configured.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                return (Array.Empty<string>(), true);
            }
            string? value = entry.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                names.Add(value);
            }
        }
        return (names, false);
    }
}
