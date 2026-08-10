namespace Archon.Tests.Corpus;

internal static class SnippetExtractor
{
    /// <summary>
    /// Reads one library markdown file. A snippet begins at a heading of the form
    /// "### PUB-BOOT-01 · Host entry point"; every fenced block after it belongs to it until the
    /// next such heading. Blocks of every language are returned — the caller decides what to
    /// analyse — so that a block the corpus skips can still be counted and reported.
    /// </summary>
    public static IReadOnlyList<SnippetBlock> Extract(string markdown, string sourceFile)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<SnippetBlock>();

        string? snippetId = null;
        string title = "";
        int ordinal = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();

            if (TryParseHeading(trimmed, out string headingId, out string headingTitle))
            {
                snippetId = headingId;
                title = headingTitle;
                ordinal = 0;
                continue;
            }

            if (snippetId is null || !IsFenceMarker(trimmed))
            {
                continue;
            }

            string language = trimmed[3..].Trim().ToLowerInvariant();
            int contentStart = i + 1;
            int closeIndex = FindFenceClose(lines, contentStart);
            if (closeIndex < 0)
            {
                continue;
            }

            string text = string.Join("\n", lines[contentStart..closeIndex]);
            blocks.Add(new SnippetBlock(snippetId, title, ordinal, language, text, sourceFile, contentStart));
            ordinal++;
            i = closeIndex;
        }

        return blocks;
    }

    private static bool TryParseHeading(string trimmed, out string id, out string title)
    {
        id = "";
        title = "";
        if (!trimmed.StartsWith("### ", StringComparison.Ordinal))
        {
            return false;
        }
        int separator = trimmed.IndexOf(" · ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }
        id = trimmed[4..separator].Trim();
        title = trimmed[(separator + 3)..].Trim();
        return true;
    }

    private static bool IsFenceMarker(string trimmed) => trimmed.StartsWith("```", StringComparison.Ordinal);

    private static int FindFenceClose(string[] lines, int start)
    {
        for (int i = start; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "```")
            {
                return i;
            }
        }
        return -1;
    }
}
