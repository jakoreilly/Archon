using System.Text.Json;
using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a configuration key read in code that appears in none of the settings files beside it.
///
/// A key can legitimately come from an environment variable, a secret store or another provider, so
/// every finding is worded as a possibility and never rises above a warning. A receiver counts as
/// configuration only when its declared type, as written, names one of the configuration
/// interfaces; the distinctive lookup methods are matched by name. A key that names a section is
/// satisfied by any key beneath it.
/// </summary>
public sealed class ConfigKeyRule : IRule
{
    public const string Id = "AR0030";

    public const string SettingsUnreadable = "AR0031";

    private const string Category = "configuration";

    private static readonly string[] ConfigurationTypeNames =
    {
        "IConfiguration", "IConfigurationRoot", "IConfigurationSection"
    };

    private static readonly string[] LookupMethods =
    {
        "GetSection", "GetValue", "GetConnectionString"
    };

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            Id,
            "Configuration key not found in settings",
            Category,
            Severity.Warning,
            "A configuration key read in code appears in none of the settings files beside the project."),
        new RuleDescriptor(
            SettingsUnreadable,
            "Settings file could not be read",
            Category,
            Severity.Information,
            "A settings file could not be parsed, so keys could not be checked against it.")
    };

    public RuleScope Scope => RuleScope.Project;

    public string Language => RuleLanguages.CSharp;

    private sealed record KeyUsage(string Key, string FilePath, SourceSpan Span);

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        var findings = new List<Finding>();

        IEnumerable<(string Directory, IReadOnlyList<SourceFile> Files)> groups =
            context.Workspace.Projects.Count > 0
                ? context.Workspace.Projects.Select(p => (p.Directory, p.Files))
                : new[] { (context.Workspace.Root, (IReadOnlyList<SourceFile>)context.Workspace.Files) };

        foreach ((string directory, IReadOnlyList<SourceFile> files) in groups)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var settingsPaths = DiscoverSettings(directory, ExtraSettingsPaths(context)).ToList();
            if (settingsPaths.Count == 0)
            {
                continue;
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string settingsPath in settingsPaths)
            {
                if (!TryFlatten(settingsPath, known, out string? error))
                {
                    if (context.IsEnabled(SettingsUnreadable))
                    {
                        findings.Add(new Finding
                        {
                            RuleId = SettingsUnreadable,
                            FilePath = settingsPath,
                            Kind = "SettingsUnreadable",
                            Span = SourceSpan.None,
                            Message = $"Could not read settings: {error}. Keys were not checked against this file."
                        });
                    }
                }
            }

            if (known.Count == 0 || !context.IsEnabled(Id))
            {
                continue;
            }

            foreach (SourceFile file in files.Where(f => f.Language == RuleLanguages.CSharp))
            {
                ParsedCSharp? parsed = context.Sources.GetCSharp(file.Path);
                if (parsed is null)
                {
                    continue;
                }
                foreach (KeyUsage usage in ExtractUsages(parsed, file.Path))
                {
                    if (IsKnown(usage.Key, known))
                    {
                        continue;
                    }
                    findings.Add(new Finding
                    {
                        RuleId = Id,
                        FilePath = usage.FilePath,
                        Kind = "UnknownConfigurationKey",
                        Span = usage.Span,
                        Message = $"'{usage.Key}' appears in no settings file beside this project; it may be supplied by another provider."
                    });
                }
            }
        }

        return findings;
    }

    private static IReadOnlyList<string> ExtraSettingsPaths(RuleContext context)
    {
        JsonElement? options = context.OptionsFor(Id);
        if (options is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("additionalSettingsFiles", out JsonElement extra) ||
            extra.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        foreach (JsonElement item in extra.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string? value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            paths.Add(Path.IsPathRooted(value) ? value : Path.Combine(context.Workspace.Root, value));
        }
        return paths;
    }

    private static IEnumerable<string> DiscoverSettings(string directory, IReadOnlyList<string> extra)
    {
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "appsettings*.json", SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
        foreach (string path in extra.Where(File.Exists))
        {
            yield return path;
        }
    }

    /// <summary>
    /// Reads a settings document into colon-separated leaf paths, matching how the configuration
    /// system itself addresses nested values.
    /// </summary>
    private static bool TryFlatten(string path, HashSet<string> into, out string? error)
    {
        error = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            Flatten(document.RootElement, "", into);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Flatten(JsonElement element, string prefix, HashSet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}", into);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{index++}", into);
                }
                break;
            default:
                if (prefix.Length > 0)
                {
                    into.Add(prefix);
                }
                break;
        }
    }

    private static bool IsKnown(string key, HashSet<string> known)
    {
        if (known.Contains(key))
        {
            return true;
        }
        string sectionPrefix = key + ":";
        return known.Any(k => k.StartsWith(sectionPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<KeyUsage> ExtractUsages(ParsedCSharp parsed, string filePath)
    {
        Dictionary<string, string> declared = DeclaredTypes.Collect(parsed.Root);

        foreach (ElementAccessExpressionSyntax access in parsed.Root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
        {
            string? receiver = access.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                _ => null
            };
            if (receiver is null ||
                !declared.TryGetValue(receiver, out string? typeText) ||
                !ConfigurationTypeNames.Contains(DeclaredTypes.SimpleName(typeText), StringComparer.Ordinal))
            {
                continue;
            }
            if (TryReadLiteral(access.ArgumentList.Arguments, parsed, out string key, out SourceSpan span))
            {
                yield return new KeyUsage(key, filePath, span);
            }
        }

        foreach (InvocationExpressionSyntax invocation in parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                continue;
            }
            string method = member.Name is GenericNameSyntax generic
                ? generic.Identifier.Text
                : member.Name.Identifier.Text;
            if (!LookupMethods.Contains(method, StringComparer.Ordinal))
            {
                continue;
            }
            if (!TryReadLiteral(invocation.ArgumentList.Arguments, parsed, out string key, out SourceSpan span))
            {
                continue;
            }
            yield return new KeyUsage(
                method == "GetConnectionString" ? $"ConnectionStrings:{key}" : key,
                filePath,
                span);
        }
    }

    private static bool TryReadLiteral<T>(SeparatedSyntaxList<T> arguments, ParsedCSharp parsed, out string key, out SourceSpan span)
        where T : SyntaxNode
    {
        key = "";
        span = SourceSpan.None;
        if (arguments.Count == 0)
        {
            return false;
        }

        ExpressionSyntax? expression = arguments[0] switch
        {
            ArgumentSyntax argument => argument.Expression,
            _ => null
        };
        if (expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return false;
        }

        key = literal.Token.ValueText;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(literal.Span).Span;
        span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character);
        return true;
    }
}
