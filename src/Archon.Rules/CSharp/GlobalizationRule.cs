using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a parameterless <c>ToUpper()</c>/<c>ToLower()</c> call on a string, which casts case using
/// the current thread's culture. Under a Turkish-family culture in particular, this silently
/// produces a different letter than expected (the "Turkish I" problem: 'i'.ToUpper() is 'İ', not
/// 'I'), which breaks anything compared, stored or looked up afterwards.
///
/// A receiver counts as a string in one of two ways: it is a string literal, or its declared type
/// -- read the same way <see cref="DeclaredTypes"/> already does for AR0021 -- is textually
/// <c>string</c>. A property chain or a method's return value is neither, so it is left alone rather
/// than guessed at, the same limitation AR0021 already carries.
/// </summary>
public sealed class GlobalizationRule : IRule
{
    public const string CultureSensitiveCasing = "AR0090";

    private const string Category = "globalization";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            CultureSensitiveCasing,
            "Culture-sensitive string casing",
            Category,
            Severity.Information,
            "ToUpper()/ToLower() cast case using the current culture, which can silently change letters under some cultures (e.g. Turkish 'i'). Use ToUpperInvariant()/ToLowerInvariant() unless the result is shown to the user in their own culture.")
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

        return context.IsEnabled(CultureSensitiveCasing)
            ? FindCultureSensitiveCasing(parsed, file.Path)
            : Array.Empty<Finding>();
    }

    private IEnumerable<Finding> FindCultureSensitiveCasing(ParsedCSharp parsed, string filePath)
    {
        Dictionary<string, string> declared = DeclaredTypes.Collect(parsed.Root);

        foreach (InvocationExpressionSyntax invocation in parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                member.Name.Identifier.Text is not ("ToUpper" or "ToLower") ||
                invocation.ArgumentList.Arguments.Count != 0)
            {
                continue;
            }
            if (!IsKnownString(member.Expression, declared))
            {
                continue;
            }
            string replacement = member.Name.Identifier.Text == "ToUpper" ? "ToUpperInvariant" : "ToLowerInvariant";
            yield return Create(parsed, invocation.Span, filePath,
                $"'.{member.Name.Identifier.Text}()' casts case using the current culture; prefer '.{replacement}()' unless this is shown to the user in their own culture.");
        }
    }

    private static bool IsKnownString(ExpressionSyntax receiver, Dictionary<string, string> declared)
    {
        if (receiver is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return true;
        }
        string? name = receiver switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            _ => null
        };
        return name is not null && declared.TryGetValue(name, out string? typeText) && DeclaredTypes.SimpleName(typeText) == "string";
    }

    private static Finding Create(ParsedCSharp parsed, TextSpan span, string filePath, string message)
    {
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = CultureSensitiveCasing,
            FilePath = filePath,
            Kind = "CultureSensitiveCasing",
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = message
        };
    }
}
