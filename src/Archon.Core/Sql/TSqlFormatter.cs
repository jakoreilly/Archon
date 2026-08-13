using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Core.Sql;

/// <summary>Loss-safe T-SQL formatter: parse -> regenerate each statement via ScriptDom's generator,
/// re-emitting inter-statement comments (which live in the token stream, not the AST, and would
/// otherwise be dropped). A file that fails to parse is passed through byte-for-byte unchanged
/// (never corrupt or drop SQL it did not understand). Inline comments *inside* a statement cannot be
/// preserved through GenerateScript and are reported as a diagnostic rather than silently lost.
///
/// Parses with <see cref="TSql150Parser"/> to match <c>SourceCache.GetSql</c>, which every SQL rule
/// reads from — a file that parses for the rules and fails to format (or vice versa) would be a
/// confusing inconsistency between the two surfaces.
/// </summary>
public static class TSqlFormatter
{
    private static SqlScriptGeneratorOptions Options() => new()
    {
        KeywordCasing = KeywordCasing.Uppercase,
        IndentationSize = 4,
        AlignClauseBodies = true,
        NewLineBeforeFromClause = true,
        NewLineBeforeWhereClause = true,
        NewLineBeforeGroupByClause = true,
        NewLineBeforeOrderByClause = true,
    };

    public static string Format(string content)
    {
        using var reader = new StringReader(content);
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
        {
            return content; // unparseable (or not a script): pass through unchanged, never corrupt
        }

        var tokens = fragment.ScriptTokenStream;
        var gen = new Sql150ScriptGenerator(Options());
        var sb = new StringBuilder();
        var cursor = 0; // next unconsumed token index; comments before a statement are emitted first

        for (var b = 0; b < script.Batches.Count; b++)
        {
            var batch = script.Batches[b];
            foreach (var stmt in batch.Statements)
            {
                EmitCommentsBetween(sb, tokens, cursor, stmt.FirstTokenIndex);
                gen.GenerateScript(stmt, out var formatted);
                sb.Append(formatted.TrimEnd());
                sb.Append('\n');
                cursor = stmt.LastTokenIndex + 1;
            }
            if (b < script.Batches.Count - 1)
            { sb.Append("GO\n"); } // GO separates batches; not a statement
        }
        // Trailing comments after the last statement (e.g. a footer banner).
        EmitCommentsBetween(sb, tokens, cursor, tokens.Count);
        return sb.ToString();
    }

    /// <summary>Emits every comment token in [from, toExclusive) verbatim, one per line. Whitespace
    /// and other tokens in the gap are dropped (the generator re-creates layout); only comments —
    /// which the generator can't reproduce — are carried across.</summary>
    private static void EmitCommentsBetween(StringBuilder sb, IList<TSqlParserToken> tokens, int from, int toExclusive)
    {
        for (var i = from; i < toExclusive && i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
            {
                sb.Append(t.Text.TrimEnd('\r', '\n'));
                sb.Append('\n');
            }
        }
    }

    /// <summary>True if the parsed content contains a comment token that lies *inside* a statement's
    /// token span (i.e. one this formatter cannot preserve). Callers surface a diagnostic.</summary>
    public static bool HasInlineComments(string content)
    {
        using var reader = new StringReader(content);
        var fragment = new TSql150Parser(initialQuotedIdentifiers: true).Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
        { return false; }

        var comments = new List<int>();
        for (var i = 0; i < fragment.ScriptTokenStream.Count; i++)
        {
            var tt = fragment.ScriptTokenStream[i].TokenType;
            if (tt is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
            { comments.Add(i); }
        }
        foreach (var stmt in script.Batches.SelectMany(b => b.Statements))
        {
            if (comments.Any(c => c > stmt.FirstTokenIndex && c < stmt.LastTokenIndex))
            { return true; }
        }
        return false;
    }
}
