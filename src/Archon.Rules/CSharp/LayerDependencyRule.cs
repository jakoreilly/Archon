using Archon.Core.Configuration;
using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a reference from one architectural layer to another that the layer rules forbid.
///
/// Layer membership is decided from namespace text as written in the file, not from a resolved
/// namespace symbol, so no build and no target framework are required. A file whose namespace
/// matches no declared layer is never flagged, and alias usings are skipped because resolving an
/// alias target requires semantic information that is deliberately not available here.
/// </summary>
public sealed class LayerDependencyRule : IRule
{
    public const string Id = "AR0001";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            Id,
            "Layer dependency violation",
            "architecture",
            Severity.Warning,
            "A layer references another layer that the configured rules forbid.")
    };

    public RuleScope Scope => RuleScope.File;

    public string Language => RuleLanguages.CSharp;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        LayerConfig layers = context.Config.Layers;
        if (!layers.IsConfigured || context.TargetFile is not SourceFile file)
        {
            yield break;
        }

        ParsedCSharp? parsed = context.Sources.GetCSharp(file.Path);
        if (parsed is null)
        {
            yield break;
        }

        SyntaxNode root = parsed.Root;
        string? fileNamespace = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();
        if (fileNamespace is null)
        {
            yield break;
        }

        string? fileLayer = ResolveLayer(fileNamespace, layers);
        if (fileLayer is null)
        {
            yield break;
        }

        var flaggedLines = new HashSet<int>();

        foreach (UsingDirectiveSyntax usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (usingDirective.Name is null || usingDirective.Alias is not null)
            {
                continue;
            }
            string? targetLayer = ResolveLayer(usingDirective.Name.ToString(), layers);
            if (targetLayer is null || targetLayer == fileLayer)
            {
                continue;
            }
            if (IsAllowed(fileLayer, targetLayer, layers, out string ruleName))
            {
                continue;
            }
            Finding finding = Create(parsed.Tree, usingDirective.Span, file.Path, fileLayer, targetLayer, ruleName);
            flaggedLines.Add(finding.Span.StartLine);
            yield return finding;
        }

        foreach (QualifiedNameSyntax qualifiedName in root.DescendantNodes().OfType<QualifiedNameSyntax>())
        {
            if (qualifiedName.Parent is QualifiedNameSyntax)
            {
                continue;
            }
            if (qualifiedName.Ancestors().OfType<UsingDirectiveSyntax>().Any())
            {
                continue;
            }

            string text = qualifiedName.ToString();
            int lastDot = text.LastIndexOf('.');
            if (lastDot < 0)
            {
                continue;
            }

            string? targetLayer = ResolveLayer(text[..lastDot], layers);
            if (targetLayer is null || targetLayer == fileLayer)
            {
                continue;
            }
            if (IsAllowed(fileLayer, targetLayer, layers, out string ruleName))
            {
                continue;
            }

            int line = parsed.Tree.GetLineSpan(qualifiedName.Span).StartLinePosition.Line;
            if (!flaggedLines.Add(line))
            {
                continue;
            }
            yield return Create(parsed.Tree, qualifiedName.Span, file.Path, fileLayer, targetLayer, ruleName);
        }
    }

    /// <summary>
    /// Returns the layer whose prefix matches a namespace most specifically, which matters when
    /// layers nest and a shorter prefix would otherwise claim a namespace belonging to a longer one.
    /// </summary>
    internal static string? ResolveLayer(string namespaceName, LayerConfig layers)
    {
        string? best = null;
        int bestLength = -1;
        foreach ((string layerName, List<string> prefixes) in layers.Layers)
        {
            foreach (string prefix in prefixes)
            {
                bool matches = namespaceName == prefix
                    || namespaceName.StartsWith(prefix + ".", StringComparison.Ordinal);
                if (matches && prefix.Length > bestLength)
                {
                    best = layerName;
                    bestLength = prefix.Length;
                }
            }
        }
        return best;
    }

    internal static bool IsAllowed(string from, string to, LayerConfig layers, out string ruleName)
    {
        if (string.Equals(layers.Mode, "allowlist", StringComparison.OrdinalIgnoreCase))
        {
            LayerEdge? allowed = layers.Allow.FirstOrDefault(e => e.From == from && e.To == to);
            ruleName = allowed?.Id ?? $"allowlist:{from}->{to}";
            return allowed is not null;
        }
        LayerEdge? denied = layers.Deny.FirstOrDefault(e => e.From == from && e.To == to);
        ruleName = denied?.Id ?? $"denylist:{from}->{to}";
        return denied is null;
    }

    private Finding Create(SyntaxTree tree, TextSpan span, string filePath, string fromLayer, string toLayer, string ruleName)
    {
        LinePositionSpan lineSpan = tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = Id,
            FilePath = filePath,
            Kind = "LayerViolation",
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = $"{fromLayer} must not reference {toLayer} ({ruleName})."
        };
    }
}
