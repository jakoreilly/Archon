using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Core.Sql;

/// <summary>One table's known columns, keyed by a normalized "schema.name" id.</summary>
public sealed record TableSchema(string Id, string Schema, string Name, IReadOnlyList<string> Columns)
{
    public bool HasColumn(string columnName) =>
        Columns.Any(c => string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A minimal schema model built entirely from parsed <c>CREATE TABLE</c> statements in the
/// workspace's own <c>.sql</c> files -- no live database connection, no configuration beyond the
/// files Archon already discovers. Answers exactly one question a rule needs: does a named table
/// have a column with this name. Column types, keys, indexes and every other schema fact a live
/// catalog carries are deliberately absent; a rule that needs one of those is not this rule.
/// </summary>
public sealed class SchemaCatalog
{
    private readonly Dictionary<string, TableSchema> _tablesById;

    private SchemaCatalog(Dictionary<string, TableSchema> tablesById) => _tablesById = tablesById;

    public static readonly SchemaCatalog Empty = new(new Dictionary<string, TableSchema>(StringComparer.Ordinal));

    public TableSchema? Find(string schema, string name) =>
        _tablesById.TryGetValue(NormalizeId(schema, name), out TableSchema? table) ? table : null;

    /// <summary>Builds a catalog from every already-parsed T-SQL fragment supplied, so a caller
    /// with access to <see cref="Sources.SourceCache"/>'s cached parse reuses it instead of
    /// re-parsing.</summary>
    public static SchemaCatalog Build(IEnumerable<TSqlFragment> fragments)
    {
        var visitor = new TableVisitor();
        foreach (TSqlFragment fragment in fragments)
        {
            fragment.Accept(visitor);
        }
        return new SchemaCatalog(visitor.TablesById);
    }

    /// <summary>Schema defaults to "dbo" (T-SQL's own default for an unqualified name), then the
    /// id is lowercased for lookup. Every identifier reaching this method already comes from
    /// ScriptDom's own <c>Identifier.Value</c>, which is unquoted -- unlike Arch's
    /// IdentifierRules.NormalizeId, no bracket/quote stripping is needed here.</summary>
    private static string NormalizeId(string schema, string name) =>
        $"{(schema.Length == 0 ? "dbo" : schema)}.{name}".ToLowerInvariant();

    private static bool IsTempOrVariable(string name) => name.Length > 0 && (name[0] == '#' || name[0] == '@');

    private sealed class TableVisitor : TSqlFragmentVisitor
    {
        public Dictionary<string, TableSchema> TablesById { get; } = new(StringComparer.Ordinal);

        public override void Visit(CreateTableStatement node)
        {
            IList<Identifier> identifiers = node.SchemaObjectName.Identifiers;
            if (identifiers.Count == 0)
            {
                return;
            }
            string name = identifiers[^1].Value;
            if (name.Length == 0 || IsTempOrVariable(name))
            {
                return;
            }
            string schema = identifiers.Count >= 2 ? identifiers[^2].Value : "";

            var columns = new List<string>();
            foreach (ColumnDefinition column in node.Definition.ColumnDefinitions)
            {
                columns.Add(column.ColumnIdentifier.Value);
            }

            string id = NormalizeId(schema, name);
            TablesById[id] = new TableSchema(id, schema.Length == 0 ? "dbo" : schema, name, columns);
        }
    }
}
