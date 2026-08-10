using System.Text.Json;
using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ServiceConventionRules;

/// <summary>
/// Flags a string literal, in an initialiser or assignment, that is a URL, a UNC path or an IPv4
/// literal — an environment fact written into source rather than read from configuration. A
/// well-known specification or loopback host is exempt, as is a literal inside an attribute
/// argument (a route template, an xmlns) and a literal whose target name reads as a pattern
/// rather than an address. <see cref="AdditionalAllowedHosts"/> extends the exemption list per
/// workspace without a rebuild.
/// </summary>
public sealed class HardcodedEndpointRule : IRule
{
    public const string Id = "SVC0010";

    private const string Category = "conventions";

    private enum EndpointKind
    {
        Url,
        UncPath,
        Ipv4Literal
    }

    /// <summary>
    /// Hosts that name a specification or a machine's own loopback rather than an environment.
    /// Measured by running this rule read-only over tests/fixtures/library and src/: the vendored
    /// corpus produced zero raw hits (its one XML block, 01-bootstrap-and-di.md, is not C# and is
    /// never wrapped or analysed), and src/ produced exactly two — the SARIF schema URL and this
    /// tool's own repository URL in Reporters.cs — neither of which matches a specification or
    /// loopback host, so both are left as genuine findings rather than added here. This list is
    /// therefore the plan's own baseline (w3.org-family specification hosts plus the loopback
    /// addresses), kept as a defensive default for a workspace this measurement did not happen to
    /// exercise; see the phase report for the exact counts.
    /// </summary>
    private static readonly HashSet<string> WellKnownHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "w3.org", "www.w3.org", "schemas.microsoft.com", "schemas.xmlsoap.org", "tempuri.org",
        "localhost", "127.0.0.1", "0.0.0.0", "::1"
    };

    private static readonly string[] ExemptNameSuffixes = { "Pattern", "Template", "Format", "Scheme" };

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            Id,
            "Hardcoded endpoint literal",
            Category,
            Severity.Warning,
            "A string literal is a URL, a UNC path or an IPv4 address. An address that differs between environments belongs in configuration, not in source.")
    };

    public RuleScope Scope => RuleScope.File;

    public string Language => RuleLanguages.CSharp;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        if (context.TargetFile is not SourceFile file)
        {
            return Array.Empty<Finding>();
        }
        ParsedCSharp? parsed = context.Sources.GetCSharp(file.Path);
        if (parsed is null)
        {
            return Array.Empty<Finding>();
        }

        var findings = new List<Finding>();
        if (context.IsEnabled(Id))
        {
            findings.AddRange(FindHardcodedEndpoints(parsed, context, file.Path));
        }
        return findings;
    }

    private IEnumerable<Finding> FindHardcodedEndpoints(ParsedCSharp parsed, RuleContext context, string filePath)
    {
        IReadOnlyList<string> additionalHosts = AdditionalAllowedHosts(context);

        foreach (VariableDeclaratorSyntax declarator in parsed.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is not LiteralExpressionSyntax literal ||
                !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }
            Finding? finding = Evaluate(parsed, literal, declarator.Identifier.Text, additionalHosts, filePath);
            if (finding is not null)
            {
                yield return finding;
            }
        }

        foreach (AssignmentExpressionSyntax assignment in parsed.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                assignment.Right is not LiteralExpressionSyntax literal ||
                !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }
            string name = assignment.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                _ => ""
            };
            Finding? finding = Evaluate(parsed, literal, name, additionalHosts, filePath);
            if (finding is not null)
            {
                yield return finding;
            }
        }
    }

    private Finding? Evaluate(
        ParsedCSharp parsed, LiteralExpressionSyntax literal, string targetName,
        IReadOnlyList<string> additionalHosts, string filePath)
    {
        string text = literal.Token.ValueText;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        EndpointKind? kind = Classify(text);
        if (kind is null)
        {
            return null;
        }
        if (IsInsideAttribute(literal) || IsExemptByName(targetName))
        {
            return null;
        }
        string host = ExtractHost(text, kind.Value);
        if (IsAllowedHost(host, additionalHosts))
        {
            return null;
        }
        string kindLabel = kind.Value switch
        {
            EndpointKind.Url => "URL",
            EndpointKind.UncPath => "UNC path",
            _ => "IPv4 address"
        };
        return Create(parsed, literal.Span, filePath,
            $"'{text}' is a hardcoded {kindLabel}; take the address from configuration so it can differ per environment.");
    }

    private static EndpointKind? Classify(string text)
    {
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointKind.Url;
        }
        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return EndpointKind.UncPath;
        }
        return LooksLikeIPv4(text) ? EndpointKind.Ipv4Literal : null;
    }

    /// <summary>Four dot-separated segments, each one to three digits, each no greater than 255.</summary>
    private static bool LooksLikeIPv4(string text)
    {
        string[] segments = text.Split('.');
        if (segments.Length != 4)
        {
            return false;
        }
        foreach (string segment in segments)
        {
            if (segment.Length is 0 or > 3 || !segment.All(char.IsAsciiDigit))
            {
                return false;
            }
            if (!int.TryParse(segment, out int value) || value > 255)
            {
                return false;
            }
        }
        return true;
    }

    private static string ExtractHost(string text, EndpointKind kind)
    {
        string remainder = kind switch
        {
            EndpointKind.Url => text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..],
            EndpointKind.UncPath => text[2..],
            _ => text
        };
        char[] boundary = kind == EndpointKind.UncPath ? new[] { '\\' } : new[] { '/', ':' };
        int end = remainder.IndexOfAny(boundary);
        return end < 0 ? remainder : remainder[..end];
    }

    private static bool IsInsideAttribute(LiteralExpressionSyntax literal) =>
        literal.FirstAncestorOrSelf<AttributeSyntax>() is not null;

    private static bool IsExemptByName(string targetName) =>
        ExemptNameSuffixes.Any(suffix => targetName.EndsWith(suffix, StringComparison.Ordinal));

    private static bool IsAllowedHost(string host, IReadOnlyList<string> additionalHosts) =>
        WellKnownHosts.Contains(host) || additionalHosts.Any(allowed => string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the workspace's own extensions to the allowed-host list. Never throws.</summary>
    private static IReadOnlyList<string> AdditionalAllowedHosts(RuleContext context)
    {
        JsonElement? options = context.OptionsFor(Id);
        if (options is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("additionalAllowedHosts", out JsonElement extra) ||
            extra.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var hosts = new List<string>();
        foreach (JsonElement item in extra.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string? value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                hosts.Add(value);
            }
        }
        return hosts;
    }

    private static Finding Create(ParsedCSharp parsed, TextSpan span, string filePath, string message)
    {
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = Id,
            FilePath = filePath,
            Kind = "HardcodedEndpoint",
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = message
        };
    }
}
