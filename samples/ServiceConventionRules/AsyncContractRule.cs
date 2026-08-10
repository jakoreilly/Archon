using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ServiceConventionRules;

/// <summary>
/// Flags an asynchronous method whose name does not say so: a method whose body awaits something
/// but whose name does not end in "Async". Reads only <see cref="MethodDeclarationSyntax"/>, the
/// same enumeration limit AR0012/AR0060/AR0070 already carry (see the Phase 1 investigation), so
/// a bare method at file scope needs the same wrapping to be seen; a local function is out of
/// scope for the same reason and is never visited.
///
/// A sibling check — a missing <c>CancellationToken</c> parameter — was measured against the
/// vendored library and abandoned rather than shipped as SVC0021: most of its raw hits were a
/// method implementing a framework interface whose signature is fixed
/// (<c>IJob.Execute</c>, <c>IConsumer&lt;T&gt;.Consume</c>, <c>IRollbackService.RollbackAsync</c>,
/// <c>IActionResult.ExecuteResultAsync</c>) or a delegate-wrapping helper that receives
/// cancellation through a closure rather than a parameter — the same "cannot always be told from
/// syntax alone" limitation UnusedSymbolsRule already accepts for AR0070, but at a volume (see the
/// phase report) past the point a convention pack can defend shipping it. SVC0021 is retired and
/// must not be reused.
/// </summary>
public sealed class AsyncContractRule : IRule
{
    public const string MissingAsyncSuffix = "SVC0020";

    private const string Category = "conventions";

    private static readonly HashSet<string> HttpVerbAttributes = new(StringComparer.Ordinal)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions"
    };

    private static readonly HashSet<string> TestMethodAttributes = new(StringComparer.Ordinal)
    {
        "Test", "TestCase", "Theory", "Fact"
    };

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            MissingAsyncSuffix,
            "Async method missing the 'Async' suffix",
            Category,
            Severity.Hint,
            "A method whose body awaits something does not end its name in 'Async', so a caller cannot tell from the signature alone that it must be awaited.")
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
        if (!context.IsEnabled(MissingAsyncSuffix))
        {
            return Array.Empty<Finding>();
        }

        var findings = new List<Finding>();
        foreach (MethodDeclarationSyntax method in parsed.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.Text.EndsWith("Async", StringComparison.Ordinal))
            {
                continue;
            }
            SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body is null || !ContainsAwait(body) || IsExempt(method))
            {
                continue;
            }
            findings.Add(Create(parsed, method.Identifier.Span, file.Path,
                $"'{method.Identifier.Text}' awaits something but its name does not end in 'Async'."));
        }
        return findings;
    }

    private static bool ContainsAwait(SyntaxNode body) =>
        body.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();

    /// <summary>
    /// Signatures that are not the method's own to change: the entry point, an override, a member
    /// of an interface named by convention (starting with 'I'), an ASP.NET Core action method
    /// (an `[Http*]` routing attribute), a test framework's own method (`[Test]`/`[TestCase]`/
    /// `[Theory]`/`[Fact]` — PUB-TEST-06's own naming convention, "Method_WhenCondition_
    /// ExpectedResult", is the whole point of that snippet), or the (object, EventArgs) shape
    /// AsyncSafetyRule already recognises for the same reason.
    /// </summary>
    private static bool IsExempt(MethodDeclarationSyntax method)
    {
        if (method.Identifier.Text == "Main")
        {
            return true;
        }
        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
        {
            return true;
        }
        if (method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is InterfaceDeclarationSyntax
            containingInterface && containingInterface.Identifier.Text.StartsWith("I", StringComparison.Ordinal))
        {
            return true;
        }
        if (HasAttributeNamed(method, HttpVerbAttributes) || HasAttributeNamed(method, TestMethodAttributes))
        {
            return true;
        }
        return LooksLikeEventHandler(method);
    }

    /// <summary>
    /// Recognises the one shape where an (object, EventArgs) parameter list is the required
    /// signature rather than a mistake, so the common correct case is not reported. Copied from
    /// AsyncSafetyRule.LooksLikeEventHandler, which this pack cannot reference directly.
    /// </summary>
    private static bool LooksLikeEventHandler(MethodDeclarationSyntax method)
    {
        SeparatedSyntaxList<ParameterSyntax> parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2)
        {
            return false;
        }
        string second = parameters[1].Type?.ToString().TrimEnd('?') ?? "";
        return second.EndsWith("EventArgs", StringComparison.Ordinal);
    }

    private static bool HasAttributeNamed(MethodDeclarationSyntax method, HashSet<string> names) =>
        method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => names.Contains(AttributeSimpleName(attribute)));

    private static string AttributeSimpleName(AttributeSyntax attribute)
    {
        string name = attribute.Name switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => ""
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static Finding Create(ParsedCSharp parsed, TextSpan span, string filePath, string message)
    {
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = MissingAsyncSuffix,
            FilePath = filePath,
            Kind = "MissingAsyncSuffix",
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = message
        };
    }
}
