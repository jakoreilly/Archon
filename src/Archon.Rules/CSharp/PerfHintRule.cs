using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags shapes that cost more than the obvious alternative.
///
/// Each rule here is decidable from syntax alone with no guessing. An invocation
/// <c>Count()</c> and a property access <c>Count</c> are different node shapes, so the first is
/// distinguishable from the second without resolving anything. Concatenation is only reported for
/// an identifier explicitly declared <c>string</c>. Inline SQL is decided by parsing the literal as
/// T-SQL, never by searching its text, so prose that happens to contain the same words is silent.
/// </summary>
public sealed class PerfHintRule : IRule
{
    /// <summary>Counting a whole sequence to answer whether anything is in it.</summary>
    public const string CountInsteadOfAny = "AR0020";

    /// <summary>Repeated string concatenation inside a loop.</summary>
    public const string ConcatenationInLoop = "AR0021";

    /// <summary>Materialising a sequence that is immediately filtered or transformed again.</summary>
    public const string RedundantMaterialisation = "AR0022";

    /// <summary>A wildcard column list inside SQL embedded in C#.</summary>
    public const string InlineWildcardSelect = "AR0023";

    private const string Category = "performance";

    private static readonly HashSet<string> DeferredOperators = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "Any", "All", "First", "FirstOrDefault", "Single",
        "SingleOrDefault", "OrderBy", "OrderByDescending", "ThenBy", "GroupBy", "Skip", "Take",
        "Count", "Sum", "Min", "Max", "Average", "Distinct", "Reverse", "Contains", "ToDictionary"
    };

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            CountInsteadOfAny,
            "Count used as an emptiness check",
            Category,
            Severity.Information,
            "A sequence is counted in full to decide whether it holds anything; Any stops at the first element."),
        new RuleDescriptor(
            ConcatenationInLoop,
            "String concatenation in a loop",
            Category,
            Severity.Information,
            "Repeated concatenation in a loop copies the whole string each time."),
        new RuleDescriptor(
            RedundantMaterialisation,
            "Sequence materialised then transformed",
            Category,
            Severity.Hint,
            "A sequence is copied into a list or array and then immediately filtered or transformed again."),
        new RuleDescriptor(
            InlineWildcardSelect,
            "Wildcard column list in inline SQL",
            Category,
            Severity.Information,
            "SQL embedded in C# selects all columns, so its result changes whenever a table does.")
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
        if (context.IsEnabled(CountInsteadOfAny))
        {
            findings.AddRange(FindCountComparisons(parsed, file.Path));
        }
        if (context.IsEnabled(ConcatenationInLoop))
        {
            findings.AddRange(FindConcatenationInLoop(parsed, file.Path));
        }
        if (context.IsEnabled(RedundantMaterialisation))
        {
            findings.AddRange(FindRedundantMaterialisation(parsed, file.Path));
        }
        if (context.IsEnabled(InlineWildcardSelect))
        {
            findings.AddRange(FindInlineWildcard(parsed, file.Path));
        }
        return findings;
    }

    private IEnumerable<Finding> FindCountComparisons(ParsedCSharp parsed, string filePath)
    {
        foreach (BinaryExpressionSyntax binary in parsed.Root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (binary.Kind() is not (SyntaxKind.GreaterThanExpression or SyntaxKind.NotEqualsExpression
                or SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.EqualsExpression))
            {
                continue;
            }
            if (!IsCountComparison(binary, out bool isEmptinessCheck))
            {
                continue;
            }
            yield return Create(CountInsteadOfAny, parsed, binary.Span, filePath, "CountInsteadOfAny",
                isEmptinessCheck
                    ? "Prefer '!sequence.Any()' to counting every element to learn that there are none."
                    : "Prefer 'sequence.Any()' to counting every element to learn that there is at least one.");
        }
    }

    /// <summary>
    /// Matches a comparison of <c>Count()</c> against zero or one that is really an emptiness test.
    /// Only the invocation form is matched, so a <c>Count</c> property on a collection that already
    /// knows its size is never reported.
    /// </summary>
    private static bool IsCountComparison(BinaryExpressionSyntax binary, out bool isEmptinessCheck)
    {
        isEmptinessCheck = false;

        (ExpressionSyntax candidate, ExpressionSyntax other) = binary.Left is LiteralExpressionSyntax
            ? (binary.Right, binary.Left)
            : (binary.Left, binary.Right);

        if (other is not LiteralExpressionSyntax literal || literal.Token.Value is not int value)
        {
            return false;
        }
        if (candidate is not InvocationExpressionSyntax invocation ||
            invocation.Expression is not MemberAccessExpressionSyntax member ||
            member.Name.Identifier.Text != "Count" ||
            invocation.ArgumentList.Arguments.Count != 0)
        {
            return false;
        }

        bool zero = value == 0;
        isEmptinessCheck = binary.IsKind(SyntaxKind.EqualsExpression) && zero;
        bool nonEmpty = (binary.IsKind(SyntaxKind.GreaterThanExpression) && zero)
            || (binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression) && value == 1)
            || (binary.IsKind(SyntaxKind.NotEqualsExpression) && zero);

        return isEmptinessCheck || nonEmpty;
    }

    private IEnumerable<Finding> FindConcatenationInLoop(ParsedCSharp parsed, string filePath)
    {
        Dictionary<string, string> declared = DeclaredTypes.Collect(parsed.Root);

        foreach (AssignmentExpressionSyntax assignment in parsed.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression))
            {
                continue;
            }
            string? name = assignment.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                _ => null
            };
            if (name is null || !declared.TryGetValue(name, out string? typeText) || typeText.TrimEnd('?') != "string")
            {
                continue;
            }
            bool inLoop = assignment.Ancestors()
                .Any(a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
            if (!inLoop)
            {
                continue;
            }
            yield return Create(ConcatenationInLoop, parsed, assignment.Span, filePath, "ConcatenationInLoop",
                $"'{name}' is rebuilt on every iteration, so the cost grows with the square of the loop count. Use a StringBuilder.");
        }
    }

    private IEnumerable<Finding> FindRedundantMaterialisation(ParsedCSharp parsed, string filePath)
    {
        foreach (InvocationExpressionSyntax outer in parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (outer.Expression is not MemberAccessExpressionSyntax outerMember ||
                !DeferredOperators.Contains(outerMember.Name.Identifier.Text))
            {
                continue;
            }
            if (outerMember.Expression is not InvocationExpressionSyntax inner ||
                inner.Expression is not MemberAccessExpressionSyntax innerMember)
            {
                continue;
            }
            string materialiser = innerMember.Name.Identifier.Text;
            if (materialiser is not ("ToList" or "ToArray") || inner.ArgumentList.Arguments.Count != 0)
            {
                continue;
            }
            yield return Create(RedundantMaterialisation, parsed, outer.Span, filePath, "RedundantMaterialisation",
                $"'.{materialiser}()' copies the sequence and '.{outerMember.Name.Identifier.Text}(...)' then walks the copy. " +
                "Drop the copy unless it is a deliberate snapshot.");
        }
    }

    private IEnumerable<Finding> FindInlineWildcard(ParsedCSharp parsed, string filePath)
    {
        foreach (LiteralExpressionSyntax literal in parsed.Root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }
            string text = literal.Token.ValueText;
            if (text.Length < 10 || !text.Contains("select", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!ContainsWildcardSelect(text))
            {
                continue;
            }
            yield return Create(InlineWildcardSelect, parsed, literal.Span, filePath, "InlineWildcardSelect",
                "This embedded query selects all columns, so its result set changes whenever the table does. List the columns.");
        }
    }

    /// <summary>
    /// Decides whether a literal is SQL with a wildcard column list by parsing it. Text that is not
    /// valid T-SQL fails to parse and is reported as nothing at all, which is what keeps ordinary
    /// prose containing an asterisk from being flagged.
    /// </summary>
    private static bool ContainsWildcardSelect(string text)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0)
        {
            return false;
        }
        var visitor = new WildcardVisitor();
        fragment.Accept(visitor);
        return visitor.Found;
    }

    private sealed class WildcardVisitor : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(SelectStarExpression node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }
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
