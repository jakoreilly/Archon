using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Archon.Core.Sql;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a column named in inline SQL that does not exist on the single table the statement
/// unambiguously targets. The schema comes entirely from CREATE TABLE statements already present
/// in the workspace's own .sql files (Archon.Core.Sql.SchemaCatalog) -- no live connection, no new
/// configuration. A statement whose target table is not multi-table-ambiguous but is simply not
/// found in the catalog is silently skipped, not flagged as missing: the table may be defined
/// through a live database this tool never sees, and reporting an absence it cannot be sure of
/// would be a false positive by construction. JOINs, multi-table FROM clauses and INSERTs with no
/// explicit column list are skipped entirely for the same reason AR0023 only trusts what parses --
/// resolving a bare column name across several tables needs alias tracking this rule does not do.
/// </summary>
public sealed class SchemaAwareSqlRule : IRule
{
    public const string UnknownColumn = "AR0080";

    private const string Category = "sql";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            UnknownColumn,
            "Unknown column referenced in inline SQL",
            Category,
            Severity.Warning,
            "SQL embedded in C# names a column that does not exist on the table it targets, judged against CREATE TABLE statements found elsewhere in the workspace.")
    };

    public RuleScope Scope => RuleScope.Workspace;

    public string Language => RuleLanguages.Any;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        if (!context.IsEnabled(UnknownColumn))
        {
            return Array.Empty<Finding>();
        }

        var fragments = new List<TSqlFragment>();
        foreach (SourceFile sqlFile in context.Workspace.FilesOfLanguage(RuleLanguages.Sql))
        {
            TSqlFragment? fragment = context.Sources.GetSql(sqlFile.Path)?.Fragment;
            if (fragment is not null)
            {
                fragments.Add(fragment);
            }
        }
        SchemaCatalog catalog = SchemaCatalog.Build(fragments);

        var findings = new List<Finding>();
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        foreach (SourceFile csharpFile in context.Workspace.FilesOfLanguage(RuleLanguages.CSharp))
        {
            ParsedCSharp? parsed = context.Sources.GetCSharp(csharpFile.Path);
            if (parsed is null)
            {
                continue;
            }
            foreach (LiteralExpressionSyntax literal in parsed.Root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }
                string text = literal.Token.ValueText;
                if (text.Length < 10 || !LooksLikeCandidateStatement(text))
                {
                    continue;
                }
                findings.AddRange(CheckLiteral(parser, text, catalog, parsed, literal, csharpFile.Path));
            }
        }
        return findings;
    }

    private static bool LooksLikeCandidateStatement(string text) =>
        text.Contains("select", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("insert", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("update", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<Finding> CheckLiteral(
        TSql150Parser parser, string text, SchemaCatalog catalog,
        ParsedCSharp parsed, LiteralExpressionSyntax literal, string filePath)
    {
        using var reader = new StringReader(text);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0)
        {
            yield break;
        }

        foreach (TSqlStatement statement in AllStatements(fragment))
        {
            IEnumerable<(string Column, TSqlFragment Site)>? bad = statement switch
            {
                SelectStatement select => CheckSelect(select, catalog),
                InsertStatement insert => CheckInsert(insert, catalog),
                UpdateStatement update => CheckUpdate(update, catalog),
                _ => null
            };
            if (bad is null)
            {
                continue;
            }
            foreach ((string column, TSqlFragment _) in bad)
            {
                yield return Create(parsed, literal.Span, filePath,
                    $"'{column}' does not exist on the table this statement targets. Check the column name, or the table's CREATE TABLE definition.");
            }
        }
    }

    private static IEnumerable<TSqlStatement> AllStatements(TSqlFragment fragment) =>
        fragment switch
        {
            TSqlScript script => script.Batches.SelectMany(b => b.Statements),
            TSqlStatement statement => new[] { statement },
            _ => Array.Empty<TSqlStatement>()
        };

    private static IEnumerable<(string Column, TSqlFragment Site)> CheckSelect(SelectStatement select, SchemaCatalog catalog)
    {
        if (select.QueryExpression is not QuerySpecification query ||
            query.FromClause is not { TableReferences.Count: 1 } from ||
            from.TableReferences[0] is not NamedTableReference tableRef)
        {
            yield break;
        }
        TableSchema? table = ResolveTable(tableRef, catalog);
        if (table is null)
        {
            yield break;
        }

        foreach (SelectElement element in query.SelectElements)
        {
            if (element is not SelectScalarExpression { Expression: ColumnReferenceExpression columnRef })
            {
                continue;
            }
            string? column = LastIdentifier(columnRef.MultiPartIdentifier);
            if (column is not null && !table.HasColumn(column))
            {
                yield return (column, columnRef);
            }
        }
    }

    private static IEnumerable<(string Column, TSqlFragment Site)> CheckInsert(InsertStatement insert, SchemaCatalog catalog)
    {
        InsertSpecification spec = insert.InsertSpecification;
        if (spec.Target is not NamedTableReference tableRef || spec.Columns.Count == 0)
        {
            yield break;
        }
        TableSchema? table = ResolveTable(tableRef, catalog);
        if (table is null)
        {
            yield break;
        }

        foreach (ColumnReferenceExpression columnRef in spec.Columns)
        {
            string? column = LastIdentifier(columnRef.MultiPartIdentifier);
            if (column is not null && !table.HasColumn(column))
            {
                yield return (column, columnRef);
            }
        }
    }

    private static IEnumerable<(string Column, TSqlFragment Site)> CheckUpdate(UpdateStatement update, SchemaCatalog catalog)
    {
        UpdateSpecification spec = update.UpdateSpecification;
        if (spec.Target is not NamedTableReference tableRef || spec.FromClause is not null)
        {
            yield break;
        }
        TableSchema? table = ResolveTable(tableRef, catalog);
        if (table is null)
        {
            yield break;
        }

        foreach (SetClause setClause in spec.SetClauses)
        {
            if (setClause is not AssignmentSetClause { Column: not null } assignment)
            {
                continue;
            }
            string? column = LastIdentifier(assignment.Column.MultiPartIdentifier);
            if (column is not null && !table.HasColumn(column))
            {
                yield return (column, assignment.Column);
            }
        }
    }

    private static TableSchema? ResolveTable(NamedTableReference tableRef, SchemaCatalog catalog)
    {
        IList<Identifier> identifiers = tableRef.SchemaObject.Identifiers;
        if (identifiers.Count == 0)
        {
            return null;
        }
        string name = identifiers[^1].Value;
        string schema = identifiers.Count >= 2 ? identifiers[^2].Value : "";
        return catalog.Find(schema, name);
    }

    private static string? LastIdentifier(MultiPartIdentifier identifier) =>
        identifier.Count == 0 ? null : identifier.Identifiers[^1].Value;

    private static Finding Create(ParsedCSharp parsed, TextSpan span, string filePath, string message)
    {
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = UnknownColumn,
            FilePath = filePath,
            Kind = "UnknownColumn",
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = message
        };
    }
}
