using System.Text.RegularExpressions;

namespace Archon.Core.Sources;

/// <summary>A discovered project and the files beneath it.</summary>
public sealed record ProjectModel(string ProjectFilePath, string Directory, IReadOnlyList<SourceFile> Files)
{
    public string Name => Path.GetFileNameWithoutExtension(ProjectFilePath);
}

/// <summary>
/// The set of files under analysis, discovered once per pass. Discovery is filesystem-only: no
/// project system is loaded and no build is required, so a workspace that does not compile is
/// still fully analysable.
/// </summary>
public sealed class WorkspaceModel
{
    private WorkspaceModel(string root, IReadOnlyList<SourceFile> files, IReadOnlyList<ProjectModel> projects)
    {
        Root = root;
        Files = files;
        Projects = projects;
    }

    public string Root { get; }

    public IReadOnlyList<SourceFile> Files { get; }

    public IReadOnlyList<ProjectModel> Projects { get; }

    public IEnumerable<SourceFile> FilesOfLanguage(string language) =>
        language == Rules.RuleLanguages.Any ? Files : Files.Where(f => f.Language == language);

    /// <summary>Returns the project whose directory most specifically contains a file.</summary>
    public ProjectModel? ProjectOf(string filePath)
    {
        ProjectModel? best = null;
        foreach (ProjectModel project in Projects)
        {
            if (!filePath.StartsWith(project.Directory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (best is null || project.Directory.Length > best.Directory.Length)
            {
                best = project;
            }
        }
        return best;
    }

    public static WorkspaceModel Discover(string root, IReadOnlyList<string> excludeGlobs)
    {
        string fullRoot = Path.GetFullPath(root);
        var matcher = new GlobMatcher(excludeGlobs);
        var files = new List<SourceFile>();
        var projectFiles = new List<string>();

        if (Directory.Exists(fullRoot))
        {
            // Source files and project files are collected in a single walk. Enumerating the tree
            // twice doubles the cost of discovery, and discovery is on the path of every request
            // that needs the file set.
            foreach (string path in EnumerateFiles(fullRoot))
            {
                bool isProject = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
                string? language = LanguageOf(path);
                if (language is null && !isProject)
                {
                    continue;
                }
                string relative = Path.GetRelativePath(fullRoot, path).Replace('\\', '/');
                if (matcher.IsExcluded(relative))
                {
                    continue;
                }
                if (isProject)
                {
                    projectFiles.Add(path);
                }
                else
                {
                    files.Add(new SourceFile(path, language!));
                }
            }
        }
        else if (File.Exists(fullRoot))
        {
            string? language = LanguageOf(fullRoot);
            if (language is not null)
            {
                files.Add(new SourceFile(fullRoot, language));
            }
            fullRoot = Path.GetDirectoryName(fullRoot) ?? fullRoot;
            projectFiles.AddRange(EnumerateFiles(fullRoot)
                .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Where(p => !matcher.IsExcluded(Path.GetRelativePath(fullRoot, p).Replace('\\', '/'))));
        }

        var projects = BuildProjects(projectFiles, files);
        return new WorkspaceModel(fullRoot, files, projects);
    }

    /// <summary>
    /// Builds a workspace over a caller-supplied file list rather than by scanning a directory,
    /// for a host that already knows which files are in scope or holds them only in memory.
    /// </summary>
    public static WorkspaceModel FromFiles(string root, IReadOnlyList<SourceFile> files) =>
        new(Path.GetFullPath(root), files, Array.Empty<ProjectModel>());

    /// <summary>
    /// Builds a workspace covering the project that owns a file, by walking up for the nearest
    /// project file. This lets a save-triggered pass run project-scope rules at the cost of one
    /// project rather than the whole workspace. Falls back to the file alone when it belongs to no
    /// project.
    /// </summary>
    public static WorkspaceModel DiscoverProjectOf(string filePath, string root, IReadOnlyList<string> excludeGlobs)
    {
        string fullPath = Path.GetFullPath(filePath);
        var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? root);
        string fullRoot = Path.GetFullPath(root);

        while (directory is not null)
        {
            string[] projectFiles = Directory.Exists(directory.FullName)
                ? Directory.GetFiles(directory.FullName, "*.csproj", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            if (projectFiles.Length > 0)
            {
                WorkspaceModel discovered = Discover(directory.FullName, excludeGlobs);
                var projects = projectFiles
                    .Select(p => new ProjectModel(p, directory.FullName, discovered.Files))
                    .ToList();
                return new WorkspaceModel(fullRoot, discovered.Files, projects);
            }

            if (string.Equals(directory.FullName, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            directory = directory.Parent;
        }

        return ForSingleFile(fullPath, fullRoot);
    }

    /// <summary>Builds a workspace containing exactly one file, used for single-file analysis.</summary>
    public static WorkspaceModel ForSingleFile(string filePath, string root)
    {
        string? language = LanguageOf(filePath);
        var files = language is null
            ? Array.Empty<SourceFile>()
            : new[] { new SourceFile(Path.GetFullPath(filePath), language) };
        return new WorkspaceModel(Path.GetFullPath(root), files, Array.Empty<ProjectModel>());
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*", options);
    }

    /// <summary>
    /// Attributes files to the projects that contain them. A file under nested projects belongs to
    /// every project above it, as it did when each project scanned the whole file list for itself.
    ///
    /// The walk is upwards from each file rather than downwards from each project, and the answer
    /// is memoised per directory, so the cost is the number of files rather than the number of
    /// files multiplied by the number of projects.
    /// </summary>
    private static List<ProjectModel> BuildProjects(IReadOnlyList<string> projectFiles, IReadOnlyList<SourceFile> files)
    {
        if (projectFiles.Count == 0)
        {
            return new List<ProjectModel>();
        }

        var directories = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var owned = new List<List<SourceFile>>(projectFiles.Count);
        for (int index = 0; index < projectFiles.Count; index++)
        {
            string directory = Path.GetDirectoryName(projectFiles[index]) ?? projectFiles[index];
            if (!directories.TryGetValue(directory, out List<int>? sharing))
            {
                sharing = new List<int>();
                directories[directory] = sharing;
            }
            sharing.Add(index);
            owned.Add(new List<SourceFile>());
        }

        var ownersByDirectory = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFile file in files)
        {
            string directory = Path.GetDirectoryName(file.Path) ?? file.Path;
            if (!ownersByDirectory.TryGetValue(directory, out List<int>? owners))
            {
                owners = new List<int>();
                // A file sits inside a project when that project's directory is an ancestor of its
                // own, so walking up collects every owner without comparing against each project.
                for (string? current = Path.GetDirectoryName(directory + Path.DirectorySeparatorChar);
                     current is not null;
                     current = Path.GetDirectoryName(current))
                {
                    if (directories.TryGetValue(current, out List<int>? sharing))
                    {
                        owners.AddRange(sharing);
                    }
                }
                ownersByDirectory[directory] = owners;
            }
            foreach (int index in owners)
            {
                owned[index].Add(file);
            }
        }

        var projects = new List<ProjectModel>(projectFiles.Count);
        for (int index = 0; index < projectFiles.Count; index++)
        {
            projects.Add(new ProjectModel(
                projectFiles[index],
                Path.GetDirectoryName(projectFiles[index]) ?? projectFiles[index],
                owned[index]));
        }
        return projects;
    }

    private static string? LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => Rules.RuleLanguages.CSharp,
        ".sql" => Rules.RuleLanguages.Sql,
        _ => null
    };
}

/// <summary>
/// Minimal glob matching over forward-slash relative paths, supporting <c>*</c> within a segment
/// and <c>**</c> across segments. Kept deliberately small: exclusion patterns in configuration
/// are path filters, not a general-purpose expression language.
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<Regex> _patterns;

    public GlobMatcher(IReadOnlyList<string> globs)
    {
        _patterns = globs.Where(g => !string.IsNullOrWhiteSpace(g)).Select(ToRegex).ToList();
    }

    public bool IsExcluded(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        return _patterns.Any(p => p.IsMatch(normalized));
    }

    private static Regex ToRegex(string glob)
    {
        string normalized = glob.Replace('\\', '/').Trim();
        var builder = new System.Text.StringBuilder("^");
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (c == '*')
            {
                bool doubled = i + 1 < normalized.Length && normalized[i + 1] == '*';
                if (doubled)
                {
                    bool trailingSlash = i + 2 < normalized.Length && normalized[i + 2] == '/';
                    builder.Append(trailingSlash ? "(?:.*/)?" : ".*");
                    i += trailingSlash ? 2 : 1;
                }
                else
                {
                    builder.Append("[^/]*");
                }
                continue;
            }
            if (c == '?')
            {
                builder.Append("[^/]");
                continue;
            }
            builder.Append(Regex.Escape(c.ToString()));
        }
        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
