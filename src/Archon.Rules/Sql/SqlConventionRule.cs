using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Rules.Sql;

/// <summary>
/// Enforces a team's T-SQL conventions: whether table hints are required or forbidden, and the
/// naming patterns temporary tables and stored procedures must follow.
///
/// All three are policy rather than universal truth, so each stays silent until configured. Names
/// and hints are read from the parsed statement, never from the text, so a name inside a comment or
/// a string is not a candidate.
/// </summary>
public sealed class SqlConventionRule : IRule
{
    /// <summary>A table reference that breaks the configured hint policy.</summary>
    public const string TableHintPolicy = "SQ0010";

    /// <summary>A temporary table whose name does not match the configured pattern.</summary>
    public const string TemporaryTableNaming = "SQ0011";

    /// <summary>A stored procedure whose name does not match the configured pattern.</summary>
    public const string ProcedureNaming = "SQ0012";

    private const string Category = "sql";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            TableHintPolicy,
            "Table hint policy",
            Category,
            Severity.Warning,
            "A table reference does not follow the configured policy on locking hints."),
        new RuleDescriptor(
            TemporaryTableNaming,
            "Temporary table naming",
            Category,
            Severity.Warning,
            "A temporary table's name does not match the configured pattern."),
        new RuleDescriptor(
            ProcedureNaming,
            "Stored procedure naming",
            Category,
            Severity.Warning,
            "A stored procedure's name does not match the configured pattern.")
    };

    public RuleScope Scope => RuleScope.File;

    public string Language => RuleLanguages.Sql;

    /// <summary>Convention settings, each absent by default so nothing is enforced unasked.</summary>
    private sealed record Conventions(string HintPolicy, Regex? TemporaryTable, Regex? Procedure)
    {
        public bool AnythingToCheck => HintPolicy is "required" or "forbidden" || TemporaryTable is not null || Procedure is not null;
    }

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        if (context.TargetFile is not SourceFile file)
        {
            return Array.Empty<Finding>();
        }

        Conventions conventions = ReadConventions(context);
        if (!conventions.AnythingToCheck)
        {
            return Array.Empty<Finding>();
        }

        ParsedSql? parsed = context.Sources.GetSql(file.Path);
        if (parsed?.Fragment is null)
        {
            return Array.Empty<Finding>();
        }

        var visitor = new ConventionVisitor(file.Path, conventions, context.IsEnabled);
        parsed.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    private static Conventions ReadConventions(RuleContext context)
    {
        string hintPolicy = ReadString(context, TableHintPolicy, "policy") ?? "none";
        Regex? temporaryTable = ReadPattern(context, TemporaryTableNaming);
        Regex? procedure = ReadPattern(context, ProcedureNaming);
        return new Conventions(hintPolicy.ToLowerInvariant(), temporaryTable, procedure);
    }

    private static string? ReadString(RuleContext context, string ruleId, string property)
    {
        JsonElement? options = context.OptionsFor(ruleId);
        if (options is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Reads a configured pattern, ignoring one that does not compile rather than failing the whole
    /// pass, since an invalid pattern is a configuration mistake and not a reason to stop analysing.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex?> PatternCache = new();

    private static Regex? ReadPattern(RuleContext context, string ruleId)
    {
        string? pattern = ReadString(context, ruleId, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }
        return PatternCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
    }

    private sealed class ConventionVisitor : TSqlFragmentVisitor
    {
        private readonly string _filePath;
        private readonly Conventions _conventions;
        private readonly Func<string, bool> _isEnabled;

        public ConventionVisitor(string filePath, Conventions conventions, Func<string, bool> isEnabled)
        {
            _filePath = filePath;
            _conventions = conventions;
            _isEnabled = isEnabled;
        }

        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (_isEnabled(TableHintPolicy))
            {
                bool hasHint = node.TableHints.Count > 0;
                string name = node.SchemaObject.BaseIdentifier?.Value ?? "a table";

                if (_conventions.HintPolicy == "required" && !hasHint)
                {
                    Add(TableHintPolicy, node.StartLine, node.StartColumn, "TableHintMissing",
                        $"'{name}' has no table hint, and the configured policy requires one.");
                }
                else if (_conventions.HintPolicy == "forbidden" && hasHint)
                {
                    Add(TableHintPolicy, node.StartLine, node.StartColumn, "TableHintForbidden",
                        $"'{name}' carries a table hint, and the configured policy forbids them.");
                }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            if (_conventions.TemporaryTable is { } pattern && _isEnabled(TemporaryTableNaming))
            {
                string name = node.SchemaObjectName.BaseIdentifier?.Value ?? "";
                if (name.StartsWith('#') && !pattern.IsMatch(name))
                {
                    Add(TemporaryTableNaming, node.StartLine, node.StartColumn, "TemporaryTableNaming",
                        $"Temporary table '{name}' does not match the required pattern '{pattern}'.");
                }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            if (_conventions.Procedure is { } pattern && _isEnabled(ProcedureNaming))
            {
                string name = node.ProcedureReference.Name.BaseIdentifier?.Value ?? "";
                if (name.Length > 0 && !pattern.IsMatch(name))
                {
                    Add(ProcedureNaming, node.StartLine, node.StartColumn, "ProcedureNaming",
                        $"Procedure '{name}' does not match the required pattern '{pattern}'.");
                }
            }
            base.ExplicitVisit(node);
        }

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
