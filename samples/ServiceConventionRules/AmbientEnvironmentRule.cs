using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ServiceConventionRules;

/// <summary>
/// Flags three ways a service reads its environment ambiently instead of being given it: the
/// machine clock, the machine culture, and the process working directory. Each is decided from
/// the member access as written, with no symbol resolution, so a type of your own called
/// DateTime is indistinguishable from the framework's — the same limitation the built-in
/// security rules carry.
/// </summary>
public sealed class AmbientEnvironmentRule : IRule
{
    public const string AmbientClock = "SVC0001";
    public const string AmbientCulture = "SVC0002";
    public const string AmbientWorkingDirectory = "SVC0003";

    private const string Category = "conventions";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            AmbientClock,
            "Ambient machine clock",
            Category,
            Severity.Warning,
            "DateTime.Now, DateTime.Today or DateTimeOffset.Now reads the machine's local clock, so behaviour depends on the host's time zone and cannot be controlled in a test. Inject a clock and work in UTC."),
        new RuleDescriptor(
            AmbientCulture,
            "Ambient machine culture",
            Category,
            Severity.Warning,
            "CultureInfo.CurrentCulture or CurrentUICulture makes parsing and formatting depend on the host's regional settings. Use CultureInfo.InvariantCulture for anything machine-readable."),
        new RuleDescriptor(
            AmbientWorkingDirectory,
            "Ambient working directory",
            Category,
            Severity.Information,
            "Directory.GetCurrentDirectory() resolves against however the process was started, which differs between a service, a test host and a console run. Take the path from configuration.")
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
        if (context.IsEnabled(AmbientClock))
        {
            findings.AddRange(FindAmbientClock(parsed, file.Path));
        }
        if (context.IsEnabled(AmbientCulture))
        {
            findings.AddRange(FindAmbientCulture(parsed, file.Path));
        }
        if (context.IsEnabled(AmbientWorkingDirectory))
        {
            findings.AddRange(FindAmbientWorkingDirectory(parsed, file.Path));
        }
        return findings;
    }

    /// <summary>
    /// Matches DateTime.Now / DateTime.Today / DateTimeOffset.Now written as a member access on
    /// the type name. A receiver that is itself a member access is read by its last name part, so
    /// System.DateTime.Now matches; anything else — a property called Now on an instance, say — is
    /// left alone rather than guessed at.
    /// </summary>
    private IEnumerable<Finding> FindAmbientClock(ParsedCSharp parsed, string filePath)
    {
        foreach (MemberAccessExpressionSyntax access in
                 parsed.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            string member = access.Name.Identifier.Text;
            if (member is not ("Now" or "Today"))
            {
                continue;
            }
            string receiver = ReceiverName(access.Expression);
            if (receiver is not ("DateTime" or "DateTimeOffset"))
            {
                continue;
            }
            if (receiver == "DateTimeOffset" && member == "Today")
            {
                continue;
            }
            yield return Create(AmbientClock, parsed, access.Span, filePath, "AmbientClock",
                $"'{receiver}.{member}' reads the host's local clock; take the time from an injected clock and work in UTC.");
        }
    }

    /// <summary>
    /// Matches CultureInfo.CurrentCulture / CurrentUICulture written as a member access on the
    /// type name, by the same receiver-name rule as <see cref="FindAmbientClock"/>.
    /// </summary>
    private IEnumerable<Finding> FindAmbientCulture(ParsedCSharp parsed, string filePath)
    {
        foreach (MemberAccessExpressionSyntax access in
                 parsed.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            string member = access.Name.Identifier.Text;
            if (member is not ("CurrentCulture" or "CurrentUICulture"))
            {
                continue;
            }
            if (ReceiverName(access.Expression) != "CultureInfo")
            {
                continue;
            }
            yield return Create(AmbientCulture, parsed, access.Span, filePath, "AmbientCulture",
                $"'CultureInfo.{member}' depends on the host's regional settings; use CultureInfo.InvariantCulture for anything machine-readable.");
        }
    }

    /// <summary>Matches Directory.GetCurrentDirectory(), by the same receiver-name rule as above.</summary>
    private IEnumerable<Finding> FindAmbientWorkingDirectory(ParsedCSharp parsed, string filePath)
    {
        foreach (InvocationExpressionSyntax invocation in
                 parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access ||
                access.Name.Identifier.Text != "GetCurrentDirectory")
            {
                continue;
            }
            if (ReceiverName(access.Expression) != "Directory")
            {
                continue;
            }
            yield return Create(AmbientWorkingDirectory, parsed, invocation.Span, filePath, "AmbientWorkingDirectory",
                "'Directory.GetCurrentDirectory()' resolves against however the process was started; take the path from configuration.");
        }
    }

    private static string ReceiverName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => ""
    };

    private static Finding Create(
        string ruleId, ParsedCSharp parsed, TextSpan span, string filePath, string kind, string message)
    {
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = ruleId,
            FilePath = filePath,
            Kind = kind,
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = message
        };
    }
}
