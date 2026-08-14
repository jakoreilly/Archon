using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a mutable field exposed directly as <c>public</c> on a class, which lets any caller change
/// the value with no chance for the class to validate, react to, or forbid the change.
///
/// Only <c>class</c> declarations are checked; a <c>struct</c> is left alone; because a value type
/// commonly exposes public fields by design (a POD/interop shape, a small coordinate-like type), and
/// telling that legitimate case apart from a mistake needs judgement this rule cannot make from
/// syntax alone. <c>const</c> and <c>readonly</c> fields are exempt since neither can be reassigned
/// by a caller after construction, so there is nothing to encapsulate against. An <c>event</c> field
/// is a different syntax node entirely and is never a candidate.
/// </summary>
public sealed class FieldVisibilityRule : IRule
{
    public const string MutablePublicField = "AR0075";

    private const string Category = "maintainability";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            MutablePublicField,
            "Mutable field exposed as public",
            Category,
            Severity.Information,
            "A public, non-readonly field on a class lets any caller change its value directly, with no chance to validate or react. Expose a property instead.")
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

        return context.IsEnabled(MutablePublicField)
            ? FindMutablePublicFields(parsed, file.Path)
            : Array.Empty<Finding>();
    }

    private IEnumerable<Finding> FindMutablePublicFields(ParsedCSharp parsed, string filePath)
    {
        foreach (ClassDeclarationSyntax classDeclaration in parsed.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            foreach (FieldDeclarationSyntax field in classDeclaration.Members.OfType<FieldDeclarationSyntax>())
            {
                if (!field.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                    field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
                {
                    continue;
                }

                foreach (VariableDeclaratorSyntax declarator in field.Declaration.Variables)
                {
                    yield return Create(parsed, declarator.Span, filePath,
                        $"'{declarator.Identifier.Text}' is a public, mutable field; wrap it in a property so the class keeps control over how its value changes.");
                }
            }
        }
    }

    private static Finding Create(ParsedCSharp parsed, TextSpan span, string filePath, string message)
    {
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = MutablePublicField,
            FilePath = filePath,
            Kind = "MutablePublicField",
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = message
        };
    }
}
