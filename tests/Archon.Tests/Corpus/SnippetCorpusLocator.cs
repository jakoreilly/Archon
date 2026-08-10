namespace Archon.Tests.Corpus;

/// <summary>
/// Finds the vendored library. The test binary runs from bin/Debug/net10.0, so the repository
/// root is found by walking up for the file that marks it. ARCHON_SNIPPET_LIBRARY overrides,
/// for a run against the live library rather than the vendored copy.
/// </summary>
internal static class SnippetCorpusLocator
{
    public const string OverrideVariable = "ARCHON_SNIPPET_LIBRARY";

    /// <summary>The library directory, or null when neither the override nor the vendored copy exists.</summary>
    public static string? Locate()
    {
        string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && Directory.Exists(overridden))
        {
            return overridden;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Archon.slnx")))
            {
                string vendored = Path.Combine(directory.FullName, "tests", "fixtures", "library");
                return Directory.Exists(vendored) ? vendored : null;
            }
            directory = directory.Parent;
        }
        return null;
    }
}
