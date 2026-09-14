using System.Text;

namespace Archon.Core.Findings;

/// <summary>
/// Applies fixes to a file's text. Edits are positioned by line and column, as findings are, so
/// this is the one place that turns them back into offsets — and the one place that decides what
/// happens when two fixes want the same characters: the first in document order is applied and
/// any that overlaps it is left for a later pass, once the file has been re-analysed. Applying
/// both would corrupt the text, and guessing at a merge is worse than doing one at a time.
/// </summary>
public static class FixApplier
{
    /// <summary>What one pass did: the new text, and how many fixes it took and left.</summary>
    public sealed record Outcome(string Text, int Applied, int Deferred);

    public static Outcome Apply(string text, IEnumerable<FindingFix> fixes)
    {
        int[] lineStarts = LineStarts(text);

        // Every edit is resolved to offsets first, and a fix is only ever applied whole: a fix
        // with two edits where one collides is deferred entirely rather than half-applied.
        var resolved = new List<(int Start, int End, string NewText)[]>();
        foreach (FindingFix fix in fixes)
        {
            var edits = new (int Start, int End, string NewText)[fix.Edits.Count];
            bool valid = true;
            for (int i = 0; i < edits.Length && valid; i++)
            {
                TextEdit edit = fix.Edits[i];
                int? start = Offset(lineStarts, text.Length, edit.Span.StartLine, edit.Span.StartColumn);
                int? end = Offset(lineStarts, text.Length, edit.Span.EndLine, edit.Span.EndColumn);
                valid = start is not null && end is not null && end >= start;
                if (valid)
                {
                    edits[i] = (start!.Value, end!.Value, edit.NewText);
                }
            }
            if (valid)
            {
                resolved.Add(edits);
            }
        }

        // Fixes are taken in document order so the outcome does not depend on the order rules ran.
        resolved.Sort((a, b) => a.Min(e => e.Start).CompareTo(b.Min(e => e.Start)));

        var accepted = new List<(int Start, int End, string NewText)>();
        int deferred = 0;
        foreach ((int Start, int End, string NewText)[] fix in resolved)
        {
            if (CollidesWithin(fix) || fix.Any(edit => accepted.Any(other => Overlaps(edit, other))))
            {
                deferred++;
                continue;
            }
            accepted.AddRange(fix);
        }

        // Applied back to front so earlier offsets stay valid as later text changes length.
        accepted.Sort((a, b) => b.Start.CompareTo(a.Start));
        var builder = new StringBuilder(text);
        foreach ((int start, int end, string newText) in accepted)
        {
            builder.Remove(start, end - start);
            builder.Insert(start, newText);
        }

        return new Outcome(builder.ToString(), resolved.Count - deferred, deferred);
    }

    /// <summary>
    /// Two regions collide when one starts inside the other, or when both start at the same
    /// offset — two insertions at one point have no defined order, so neither is taken blindly.
    /// </summary>
    private static bool Overlaps((int Start, int End, string NewText) a, (int Start, int End, string NewText) b) =>
        a.Start == b.Start || (a.Start < b.End && b.Start < a.End);

    private static bool CollidesWithin((int Start, int End, string NewText)[] edits)
    {
        for (int i = 0; i < edits.Length; i++)
        {
            for (int j = i + 1; j < edits.Length; j++)
            {
                if (Overlaps(edits[i], edits[j]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }

    private static int? Offset(int[] lineStarts, int length, int line, int column)
    {
        if (line < 0 || line >= lineStarts.Length || column < 0)
        {
            return null;
        }
        int offset = lineStarts[line] + column;
        int lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : length;
        return offset <= lineEnd ? offset : null;
    }
}
