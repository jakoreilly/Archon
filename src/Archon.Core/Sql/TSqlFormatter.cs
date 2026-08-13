using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Core.Sql;

/// <summary>Loss-safe T-SQL formatter: parse -> regenerate each statement via ScriptDom's generator,
/// re-emitting comments the generator has no slot for (which live in the token stream, not the AST,
/// and would otherwise be dropped). A file that fails to parse is passed through byte-for-byte
/// unchanged (never corrupt or drop SQL it did not understand).
///
/// Comments between top-level statements, and between statements *inside* a BEGIN...END body (a
/// procedure, function or trigger body, or an IF/WHILE/TRY-CATCH block written with braces) are
/// preserved, at any nesting depth. A comment sitting inside a single unbraced statement — mid
/// expression, or the lone statement of an unbraced IF/WHILE — cannot be preserved, because
/// ScriptDom's generator regenerates that statement from its AST in one call with no comment slot;
/// <see cref="HasInlineComments"/> reports when this happens so a caller can surface it.
///
/// Parses with <see cref="TSql150Parser"/> to match <c>SourceCache.GetSql</c>, which every SQL rule
/// reads from — a file that parses for the rules and fails to format (or vice versa) would be a
/// confusing inconsistency between the two surfaces.
/// </summary>
public static class TSqlFormatter
{
    /// <summary>Marks a BEGIN...END body ScriptDom is about to regenerate whole, so its real,
    /// comment-preserving text can be spliced back in afterwards. Private-use prefix: astronomically
    /// unlikely to collide with anything in real SQL, and even if it somehow did, the worst case is a
    /// misplaced splice in one file's formatting, never data loss or corruption elsewhere.</summary>
    private const string PlaceholderPrefix = "ARCHON_FMT_BLOCK_";

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

        // Every BEGIN...END body anywhere in the tree is replaced with a single placeholder
        // statement before anything is generated, deepest first, so the top-level GenerateScript
        // call below regenerates a correctly laid-out shell (procedure signature, IF/WHILE syntax,
        // indentation, everything ScriptDom already gets right) with a marker in place of each body.
        // Each body's own comment-preserving text is generated separately, from the *original*
        // statements, and spliced back into the marker's position afterwards — at that marker's own
        // rendered indentation, so nesting depth needs no manual tracking.
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        fragment.Accept(new BeginEndCommentSplicer(tokens, gen, replacements));

        var sb = new StringBuilder();
        var cursor = 0; // next unconsumed token index; comments before a statement are emitted first

        for (var b = 0; b < script.Batches.Count; b++)
        {
            var batch = script.Batches[b];
            foreach (var stmt in batch.Statements)
            {
                EmitCommentsBetween(sb, tokens, cursor, stmt.FirstTokenIndex);
                AppendStatement(sb, tokens, gen, stmt);
                cursor = stmt.LastTokenIndex + 1;
            }
            if (b < script.Batches.Count - 1)
            { sb.Append("GO\n"); } // GO separates batches; not a statement
        }
        // Trailing comments after the last statement (e.g. a footer banner).
        EmitCommentsBetween(sb, tokens, cursor, tokens.Count);

        return ResolvePlaceholders(sb.ToString(), replacements);
    }

    /// <summary>Regenerates one statement from its AST and appends it, re-adding a trailing semicolon
    /// if the source had one. ScriptDom's generator has no notion of "was semicolon-terminated" — a
    /// semicolon is just an optional separator token sitting in the gap after the statement, the same
    /// gap <see cref="EmitCommentsBetween"/> scans for comments — so without this, a semicolon the
    /// author wrote would silently vanish on every reformat, regardless of whether T-SQL requires one
    /// here.</summary>
    private static void AppendStatement(StringBuilder sb, IList<TSqlParserToken> tokens, Sql150ScriptGenerator gen, TSqlStatement stmt)
    {
        gen.GenerateScript(stmt, out var formatted);
        sb.Append(formatted.TrimEnd());
        if (HasTrailingSemicolon(tokens, stmt.LastTokenIndex))
        {
            sb.Append(';');
        }
        sb.Append('\n');
    }

    /// <summary>Whether the first non-whitespace token after a statement's own last token is a
    /// semicolon — i.e. whether the source terminated this particular statement with one.</summary>
    private static bool HasTrailingSemicolon(IList<TSqlParserToken> tokens, int lastTokenIndex)
    {
        for (var i = lastTokenIndex + 1; i < tokens.Count; i++)
        {
            TSqlTokenType type = tokens[i].TokenType;
            if (type == TSqlTokenType.WhiteSpace)
            {
                continue;
            }
            return type == TSqlTokenType.Semicolon;
        }
        return false;
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

    /// <summary>Replaces each placeholder marker's whole line with its real text, re-indented to
    /// match the placeholder's own rendered indentation. A replacement can itself still contain a
    /// deeper marker (a nested BEGIN...END inside this one), so this repeats until none remain —
    /// bounded by nesting depth, which is always finite.</summary>
    private static string ResolvePlaceholders(string text, Dictionary<string, string> replacements)
    {
        if (replacements.Count == 0)
        {
            return text;
        }

        bool resolvedAny;
        do
        {
            resolvedAny = false;
            foreach ((string marker, string replacement) in replacements)
            {
                int at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                int lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
                int lineEnd = text.IndexOf('\n', at);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }
                string indent = text[lineStart..at];
                // indent may carry leading text before the marker (there is none — the marker is the
                // whole statement) but guard against a non-whitespace prefix by taking only the
                // leading whitespace, so a coincidental match mid-line never mis-indents.
                indent = new string(indent.TakeWhile(char.IsWhiteSpace).ToArray());

                string indented = replacement.Length == 0
                    ? ""
                    : string.Join('\n', replacement.Split('\n').Select(line => line.Length == 0 ? line : indent + line));

                text = text[..lineStart] + indented + (lineEnd < text.Length ? "\n" : "") + text[Math.Min(lineEnd + 1, text.Length)..];
                resolvedAny = true;
            }
        } while (resolvedAny);

        return text;
    }

    /// <summary>Walks the whole fragment tree and, for every BEGIN...END body found (at any depth,
    /// deepest first via post-order traversal), replaces its statement list with a single
    /// placeholder statement carrying a unique marker, recording the body's own comment-preserving
    /// text under that marker. A body with nothing in it — no statements and no comment — is left
    /// alone, since there is nothing for the ordinary single-shot generation to lose.</summary>
    private sealed class BeginEndCommentSplicer : TSqlFragmentVisitor
    {
        private readonly IList<TSqlParserToken> _tokens;
        private readonly Sql150ScriptGenerator _gen;
        private readonly Dictionary<string, string> _replacements;
        private int _nextMarker;

        public BeginEndCommentSplicer(IList<TSqlParserToken> tokens, Sql150ScriptGenerator gen, Dictionary<string, string> replacements)
        {
            _tokens = tokens;
            _gen = gen;
            _replacements = replacements;
        }

        public override void ExplicitVisit(BeginEndBlockStatement node)
        {
            base.ExplicitVisit(node); // children first: nested BEGIN...END bodies mutate before this one is read

            // No StatementList to mutate: leave this body to the ordinary single-shot generation
            // rather than risk a crash on an assumption about ScriptDom's internals. Loss of a
            // between-statement comment here is the pre-existing limitation; a thrown exception
            // would fail the whole file, which is strictly worse.
            if (node.StatementList is not { } statementList)
            {
                return;
            }

            IList<TSqlStatement> statements = statementList.Statements;
            if (statements.Count == 0 && !HasCommentInRange(_tokens, node.FirstTokenIndex, node.LastTokenIndex))
            {
                return; // nothing this body would lose by being generated the ordinary way
            }

            var sb = new StringBuilder();
            var cursor = node.FirstTokenIndex;
            foreach (TSqlStatement stmt in statements)
            {
                EmitCommentsBetween(sb, _tokens, cursor, stmt.FirstTokenIndex);
                AppendStatement(sb, _tokens, _gen, stmt);
                cursor = stmt.LastTokenIndex + 1;
            }
            EmitCommentsBetween(sb, _tokens, cursor, node.LastTokenIndex);

            string replacement = sb.ToString().TrimEnd('\n');
            // The trailing '_' guarantees no marker is ever a string-prefix of another (marker 1
            // would otherwise prefix marker 10, 11, ... 19), which the single-pass substring search
            // in ResolvePlaceholders depends on to avoid matching the wrong marker's line.
            string marker = PlaceholderPrefix + _nextMarker++ + "_";
            _replacements[marker] = replacement;

            statements.Clear();
            statements.Add(new PrintStatement
            {
                Expression = new StringLiteral { Value = marker }
            });
        }

        private static bool HasCommentInRange(IList<TSqlParserToken> tokens, int from, int toExclusive)
        {
            for (var i = from; i < toExclusive && i < tokens.Count; i++)
            {
                if (tokens[i].TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>True if the parsed content contains a comment token that lies inside a statement's
    /// token span in a way <see cref="Format"/> cannot preserve — i.e. it is not in a gap between
    /// top-level statements or between statements inside a BEGIN...END body, at any depth. Callers
    /// surface this as a diagnostic.</summary>
    public static bool HasInlineComments(string content)
    {
        using var reader = new StringReader(content);
        var fragment = new TSql150Parser(initialQuotedIdentifiers: true).Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
        { return false; }

        var tokens = fragment.ScriptTokenStream;
        var gaps = new List<(int From, int ToExclusive)>();
        var cursor = 0;
        foreach (TSqlBatch batch in script.Batches)
        {
            foreach (TSqlStatement stmt in batch.Statements)
            {
                gaps.Add((cursor, stmt.FirstTokenIndex));
                stmt.Accept(new GapCollector(gaps));
                cursor = stmt.LastTokenIndex + 1;
            }
        }
        gaps.Add((cursor, tokens.Count));

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
            {
                continue;
            }
            int index = i;
            if (!gaps.Any(g => index >= g.From && index < g.ToExclusive))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Finds every BEGIN...END body's gap ranges (before its first statement, between each
    /// pair, and after its last, up to its own closing token) at any nesting depth — the same
    /// regions <see cref="BeginEndCommentSplicer"/> preserves during formatting.</summary>
    private sealed class GapCollector : TSqlFragmentVisitor
    {
        private readonly List<(int From, int ToExclusive)> _gaps;

        public GapCollector(List<(int From, int ToExclusive)> gaps) => _gaps = gaps;

        public override void ExplicitVisit(BeginEndBlockStatement node)
        {
            var cursor = node.FirstTokenIndex;
            foreach (TSqlStatement stmt in node.StatementList?.Statements ?? Array.Empty<TSqlStatement>())
            {
                _gaps.Add((cursor, stmt.FirstTokenIndex));
                cursor = stmt.LastTokenIndex + 1;
            }
            _gaps.Add((cursor, node.LastTokenIndex));
            base.ExplicitVisit(node);
        }
    }
}
