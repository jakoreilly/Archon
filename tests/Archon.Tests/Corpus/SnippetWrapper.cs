using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Archon.Tests.Corpus;

internal enum SnippetShape
{
    /// <summary>A complete compilation unit already: a type, a namespace, or usings plus a type.</summary>
    Unit,

    /// <summary>Bare member declarations, which must sit inside a type to be seen as members.</summary>
    Member,

    /// <summary>Bare statements, which must sit inside a method body to parse.</summary>
    Statements,

    /// <summary>No candidate shape parsed without a syntax error. Excluded from analysis and reported.</summary>
    Unparseable
}

/// <summary>
/// Text of a block, rewritten so that every rule can see what the block actually declares.
/// <see cref="PrefixLines"/> is how many lines were prepended, so a finding's line can be mapped
/// back to the block's own coordinates.
/// </summary>
internal readonly record struct WrappedSnippet(string Text, SnippetShape Shape, int PrefixLines);

internal static class SnippetWrapper
{
    private const string HostType = "internal static class ArchonSnippetHost";
    private const string HostMethod = "    private static async Task ArchonSnippetBodyAsync()";

    public static WrappedSnippet Wrap(string blockText)
    {
        (string usings, string body) = LeadingUsings(blockText);
        SnippetShape shape = ClassifyShape(body);

        foreach (SnippetShape candidate in Order(shape))
        {
            WrappedSnippet wrapped = Compose(usings, body, candidate);
            if (!HasSyntaxError(wrapped.Text))
            {
                return wrapped;
            }
        }
        return new WrappedSnippet(blockText, SnippetShape.Unparseable, 0);
    }

    /// <summary>
    /// The shape to try first, then the fallbacks. A block that mixes statements with
    /// declarations is tried as Member first, because the declarations are what the
    /// method-shaped rules need to see; Statements is the fallback when that will not parse.
    /// </summary>
    private static SnippetShape[] Order(SnippetShape classified) => classified switch
    {
        SnippetShape.Unit => new[] { SnippetShape.Unit },
        SnippetShape.Member => new[] { SnippetShape.Member, SnippetShape.Statements },
        _ => new[] { SnippetShape.Statements, SnippetShape.Member }
    };

    /// <summary>
    /// Splits off the leading using-directives so a wrapper can be inserted beneath them: a
    /// using inside a class body is a syntax error. A line is a directive only when it starts
    /// with "using ", ends with ';', contains no '=' and no '(' — which keeps
    /// "using var reader = new StringReader(text);" out of the header, where it belongs to the
    /// body instead.
    /// </summary>
    private static (string Usings, string Body) LeadingUsings(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int taken = 0;
        var header = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                taken = i + 1;
                continue;
            }
            if (!IsUsingDirective(line))
            {
                break;
            }
            header.Add(lines[i]);
            taken = i + 1;
        }
        return (string.Join("\n", header), string.Join("\n", lines.Skip(taken)));
    }

    private static bool IsUsingDirective(string trimmed) =>
        trimmed.StartsWith("using ", StringComparison.Ordinal) &&
        trimmed.EndsWith(";", StringComparison.Ordinal) &&
        !trimmed.Contains('=') &&
        !trimmed.Contains('(');

    /// <summary>
    /// Decides the shape from the parse tree's own structure, never from whether the text
    /// parsed. A bare method declaration at file scope parses perfectly happily — as a local
    /// function inside a global statement — so parse success cannot tell a compilation unit
    /// from a loose member. What separates them is whether the root has any
    /// <see cref="GlobalStatementSyntax"/> member at all.
    /// </summary>
    private static SnippetShape ClassifyShape(string body)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(body).GetRoot();
        List<GlobalStatementSyntax> globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count == 0)
        {
            return SnippetShape.Unit;
        }
        return globals.All(g => g.Statement is LocalFunctionStatementSyntax)
            ? SnippetShape.Member
            : SnippetShape.Statements;
    }

    private static WrappedSnippet Compose(string usings, string body, SnippetShape shape) => shape switch
    {
        SnippetShape.Unit => ComposeUnit(usings, body),
        SnippetShape.Member => ComposeMember(usings, body),
        _ => ComposeStatements(usings, body)
    };

    private static WrappedSnippet ComposeUnit(string usings, string body)
    {
        string header = Header(usings);
        return new WrappedSnippet(header + body, SnippetShape.Unit, LineCount(header));
    }

    private static WrappedSnippet ComposeMember(string usings, string body)
    {
        string header = Header(usings) + HostType + "\n{\n";
        return new WrappedSnippet(header + body + "\n}\n", SnippetShape.Member, LineCount(header));
    }

    private static WrappedSnippet ComposeStatements(string usings, string body)
    {
        string header = Header(usings) + HostType + "\n{\n" + HostMethod + "\n    {\n";
        return new WrappedSnippet(header + body + "\n    }\n}\n", SnippetShape.Statements, LineCount(header));
    }

    private static string Header(string usings) => usings.Length == 0 ? "" : usings + "\n\n";

    private static int LineCount(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n');

    /// <summary>
    /// True when the text has a genuine syntax error. The tree is never bound, so only the
    /// parser's own diagnostics can appear: an instance method inside a static host type, or a
    /// modifier the compiler would reject on a local function, are semantic complaints that
    /// require binding and therefore never surface here. That is what makes one host type
    /// serviceable for every member-shaped block, whatever its modifiers.
    /// </summary>
    private static bool HasSyntaxError(string text) =>
        CSharpSyntaxTree.ParseText(text).GetDiagnostics()
            .Any(d => d.Severity == DiagnosticSeverity.Error);
}
