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
/// Flags a method whose control flow is hard to hold in mind, and a string literal repeated often
/// enough that an edit to one copy can silently miss the rest.
///
/// Cognitive complexity here is a syntactic approximation inspired by Sonar's S3776, not a
/// reimplementation of it: nesting increases one level per if/loop/switch/catch body entered, each
/// if/else if/switch/loop/catch/ternary adds one plus the current nesting, a bare else adds one with
/// no nesting bonus, a maximal run of like logical operators (&amp;&amp;/||) in one expression adds
/// one per operator-type change, and a method calling its own name adds one, once, however many
/// times it recurses. Loop *conditions* are never scored, so 'while (true)' needs no special case.
/// </summary>
public sealed class ComplexityRule : IRule
{
    public const string CognitiveComplexity = "AR0060";
    public const string DuplicatedStringLiteral = "AR0061";

    private const string Category = "complexity";
    private const int DefaultThreshold = 15;
    private const int DefaultMinLength = 10;
    private const int DefaultMinOccurrences = 3;

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            CognitiveComplexity,
            "Method is hard to follow",
            Category,
            Severity.Warning,
            "A method's nested and branching control flow has crossed the configured threshold."),
        new RuleDescriptor(
            DuplicatedStringLiteral,
            "String literal repeated in this file",
            Category,
            Severity.Information,
            "The same non-trivial string literal appears several times; an edit to one copy can silently miss the rest.")
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
        if (context.IsEnabled(CognitiveComplexity))
        {
            int threshold = ReadInt(context, CognitiveComplexity, "threshold", DefaultThreshold);
            findings.AddRange(FindComplexMethods(parsed, file.Path, threshold));
        }
        if (context.IsEnabled(DuplicatedStringLiteral))
        {
            int minLength = ReadInt(context, DuplicatedStringLiteral, "minLength", DefaultMinLength);
            int minOccurrences = ReadInt(context, DuplicatedStringLiteral, "minOccurrences", DefaultMinOccurrences);
            findings.AddRange(FindDuplicatedLiterals(parsed, file.Path, minLength, minOccurrences));
        }
        return findings;
    }

    private static int ReadInt(RuleContext context, string ruleId, string property, int fallback)
    {
        JsonElement? options = context.OptionsFor(ruleId);
        if (options is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int parsedValue))
        {
            return parsedValue;
        }
        return fallback;
    }

    private IEnumerable<Finding> FindComplexMethods(ParsedCSharp parsed, string filePath, int threshold)
    {
        foreach (MethodDeclarationSyntax method in parsed.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!TryScoreMethod(method, out int score))
            {
                continue;
            }
            if (score <= threshold)
            {
                continue;
            }
            yield return Create(CognitiveComplexity, parsed, method.Identifier.Span, filePath, "CognitiveComplexity",
                $"'{method.Identifier.Text}' has a cognitive complexity of {score}, over the threshold of {threshold}. Extract named helpers for its branches.");
        }
    }

    /// <summary>
    /// Sum of every method's cognitive complexity in a file, unfiltered by the reporting
    /// threshold. Used by hotspot ranking, which cares about a file's total shape rather than
    /// which individual methods happen to cross a configured line.
    /// </summary>
    public static int ScoreFile(ParsedCSharp parsed)
    {
        int total = 0;
        foreach (MethodDeclarationSyntax method in parsed.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (TryScoreMethod(method, out int score))
            {
                total += score;
            }
        }
        return total;
    }

    private static bool TryScoreMethod(MethodDeclarationSyntax method, out int score)
    {
        if (method.Body is null && method.ExpressionBody is null)
        {
            score = 0;
            return false;
        }
        SyntaxNode body = method.Body is not null ? method.Body : method.ExpressionBody!.Expression;
        score = Walk(body, nesting: 0) + ScoreLogicalChains(body) + ScoreDirectRecursion(body, method.Identifier.Text);
        return true;
    }

    private static int Walk(SyntaxNode node, int nesting)
    {
        int score = 0;
        foreach (SyntaxNode child in node.ChildNodes())
        {
            switch (child)
            {
                case IfStatementSyntax ifStatement:
                    score += ScoreIfChain(ifStatement, nesting);
                    break;

                case SwitchStatementSyntax switchStatement:
                    score += 1 + nesting;
                    foreach (SwitchSectionSyntax section in switchStatement.Sections)
                    {
                        score += Walk(section, nesting + 1);
                    }
                    break;

                case ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                    score += 1 + nesting;
                    score += Walk(child, nesting + 1);
                    break;

                case CatchClauseSyntax catchClause:
                    score += 1 + nesting;
                    score += Walk(catchClause.Block, nesting + 1);
                    break;

                case ConditionalExpressionSyntax:
                    score += 1 + nesting;
                    score += Walk(child, nesting);
                    break;

                case GotoStatementSyntax:
                    score += 1;
                    break;

                default:
                    score += Walk(child, nesting);
                    break;
            }
        }
        return score;
    }

    /// <summary>
    /// Scores one 'if', and recurses into its 'else if'/'else' chain. Each link is scored
    /// explicitly here rather than via a further <see cref="Walk"/> call on the link itself,
    /// because Walk only scores a node when it is visited as someone else's child -- passing the
    /// chain's own if-statement back into Walk as the root would silently skip that if-statement's
    /// own contribution.
    /// </summary>
    private static int ScoreIfChain(IfStatementSyntax ifStatement, int nesting)
    {
        int score = 1 + nesting;
        score += Walk(ifStatement.Statement, nesting + 1);

        ElseClauseSyntax? elseClause = ifStatement.Else;
        if (elseClause is null)
        {
            return score;
        }
        if (elseClause.Statement is IfStatementSyntax nestedIf)
        {
            score += ScoreIfChain(nestedIf, nesting);
        }
        else
        {
            score += 1;
            score += Walk(elseClause.Statement, nesting + 1);
        }
        return score;
    }

    private static int ScoreLogicalChains(SyntaxNode body)
    {
        int score = 0;
        foreach (BinaryExpressionSyntax binary in body.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!IsLogical(binary) || IsLogical(binary.Parent))
            {
                continue;
            }
            SyntaxKind? previous = null;
            foreach (SyntaxKind kind in FlattenLogicalOperators(binary))
            {
                if (kind == previous)
                {
                    continue;
                }
                score++;
                previous = kind;
            }
        }
        return score;
    }

    private static bool IsLogical(SyntaxNode? node) =>
        node is BinaryExpressionSyntax binary &&
        binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression;

    private static IEnumerable<SyntaxKind> FlattenLogicalOperators(ExpressionSyntax expression)
    {
        if (expression is ParenthesizedExpressionSyntax paren)
        {
            foreach (SyntaxKind kind in FlattenLogicalOperators(paren.Expression))
            {
                yield return kind;
            }
            yield break;
        }
        if (expression is not BinaryExpressionSyntax binary || !IsLogical(binary))
        {
            yield break;
        }
        foreach (SyntaxKind kind in FlattenLogicalOperators(binary.Left))
        {
            yield return kind;
        }
        yield return binary.Kind();
        foreach (SyntaxKind kind in FlattenLogicalOperators(binary.Right))
        {
            yield return kind;
        }
    }

    private static int ScoreDirectRecursion(SyntaxNode body, string methodName) =>
        body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation => invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == methodName,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.Text == methodName,
            _ => false
        }) ? 1 : 0;

    private IEnumerable<Finding> FindDuplicatedLiterals(ParsedCSharp parsed, string filePath, int minLength, int minOccurrences)
    {
        var occurrences = new Dictionary<string, List<LiteralExpressionSyntax>>(StringComparer.Ordinal);
        foreach (LiteralExpressionSyntax literal in parsed.Root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }
            string text = literal.Token.ValueText;
            if (text.Length < minLength || literal.Ancestors().Any(IsConstantContext))
            {
                continue;
            }
            if (!occurrences.TryGetValue(text, out List<LiteralExpressionSyntax>? list))
            {
                list = new List<LiteralExpressionSyntax>();
                occurrences[text] = list;
            }
            list.Add(literal);
        }

        foreach ((string text, List<LiteralExpressionSyntax> sites) in occurrences)
        {
            if (sites.Count < minOccurrences)
            {
                continue;
            }
            foreach (LiteralExpressionSyntax site in sites.Skip(1))
            {
                yield return Create(DuplicatedStringLiteral, parsed, site.Span, filePath, "DuplicatedStringLiteral",
                    $"\"{text}\" appears {sites.Count} times in this file; extract it to a single constant.");
            }
        }
    }

    private static bool IsConstantContext(SyntaxNode node) =>
        (node is FieldDeclarationSyntax field && field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))) ||
        (node is LocalDeclarationStatementSyntax local && local.IsConst);

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
