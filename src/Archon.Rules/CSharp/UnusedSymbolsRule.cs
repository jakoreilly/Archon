using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a method parameter or a local variable that is declared and never read again.
///
/// A parameter is exempt whenever its signature might not be the method's own to change: an
/// override, an explicit interface implementation, or the two-parameter (object, EventArgs)-shaped
/// event handler AsyncSafetyRule already recognises for the same reason. A name starting with '_' is
/// read as a deliberate "intentionally unused" marker, the convention .NET's own discard uses. A
/// local is exempt when declared by 'using' or 'const', since those forms exist for their side
/// effect or scope even when the bound name itself goes unread; this rule does not attempt to track
/// whether a value is read only by being overwritten (a genuine dead store) -- that needs a
/// control-flow graph this rule does not build.
/// </summary>
public sealed class UnusedSymbolsRule : IRule
{
    public const string UnusedParameter = "AR0070";
    public const string UnusedLocalVariable = "AR0071";

    private const string Category = "maintainability";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            UnusedParameter,
            "Parameter is never used",
            Category,
            Severity.Hint,
            "A method parameter is never read in the method body. Silent whenever the signature might not be free to change, since that cannot always be told from syntax alone."),
        new RuleDescriptor(
            UnusedLocalVariable,
            "Local variable is never used",
            Category,
            Severity.Information,
            "A local variable is declared and never read again.")
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
        if (context.IsEnabled(UnusedParameter))
        {
            findings.AddRange(FindUnusedParameters(parsed, file.Path));
        }
        if (context.IsEnabled(UnusedLocalVariable))
        {
            findings.AddRange(FindUnusedLocals(parsed, file.Path));
        }
        return findings;
    }

    private IEnumerable<Finding> FindUnusedParameters(ParsedCSharp parsed, string filePath)
    {
        foreach (MethodDeclarationSyntax method in parsed.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body is null && method.ExpressionBody is null)
            {
                continue;
            }
            if (IsExempt(method))
            {
                continue;
            }
            SyntaxNode body = method.Body is not null ? method.Body : method.ExpressionBody!.Expression;
            var used = new HashSet<string>(
                body.DescendantNodes().OfType<IdentifierNameSyntax>().Select(id => id.Identifier.Text),
                StringComparer.Ordinal);

            foreach (ParameterSyntax parameter in method.ParameterList.Parameters)
            {
                string name = parameter.Identifier.Text;
                if (name.Length == 0 || name.StartsWith('_') || used.Contains(name))
                {
                    continue;
                }
                yield return Create(UnusedParameter, parsed, parameter.Span, filePath, "UnusedParameter",
                    $"'{name}' is never read in '{method.Identifier.Text}'. Remove it, or prefix it with '_' if the signature must keep it.");
            }
        }
    }

    private static bool IsExempt(MethodDeclarationSyntax method)
    {
        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword) || m.IsKind(SyntaxKind.VirtualKeyword)))
        {
            return true;
        }
        if (method.ExplicitInterfaceSpecifier is not null)
        {
            return true;
        }
        SeparatedSyntaxList<ParameterSyntax> parameters = method.ParameterList.Parameters;
        if (parameters.Count == 2)
        {
            string second = parameters[1].Type?.ToString().TrimEnd('?') ?? "";
            if (second.EndsWith("EventArgs", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private IEnumerable<Finding> FindUnusedLocals(ParsedCSharp parsed, string filePath)
    {
        foreach (LocalDeclarationStatementSyntax statement in parsed.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) || statement.IsConst)
            {
                continue;
            }
            SyntaxNode? scope = FindEnclosingBody(statement);
            if (scope is null)
            {
                continue;
            }

            foreach (VariableDeclaratorSyntax declarator in statement.Declaration.Variables)
            {
                string name = declarator.Identifier.Text;
                if (name.Length == 0 || name.StartsWith('_'))
                {
                    continue;
                }
                bool referenced = scope.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(id => id.Identifier.Text == name && id.SpanStart > declarator.Span.End);
                if (referenced)
                {
                    continue;
                }
                yield return Create(UnusedLocalVariable, parsed, declarator.Span, filePath, "UnusedLocalVariable",
                    $"'{name}' is assigned and never read again.");
            }
        }
    }

    private static SyntaxNode? FindEnclosingBody(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is BlockSyntax or ArrowExpressionClauseSyntax);

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
