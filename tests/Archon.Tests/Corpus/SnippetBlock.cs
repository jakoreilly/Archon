namespace Archon.Tests.Corpus;

/// <summary>
/// One fenced code block from the snippet library. <see cref="Ordinal"/> separates the several
/// blocks a single snippet can carry: four snippets in the library have two C# blocks, so the
/// snippet id alone is not an identity.
/// </summary>
internal sealed record SnippetBlock(
    string SnippetId,
    string Title,
    int Ordinal,
    string Language,
    string Text,
    string SourceFile,
    int FirstContentLine);
