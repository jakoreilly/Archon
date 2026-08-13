using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a local variable created from a well-known disposable type that is never disposed on any
/// path this rule can see.
///
/// Only a curated set of BCL and ADO.NET types is recognised, by written name rather than a
/// resolved interface, the same limitation the security rules already carry. HttpClient is
/// deliberately absent from that set: disposing one per call is itself the well-known
/// socket-exhaustion mistake, so treating "never disposed" as always wrong here would be backwards.
///
/// A variable is exempt the moment it is handed anywhere this rule cannot follow: passed as an
/// argument, returned, or assigned onward (typically to a field). Each of those may well end in a
/// dispose call this rule simply cannot see, so silence is preferred to a guess.
/// </summary>
public sealed class DisposalRule : IRule
{
    public const string UndisposedLocal = "AR0074";

    private const string Category = "maintainability";

    private static readonly HashSet<string> DisposableTypeNames = new(StringComparer.Ordinal)
    {
        "SqlConnection", "SqlCommand", "SqlDataReader", "SqlDataAdapter",
        "OdbcConnection", "OleDbConnection", "NpgsqlConnection", "MySqlConnection", "SqliteConnection",
        "FileStream", "StreamReader", "StreamWriter", "BinaryReader", "BinaryWriter",
        "Mutex", "Semaphore", "SemaphoreSlim", "Timer", "Socket", "TcpClient"
    };

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            UndisposedLocal,
            "Disposable local is never disposed",
            Category,
            Severity.Warning,
            "A local variable creates a well-known disposable type and is never disposed, passed on, returned or assigned onward on any path this rule can see.")
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

        return context.IsEnabled(UndisposedLocal) ? FindUndisposedLocals(parsed, file.Path) : Array.Empty<Finding>();
    }

    private IEnumerable<Finding> FindUndisposedLocals(ParsedCSharp parsed, string filePath)
    {
        foreach (LocalDeclarationStatementSyntax statement in parsed.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
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
                if (declarator.Initializer?.Value is not ObjectCreationExpressionSyntax creation)
                {
                    continue;
                }
                string typeName = DeclaredTypes.SimpleName(creation.Type.ToString());
                if (!DisposableTypeNames.Contains(typeName))
                {
                    continue;
                }
                string name = declarator.Identifier.Text;
                if (name.Length == 0 || IsHandledOrEscapes(scope, name, declarator))
                {
                    continue;
                }
                yield return Create(UndisposedLocal, parsed, declarator.Span, filePath, "UndisposedLocal",
                    $"'{name}' creates a {typeName} that is never disposed on any path this rule can see. " +
                    "Wrap it in a 'using' declaration/statement, or call Dispose().");
            }
        }
    }

    private static bool IsHandledOrEscapes(SyntaxNode scope, string name, VariableDeclaratorSyntax declarator)
    {
        foreach (IdentifierNameSyntax id in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.Text != name || id.SpanStart <= declarator.Span.End)
            {
                continue;
            }
            if (id.Parent is MemberAccessExpressionSyntax member && member.Expression == id &&
                member.Name.Identifier.Text is "Dispose" or "DisposeAsync")
            {
                return true;
            }
            if (id.Parent is ArgumentSyntax)
            {
                return true;
            }
            if (id.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax)
            {
                return true;
            }
            if (id.Parent is AssignmentExpressionSyntax assignment && assignment.Right == id)
            {
                return true;
            }
            if (id.Parent is UsingStatementSyntax usingStatement && usingStatement.Expression == id)
            {
                return true;
            }
        }
        return false;
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
