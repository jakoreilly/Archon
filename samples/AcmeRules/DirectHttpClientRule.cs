using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AcmeRules;

/// <summary>
/// The smallest rule worth writing: one condition, decidable from a single file's syntax.
///
/// Note what this class does not do. It does not read configuration to find out whether it is
/// switched on, it does not look for an ignore comment, and it does not decide how serious the
/// finding is. The engine applies all three afterwards, which is what makes a rule from an
/// external pack behave exactly like a built-in one.
/// </summary>
public sealed class DirectHttpClientRule : IRule
{
    public const string Id = "ACME0001";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            Id,
            "HttpClient constructed directly",
            "reliability",
            Severity.Warning,
            "Each HttpClient holds its own connection pool, so constructing one per call exhausts "
                + "sockets under load. Inject IHttpClientFactory and ask it for a client instead.")
    };

    // File scope means this re-runs on every save, which is affordable because it reads one file.
    // Declaring a wider scope than a rule needs is the main way to make the editor feel slow.
    public RuleScope Scope => RuleScope.File;

    public string Language => RuleLanguages.CSharp;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        // TargetFile is set for File scope. Wider scopes read context.Workspace instead.
        if (context.TargetFile is not { } file)
        {
            yield break;
        }

        // Going through the cache rather than reading the file means this rule shares the one
        // parse of it that every other rule is using, and sees unsaved editor text when there is
        // any. Returns null when the file could not be read.
        if (context.Sources.GetCSharp(file.Path) is not { } parsed)
        {
            yield break;
        }

        foreach (ObjectCreationExpressionSyntax creation in parsed.Root
                     .DescendantNodes()
                     .OfType<ObjectCreationExpressionSyntax>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (creation.Type is not IdentifierNameSyntax { Identifier.Text: "HttpClient" })
            {
                continue;
            }

            yield return new Finding
            {
                RuleId = Id,
                Message = "HttpClient is constructed directly. Inject IHttpClientFactory instead.",
                FilePath = file.Path,
                Span = SpanOf(parsed, creation)
                // Severity and Category are deliberately not set: the engine overwrites both from
                // the descriptor and the user's configuration.
            };
        }
    }

    internal static SourceSpan SpanOf(ParsedCSharp parsed, SyntaxNode node)
    {
        FileLinePositionSpan span = parsed.Tree.GetLineSpan(node.Span);
        return new SourceSpan(
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character);
    }
}
