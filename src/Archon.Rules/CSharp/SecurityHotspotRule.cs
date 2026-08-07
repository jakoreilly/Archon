using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags syntactic shapes that are security-sensitive by convention rather than by resolved type: a
/// string literal assigned to a credential-shaped name, a call to a cryptographic primitive known to
/// be weak by its written name, System.Random assigned to a security-shaped name, and a regex
/// pattern whose structure can backtrack catastrophically. Every check is decided from the literal
/// text or the identifier as written, with no symbol resolution, so a type or member sharing one of
/// these names by coincidence is indistinguishable from the real thing -- the same limitation the
/// lifetime rules already carry.
/// </summary>
public sealed class SecurityHotspotRule : IRule
{
    public const string HardcodedCredential = "AR0050";
    public const string WeakCryptographicPrimitive = "AR0051";
    public const string InsecureRandomness = "AR0052";
    public const string CatastrophicBacktrackingRegex = "AR0053";

    private const string Category = "security";

    private static readonly string[] CredentialNameFragments =
    {
        "password", "passwd", "pwd", "secret", "apikey", "api_key", "accesskey", "access_key",
        "clientsecret", "client_secret", "privatekey", "private_key"
    };

    private static readonly HashSet<string> WeakCryptoTypeNames = new(StringComparer.Ordinal)
    {
        "MD5CryptoServiceProvider", "SHA1CryptoServiceProvider", "DESCryptoServiceProvider",
        "TripleDESCryptoServiceProvider", "RC2CryptoServiceProvider", "RijndaelManaged"
    };

    private static readonly HashSet<string> WeakCryptoFactoryTypeNames = new(StringComparer.Ordinal)
    {
        "MD5", "SHA1", "DES", "TripleDES", "RC2"
    };

    private static readonly string[] SecurityShapedNameFragments =
    {
        "token", "password", "secret", "otp", "nonce", "salt", "sessionid", "session_id", "apikey", "api_key"
    };

    private static readonly HashSet<string> RegexInvocationNames = new(StringComparer.Ordinal)
    {
        "IsMatch", "Match", "Matches", "Replace", "Split"
    };

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            HardcodedCredential,
            "Hardcoded credential",
            Category,
            Severity.Warning,
            "A non-empty string literal is assigned to a name that reads as a credential, so the value may be a real secret committed to source."),
        new RuleDescriptor(
            WeakCryptographicPrimitive,
            "Weak cryptographic primitive",
            Category,
            Severity.Warning,
            "A cryptographic type known to be weak by its written name is constructed or invoked."),
        new RuleDescriptor(
            InsecureRandomness,
            "System.Random used for a security-shaped value",
            Category,
            Severity.Information,
            "System.Random is not cryptographically secure; a value named for a security purpose may need RandomNumberGenerator instead."),
        new RuleDescriptor(
            CatastrophicBacktrackingRegex,
            "Regular expression can backtrack catastrophically",
            Category,
            Severity.Warning,
            "A quantified group is itself quantified, so a crafted input can make matching take exponential time.")
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

        var findings = new List<Finding>();
        if (context.IsEnabled(HardcodedCredential))
        {
            findings.AddRange(FindHardcodedCredentials(parsed, file.Path));
        }
        if (context.IsEnabled(WeakCryptographicPrimitive))
        {
            findings.AddRange(FindWeakCrypto(parsed, file.Path));
        }
        if (context.IsEnabled(InsecureRandomness))
        {
            findings.AddRange(FindInsecureRandomness(parsed, file.Path));
        }
        if (context.IsEnabled(CatastrophicBacktrackingRegex))
        {
            findings.AddRange(FindBacktrackingRegex(parsed, file.Path));
        }
        return findings;
    }

    private IEnumerable<Finding> FindHardcodedCredentials(ParsedCSharp parsed, string filePath)
    {
        foreach (VariableDeclaratorSyntax declarator in parsed.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is not LiteralExpressionSyntax literal ||
                !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }
            if (!NameLooksLikeCredential(declarator.Identifier.Text) || string.IsNullOrEmpty(literal.Token.ValueText))
            {
                continue;
            }
            yield return Create(HardcodedCredential, parsed, declarator.Span, filePath, "HardcodedCredential",
                $"'{declarator.Identifier.Text}' is initialised from a string literal; move the value out of source into configuration or a secret store.");
        }

        foreach (AssignmentExpressionSyntax assignment in parsed.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                assignment.Right is not LiteralExpressionSyntax literal ||
                !literal.IsKind(SyntaxKind.StringLiteralExpression) ||
                string.IsNullOrEmpty(literal.Token.ValueText))
            {
                continue;
            }
            string? name = assignment.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                _ => null
            };
            if (name is null || !NameLooksLikeCredential(name))
            {
                continue;
            }
            yield return Create(HardcodedCredential, parsed, assignment.Span, filePath, "HardcodedCredential",
                $"'{name}' is assigned a string literal; move the value out of source into configuration or a secret store.");
        }
    }

    private static bool NameLooksLikeCredential(string identifier)
    {
        string lowered = identifier.ToLowerInvariant();
        return CredentialNameFragments.Any(lowered.Contains);
    }

    private IEnumerable<Finding> FindWeakCrypto(ParsedCSharp parsed, string filePath)
    {
        foreach (ObjectCreationExpressionSyntax creation in parsed.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            string typeName = DeclaredTypes.SimpleName(creation.Type.ToString());
            if (!WeakCryptoTypeNames.Contains(typeName))
            {
                continue;
            }
            yield return Create(WeakCryptographicPrimitive, parsed, creation.Span, filePath, "WeakCryptographicPrimitive",
                $"'{typeName}' is a cryptographically weak algorithm; use SHA256/SHA512 or AesGcm instead.");
        }

        foreach (InvocationExpressionSyntax invocation in parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member || member.Name.Identifier.Text != "Create")
            {
                continue;
            }
            string typeName = member.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.Text,
                _ => ""
            };
            if (!WeakCryptoFactoryTypeNames.Contains(typeName))
            {
                continue;
            }
            yield return Create(WeakCryptographicPrimitive, parsed, invocation.Span, filePath, "WeakCryptographicPrimitive",
                $"'{typeName}.Create()' produces a cryptographically weak algorithm; use SHA256/SHA512 or AesGcm instead.");
        }
    }

    private IEnumerable<Finding> FindInsecureRandomness(ParsedCSharp parsed, string filePath)
    {
        foreach (VariableDeclaratorSyntax declarator in parsed.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is not ObjectCreationExpressionSyntax creation ||
                DeclaredTypes.SimpleName(creation.Type.ToString()) != "Random")
            {
                continue;
            }
            if (!NameLooksSecurityShaped(declarator.Identifier.Text))
            {
                continue;
            }
            yield return Create(InsecureRandomness, parsed, declarator.Span, filePath, "InsecureRandomness",
                $"'{declarator.Identifier.Text}' is generated with System.Random, which is predictable; use RandomNumberGenerator for a security-sensitive value.");
        }
    }

    private static bool NameLooksSecurityShaped(string identifier)
    {
        string lowered = identifier.ToLowerInvariant();
        return SecurityShapedNameFragments.Any(lowered.Contains);
    }

    private IEnumerable<Finding> FindBacktrackingRegex(ParsedCSharp parsed, string filePath)
    {
        foreach (LiteralExpressionSyntax literal in parsed.Root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression) || !IsRegexPatternPosition(literal))
            {
                continue;
            }
            if (!HasNestedQuantifier(literal.Token.ValueText))
            {
                continue;
            }
            yield return Create(CatastrophicBacktrackingRegex, parsed, literal.Span, filePath, "CatastrophicBacktrackingRegex",
                "This pattern quantifies a group that itself contains a quantified atom; a crafted input can make matching take exponential time. Anchor the group or make its inner quantifier possessive/atomic.");
        }
    }

    private static bool IsRegexPatternPosition(LiteralExpressionSyntax literal)
    {
        if (literal.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } } &&
            invocation.Expression is MemberAccessExpressionSyntax member &&
            RegexInvocationNames.Contains(member.Name.Identifier.Text) &&
            IsRegexTypeReceiver(member.Expression))
        {
            return true;
        }
        if (literal.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: ObjectCreationExpressionSyntax creation } } &&
            DeclaredTypes.SimpleName(creation.Type.ToString()) == "Regex")
        {
            return true;
        }
        return false;
    }

    private static bool IsRegexTypeReceiver(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text == "Regex",
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text == "Regex",
        _ => false
    };

    /// <summary>
    /// Scans a regex pattern's own text for a quantified group that itself contains a quantified
    /// atom -- e.g. <c>(a+)+</c> or <c>([a-z]*)+</c> -- the shape that makes backtracking
    /// exponential. A character class is skipped whole so a quantifier inside <c>[...]</c> is never
    /// mistaken for one outside it, and an escaped character is skipped so <c>\(</c> is never read
    /// as a group boundary. This is a heuristic over one level of nesting, not an exhaustive
    /// backtracking analysis.
    /// </summary>
    private static bool HasNestedQuantifier(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '[')
            {
                i = SkipCharacterClass(pattern, i);
                continue;
            }
            if (c != '(')
            {
                continue;
            }

            int close = FindMatchingParen(pattern, i);
            if (close < 0)
            {
                return false;
            }
            string inner = pattern[(i + 1)..close];
            bool groupIsQuantified = close + 1 < pattern.Length && IsQuantifier(pattern[close + 1]);
            if (groupIsQuantified && ContainsQuantifiedAtom(inner))
            {
                return true;
            }
            i = close;
        }
        return false;
    }

    private static int SkipCharacterClass(string pattern, int openBracket)
    {
        int i = openBracket + 1;
        if (i < pattern.Length && pattern[i] == '^')
        {
            i++;
        }
        if (i < pattern.Length && pattern[i] == ']')
        {
            i++;
        }
        while (i < pattern.Length && pattern[i] != ']')
        {
            if (pattern[i] == '\\')
            {
                i++;
            }
            i++;
        }
        return i < pattern.Length ? i : pattern.Length - 1;
    }

    private static int FindMatchingParen(string pattern, int open)
    {
        int depth = 0;
        for (int i = open; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '[')
            {
                i = SkipCharacterClass(pattern, i);
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static bool ContainsQuantifiedAtom(string inner)
    {
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '[')
            {
                i = SkipCharacterClass(inner, i);
                continue;
            }
            if (IsQuantifier(c) && i > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsQuantifier(char c) => c is '+' or '*' or '{';

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
