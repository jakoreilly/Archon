using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a condition that can never decide anything, and a direct write to the console.
///
/// Only 'if' and ternary conditions are checked for a literal true/false or a self-comparison; loop
/// conditions are never inspected, so 'while (true)' (an intentional infinite loop) and
/// 'do { } while (false)' (a single-pass block used only for its 'break' exits) need no special
/// case. Console usage is flagged everywhere with no per-project exemption, since telling a console
/// entry point from a library file would need reading the owning .csproj's OutputType -- out of
/// reach for a File-scope rule -- which is why its default severity is the lowest available.
/// </summary>
public sealed class LogicHygieneRule : IRule
{
    public const string AlwaysTrueOrFalseCondition = "AR0072";
    public const string ConsoleUsedForOutput = "AR0073";

    private const string Category = "maintainability";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            AlwaysTrueOrFalseCondition,
            "Condition is always true or always false",
            Category,
            Severity.Information,
            "A literal boolean condition or a self-comparison decides nothing; the branch it guards is either dead or unconditional."),
        new RuleDescriptor(
            ConsoleUsedForOutput,
            "Console used directly for output",
            Category,
            Severity.Hint,
            "Console.Write/WriteLine bypasses structured logging. Expected in a project that is itself a console entry point, which this rule cannot tell apart from any other file.")
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
        if (context.IsEnabled(AlwaysTrueOrFalseCondition))
        {
            findings.AddRange(FindAlwaysTrueOrFalse(parsed, file.Path));
        }
        if (context.IsEnabled(ConsoleUsedForOutput))
        {
            findings.AddRange(FindConsoleUsage(parsed, file.Path));
        }
        return findings;
    }

    private IEnumerable<Finding> FindAlwaysTrueOrFalse(ParsedCSharp parsed, string filePath)
    {
        foreach (IfStatementSyntax ifStatement in parsed.Root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (IsBooleanLiteral(ifStatement.Condition, out bool value))
            {
                yield return Create(AlwaysTrueOrFalseCondition, parsed, ifStatement.Condition.Span, filePath, "AlwaysTrueOrFalseCondition",
                    $"This condition is always {value.ToString().ToLowerInvariant()}, so the branch it guards is not really conditional.");
            }
        }

        foreach (ConditionalExpressionSyntax ternary in parsed.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>())
        {
            if (IsBooleanLiteral(ternary.Condition, out bool value))
            {
                yield return Create(AlwaysTrueOrFalseCondition, parsed, ternary.Condition.Span, filePath, "AlwaysTrueOrFalseCondition",
                    $"This condition is always {value.ToString().ToLowerInvariant()}, so one branch of the ternary is unreachable.");
            }
        }

        foreach (BinaryExpressionSyntax binary in parsed.Root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!binary.IsKind(SyntaxKind.EqualsExpression))
            {
                continue;
            }
            if (binary.Left.IsKind(SyntaxKind.IdentifierName) && binary.Right.IsKind(SyntaxKind.IdentifierName) &&
                binary.Left.ToString() == binary.Right.ToString())
            {
                yield return Create(AlwaysTrueOrFalseCondition, parsed, binary.Span, filePath, "AlwaysTrueOrFalseCondition",
                    $"'{binary}' compares an identifier to itself, so it is always true.");
            }
        }
    }

    private static bool IsBooleanLiteral(ExpressionSyntax expression, out bool value)
    {
        value = false;
        if (expression is not LiteralExpressionSyntax literal)
        {
            return false;
        }
        if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            value = true;
            return true;
        }
        if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            value = false;
            return true;
        }
        return false;
    }

    private IEnumerable<Finding> FindConsoleUsage(ParsedCSharp parsed, string filePath)
    {
        foreach (InvocationExpressionSyntax invocation in parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                member.Name.Identifier.Text is not ("Write" or "WriteLine") ||
                !IsConsoleReceiver(member.Expression))
            {
                continue;
            }
            yield return Create(ConsoleUsedForOutput, parsed, invocation.Span, filePath, "ConsoleUsedForOutput",
                "This writes directly to the console; use the project's logger unless this file is itself a console entry point.");
        }
    }

    /// <summary>
    /// Matches 'Console', 'Console.Error' and 'Console.Out' by their trailing name segments, however
    /// deeply the receiver is qualified (e.g. 'System.Console' or 'System.Console.Error'), the same
    /// text-based approach <see cref="DeclaredTypes.SimpleName"/> uses for a written type.
    /// </summary>
    private static bool IsConsoleReceiver(ExpressionSyntax expression)
    {
        string[] segments = expression.ToString().Split('.');
        string last = segments[^1];
        if (last == "Console")
        {
            return true;
        }
        return (last == "Error" || last == "Out") && segments.Length >= 2 && segments[^2] == "Console";
    }

    private static Finding Create(string ruleId, ParsedCSharp parsed, TextSpan span, string filePath, string kind, string message)
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
