using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Archon.Rules.CSharp;

/// <summary>
/// Maps each identifier in a file to its declared type text as written. Several rules need to know
/// what a receiver was declared as without resolving a symbol; collecting it once per file keeps
/// that in one place and keeps the limitation identical wherever it is relied on.
///
/// A variable declared with <c>var</c> is deliberately absent rather than guessed, so a rule that
/// depends on a declared type stays silent instead of speculating.
/// </summary>
internal static class DeclaredTypes
{
    private static readonly ConditionalWeakTable<SyntaxNode, Dictionary<string, string>> Cache = new();

    public static Dictionary<string, string> Collect(SyntaxNode root)
    {
        if (Cache.TryGetValue(root, out Dictionary<string, string>? cached))
        {
            return cached;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ParameterSyntax parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Type is not null)
            {
                map[parameter.Identifier.Text] = parameter.Type.ToString();
            }
        }

        foreach (FieldDeclarationSyntax field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            Record(map, field.Declaration);
        }

        foreach (LocalDeclarationStatementSyntax local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            Record(map, local.Declaration);
        }

        foreach (PropertyDeclarationSyntax property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            map[property.Identifier.Text] = property.Type.ToString();
        }

        Cache.AddOrUpdate(root, map);
        return map;
    }

    private static void Record(Dictionary<string, string> map, VariableDeclarationSyntax declaration)
    {
        string typeText = declaration.Type.ToString();
        if (typeText == "var")
        {
            return;
        }
        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
        {
            map[variable.Identifier.Text] = typeText;
        }
    }

    /// <summary>Reduces a written type to its bare name, dropping qualification and nullability.</summary>
    public static string SimpleName(string typeText)
    {
        string trimmed = typeText.Trim().TrimEnd('?');
        int generic = trimmed.IndexOf('<');
        if (generic >= 0)
        {
            trimmed = trimmed[..generic];
        }
        int dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }
}
