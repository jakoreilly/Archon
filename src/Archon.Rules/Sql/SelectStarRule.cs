using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Rules.Sql;

/// <summary>
/// Flags a wildcard column list, which makes a result set change silently whenever a table gains,
/// loses or reorders a column.
///
/// The decision is made on the parsed statement tree rather than on the text, so
/// <c>COUNT(*)</c> and a string literal that happens to contain the same characters are not
/// candidates at all. A file that does not parse produces no findings from this rule; the parse
/// failure itself is reported separately.
/// </summary>
public sealed class SelectStarRule : IRule
{
    public const string Id = "SQ0001";

    public const string ParseFailed = "SQ0002";

    private const string Category = "sql";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            Id,
            "Wildcard column list",
            Category,
            Severity.Warning,
            "A statement selects all columns instead of naming the ones it uses."),
        new RuleDescriptor(
            ParseFailed,
            "Statement could not be parsed",
            Category,
            Severity.Information,
            "A file could not be parsed as T-SQL, so no SQL rule was able to inspect it.")
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
        if (parsed is null)
        {
            return Array.Empty<Finding>();
        }

        if (parsed.Fragment is null)
        {
            if (!context.IsEnabled(ParseFailed) || parsed.Errors.Count == 0)
            {
                return Array.Empty<Finding>();
            }
            ParseError first = parsed.Errors[0];
            return new[]
            {
                new Finding
                {
                    RuleId = ParseFailed,
                    FilePath = file.Path,
                    Kind = "ParseError",
                    Span = SourceSpan.Line(Math.Max(0, first.Line - 1)),
                    Message = $"Could not parse as T-SQL: {first.Message}"
                }
            };
        }

        var visitor = new SelectStarVisitor(file.Path);
        parsed.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class SelectStarVisitor : TSqlFragmentVisitor
    {
        private readonly string _filePath;

        public SelectStarVisitor(string filePath) => _filePath = filePath;

        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(SelectStarExpression node)
        {
            int line = Math.Max(0, node.StartLine - 1);
            int column = Math.Max(0, node.StartColumn - 1);
            Findings.Add(new Finding
            {
                RuleId = Id,
                FilePath = _filePath,
                Kind = "SelectStar",
                Span = new SourceSpan(line, column, line, column + Math.Max(1, node.FragmentLength)),
                Message = "Selecting all columns is forbidden; list the columns explicitly."
            });
            base.ExplicitVisit(node);
        }
    }
}
