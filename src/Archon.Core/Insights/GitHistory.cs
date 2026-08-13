using System.Diagnostics;

namespace Archon.Core.Insights;

/// <summary>
/// Reads git history by shelling out to the <c>git</c> binary already required to have a
/// repository worth asking these questions about. Every method returns an empty or null result
/// rather than throwing when git is missing or the path is not a repository, so a command that
/// combines this with other signals degrades instead of crashing — the same trust-nothing-external
/// posture <see cref="Configuration.Baseline"/> takes toward its own file.
/// </summary>
public static class GitHistory
{
    /// <summary>Finds the repository containing a path, or null when there is none.</summary>
    public static string? FindRepositoryRoot(string path)
    {
        string directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        string? output = Run(directory, "rev-parse", "--show-toplevel");
        return string.IsNullOrWhiteSpace(output)
            ? null
            : output.Trim().Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Counts commits touching each file since a point in time, keyed by path relative to
    /// <paramref name="repositoryRoot"/> with forward slashes. A file with no commits in the
    /// window is simply absent from the result rather than present with a zero.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ChurnByFile(string repositoryRoot, DateTimeOffset since)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? output = Run(repositoryRoot, "log", $"--since={FormatSince(since)}", "--name-only", "--format=format:");
        if (output is null)
        {
            return counts;
        }
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            counts[line] = counts.GetValueOrDefault(line) + 1;
        }
        return counts;
    }

    /// <summary>One commit that touched a file, oldest information first when read from git log.</summary>
    public readonly record struct FileCommit(string Hash, DateTimeOffset When);

    /// <summary>
    /// Commits that touched <paramref name="relativePath"/>, newest first as git log reports them.
    /// When <paramref name="pickaxe"/> is supplied, only commits that added or removed a line
    /// containing that exact text are returned — <c>git log -S</c> — which is how a value's own
    /// first appearance in a file's history is found without assuming anything about diff shape.
    /// </summary>
    public static IReadOnlyList<FileCommit> CommitsTouching(string repositoryRoot, string relativePath, string? pickaxe = null)
    {
        var arguments = new List<string> { "log", "--follow" };
        if (pickaxe is not null)
        {
            arguments.Add("-S");
            arguments.Add(pickaxe);
        }
        arguments.Add("--format=%H%x1f%aI");
        arguments.Add("--");
        arguments.Add(relativePath);

        string? output = Run(repositoryRoot, arguments.ToArray());
        if (output is null)
        {
            return Array.Empty<FileCommit>();
        }

        var results = new List<FileCommit>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\x1f');
            if (parts.Length == 2 && DateTimeOffset.TryParse(parts[1], out DateTimeOffset when))
            {
                results.Add(new FileCommit(parts[0], when));
            }
        }
        return results;
    }

    /// <summary>The content of a file as it stood at a given commit, or null if that failed.</summary>
    public static string? ShowFileAt(string repositoryRoot, string commitHash, string relativePath) =>
        Run(repositoryRoot, "show", $"{commitHash}:{relativePath}");

    /// <summary>How many commits touched one file since a point in time. Zero when git is
    /// unavailable, so a caller combining this with other signals need not special-case it.</summary>
    public static int CommitCountSince(string repositoryRoot, string relativePath, DateTimeOffset since)
    {
        string? output = Run(repositoryRoot, "log", $"--since={FormatSince(since)}", "--format=%H", "--", relativePath);
        if (output is null)
        {
            return 0;
        }
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Formats a timestamp for <c>--since</c> with an explicit time and offset. A bare
    /// <c>yyyy-MM-dd</c> date is not reliably midnight to git's approxidate parser — it has been
    /// observed to anchor to the current time of day instead, silently excluding same-day commits
    /// made earlier than the moment the command runs — so every caller goes through this.
    /// </summary>
    private static string FormatSince(DateTimeOffset since) => since.ToString("yyyy-MM-ddTHH:mm:sszzz");

    private static string? Run(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }
            string stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
