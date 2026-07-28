using System.Text.RegularExpressions;
using Archon.Core.Findings;

namespace Archon.Core.Configuration;

/// <summary>
/// Reads inline suppression markers, which every rule honours without needing to know they
/// exist. One syntax covers all rules: a marker naming rule ids silences only those, and a bare
/// marker silences every rule. Without a way to dismiss a single wrong result, one false
/// positive is enough to make a whole tool unusable, so this is applied uniformly by the engine.
/// </summary>
public sealed partial class SuppressionIndex
{
    private const string LineMarker = "archon-ignore";
    private const string FileMarker = "archon-ignore-file";

    private readonly Dictionary<int, HashSet<string>?> _byLine = new();
    private readonly HashSet<string>? _fileWide;
    private readonly bool _fileWideAll;

    [GeneratedRegex(@"archon-ignore(?<file>-file)?(?:\s*\[(?<ids>[^\]]*)\])?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    private SuppressionIndex(Dictionary<int, HashSet<string>?> byLine, HashSet<string>? fileWide, bool fileWideAll)
    {
        _byLine = byLine;
        _fileWide = fileWide;
        _fileWideAll = fileWideAll;
    }

    public static readonly SuppressionIndex Empty = new(new Dictionary<int, HashSet<string>?>(), null, false);

    /// <summary>
    /// Builds an index from source text. A marker applies to its own line and to the line
    /// following it, so a suppression can sit above the statement it concerns rather than
    /// lengthening it.
    /// </summary>
    public static SuppressionIndex Build(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(LineMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Empty;
        }

        var byLine = new Dictionary<int, HashSet<string>?>();
        HashSet<string>? fileWide = null;
        bool fileWideAll = false;
        string[] lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match match in MarkerPattern().Matches(lines[i]))
            {
                bool isFileWide = match.Groups["file"].Success;
                HashSet<string>? ids = ParseIds(match.Groups["ids"]);

                if (isFileWide)
                {
                    if (ids is null)
                    {
                        fileWideAll = true;
                    }
                    else
                    {
                        fileWide ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        fileWide.UnionWith(ids);
                    }
                    continue;
                }

                Merge(byLine, i, ids);
                Merge(byLine, i + 1, ids);
            }
        }

        return new SuppressionIndex(byLine, fileWide, fileWideAll);
    }

    public bool IsSuppressed(string ruleId, int line)
    {
        if (_fileWideAll || _fileWide?.Contains(ruleId) == true)
        {
            return true;
        }
        if (!_byLine.TryGetValue(line, out HashSet<string>? ids))
        {
            return false;
        }
        return ids is null || ids.Contains(ruleId);
    }

    public bool IsSuppressed(Finding finding) => IsSuppressed(finding.RuleId, finding.Span.StartLine);

    private static void Merge(Dictionary<int, HashSet<string>?> byLine, int line, HashSet<string>? ids)
    {
        if (!byLine.TryGetValue(line, out HashSet<string>? existing))
        {
            byLine[line] = ids is null ? null : new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            return;
        }
        if (existing is null || ids is null)
        {
            byLine[line] = null;
            return;
        }
        existing.UnionWith(ids);
    }

    /// <summary>Returns <c>null</c> for a bare marker, meaning every rule on that line.</summary>
    private static HashSet<string>? ParseIds(Group group)
    {
        if (!group.Success)
        {
            return null;
        }
        var ids = group.Value
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Count == 0 ? null : ids;
    }
}
