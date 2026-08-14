using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Rules.Sql;

/// <summary>
/// Flags two statement shapes that are almost always a mistake rather than an intended query: a
/// <c>DELETE</c> or <c>UPDATE</c> with no <c>WHERE</c> clause, which touches every row in the table
/// instead of the one the author meant to name; and a comparison to the literal <c>NULL</c> with
/// <c>=</c> or <c>&lt;&gt;</c>, which is always unknown (neither true nor false) under three-valued
/// logic and so never behaves the way the operator suggests.
///
/// A <c>DELETE</c> against a table named <c>#...</c> is exempt: clearing a whole staging or temp
/// table before reloading it is a common, deliberate pattern, and flagging it would drown out the
/// genuine case this rule exists for. The decision is made on the parsed statement tree, never on
/// the text, so a table or column named to look like a temp table inside a string is never a
/// candidate.
/// </summary>
public sealed class SqlSafetyRule : IRule
{
    /// <summary>A DELETE or UPDATE statement with no WHERE clause.</summary>
    public const string MissingWhereClause = "SQ0020";

    /// <summary>A comparison to the literal NULL with '=' or '&lt;&gt;'/'!=' instead of IS [NOT] NULL.</summary>
    public const string NullComparisonWithEqualityOperator = "SQ0021";

    private const string Category = "sql";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            MissingWhereClause,
            "DELETE/UPDATE with no WHERE clause",
            Category,
            Severity.Warning,
            "A DELETE or UPDATE statement has no WHERE clause, so it applies to every row in the table."),
        new RuleDescriptor(
            NullComparisonWithEqualityOperator,
            "Comparison to NULL with = or <>",
            Category,
            Severity.Warning,
            "A comparison to NULL with '=' or '<>' is always unknown under three-valued logic; use IS NULL or IS NOT NULL instead.")
    };

    public RuleScope Scope => RuleScope.File;

    public string Language => RuleLanguages.Sql;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        if (context.TargetFile is not SourceFile file)
        {
            return Array.Empty<Finding>();
        }

        ParsedSql? parsed = context.Sources.GetSql(file.Path);
        if (parsed?.Fragment is null)
        {
            return Array.Empty<Finding>();
        }

        var visitor = new SafetyVisitor(file.Path, context.IsEnabled);
        parsed.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class SafetyVisitor : TSqlFragmentVisitor
    {
        private readonly string _filePath;
        private readonly Func<string, bool> _isEnabled;

        public SafetyVisitor(string filePath, Func<string, bool> isEnabled)
        {
            _filePath = filePath;
            _isEnabled = isEnabled;
        }

        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(DeleteStatement node)
        {
            if (_isEnabled(MissingWhereClause))
            {
                DeleteSpecification spec = node.DeleteSpecification;
                if (spec.WhereClause is null && !IsTemporaryTable(spec.Target))
                {
                    Add(MissingWhereClause, node.StartLine, node.StartColumn, "MissingWhereClause",
                        "This DELETE has no WHERE clause and will remove every row in the table.");
                }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            if (_isEnabled(MissingWhereClause))
            {
                UpdateSpecification spec = node.UpdateSpecification;
                if (spec.WhereClause is null)
                {
                    Add(MissingWhereClause, node.StartLine, node.StartColumn, "MissingWhereClause",
                        "This UPDATE has no WHERE clause and will change every row in the table.");
                }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            if (_isEnabled(NullComparisonWithEqualityOperator))
            {
                bool isEquality = node.ComparisonType is BooleanComparisonType.Equals
                    or BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation;
                if (isEquality && (IsNullLiteral(node.FirstExpression) || IsNullLiteral(node.SecondExpression)))
                {
                    string replacement = node.ComparisonType == BooleanComparisonType.Equals ? "IS NULL" : "IS NOT NULL";
                    Add(NullComparisonWithEqualityOperator, node.StartLine, node.StartColumn, "NullComparisonWithEqualityOperator",
                        $"This comparison to NULL is always unknown, not true or false; use {replacement} instead.");
                }
            }
            base.ExplicitVisit(node);
        }

        private static bool IsNullLiteral(ScalarExpression expression) => expression is NullLiteral;

        /// <summary>A temp table (local '#' or global '##') named as the statement's own target.</summary>
        private static bool IsTemporaryTable(TableReference target) =>
            target is NamedTableReference { SchemaObject.BaseIdentifier: { Value: { } name } } &&
            name.StartsWith('#');

        private void Add(string ruleId, int startLine, int startColumn, string kind, string message)
        {
            int line = Math.Max(0, startLine - 1);
            int column = Math.Max(0, startColumn - 1);
            Findings.Add(new Finding
            {
                RuleId = ruleId,
                FilePath = _filePath,
                Kind = kind,
                Span = new SourceSpan(line, column, line, column + 1),
                Message = message
            });
        }
    }
}
