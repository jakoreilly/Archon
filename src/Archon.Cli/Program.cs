using System.Text.Json;
using Archon.Core.Configuration;
using Archon.Core.Engine;
using Archon.Core.Explanations;
using Archon.Core.Findings;
using Archon.Core.Insights;
using Archon.Core.Output;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Archon.Core.Sql;
using Archon.Rules;
using Archon.Rules.CSharp;

namespace Archon.Cli;

/// <summary>
/// The command-line surface. It runs the same rules, reads the same configuration and applies the
/// same suppressions and baseline as the editor, so a result seen while writing code and a result
/// that fails a build are the same result rather than two similar checks that can disagree.
/// </summary>
internal static class Program
{
    private const int ExitClean = 0;
    private const int ExitFindings = 1;
    private const int ExitUsage = 2;

    /// <summary>'format --check' found at least one file that would change. Matches the exit code
    /// the standalone sqlfmt-tsql tool uses, so a CI step written against that tool needs no change
    /// to run against 'archon format --check' instead.</summary>
    private const int ExitWouldReformat = 3;

    /// <summary>
    /// Reported by <c>--version</c> and kept in step with the host and the extension by hand. It is
    /// read from the assembly rather than written twice, so it cannot disagree with what shipped.
    /// </summary>
    private static string Version =>
        typeof(Program).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "unknown";

    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? ExitUsage : ExitClean;
        }

        if (IsVersion(args[0]))
        {
            Console.WriteLine($"archon {Version}");
            return ExitClean;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "check" => RunCheck(args[1..]),
                "format" => RunFormat(args[1..]),
                "rules" => RunRules(args[1..]),
                "baseline" => RunBaseline(args[1..]),
                "explain" => RunExplain(args[1..]),
                "init" => RunInit(args[1..]),
                "schema" => RunSchema(args[1..]),
                "hotspots" => RunHotspots(args[1..]),
                "debt" => RunDebt(args[1..]),
                "trend" => RunTrend(args[1..]),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"archon: {ex.Message}");
            return ExitUsage;
        }
    }

    private static bool IsHelp(string argument) =>
        argument is "-h" or "--help" or "help" or "-?" or "/?";

    private static bool IsVersion(string argument) =>
        argument is "--version" or "-v" or "version";

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    private static readonly string[] CommandNames = { "check", "format", "rules", "baseline", "explain", "init", "schema", "hotspots", "debt", "trend" };

    private static int Unknown(string command)
    {
        string? suggestion = ConfigValidator.Nearest(command, CommandNames);
        Console.Error.WriteLine(suggestion is null
            ? $"archon: unknown command '{command}'."
            : $"archon: unknown command '{command}' — did you mean '{suggestion}'?");
        PrintUsage();
        return ExitUsage;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: archon <command> [options]

            Commands:
              check [path]        Analyse a folder or file and report findings.
              format [path]       Format a folder or file of T-SQL in place.
              rules [path]        List every registered rule and its effective severity.
              baseline [path]     Record current findings as accepted, so only new ones fail.
              explain <ruleId>    Describe one rule.
              init [path]         Write a starter .archon.json and its schema.
              schema [path]       Print the JSON Schema for .archon.json.
              hotspots [path]     Rank C# files by complexity x churn. Needs a git repository.
              debt [path]         Rank baseline entries by age x churn since acceptance. Needs git.
              trend [path]        Show baseline finding counts over the baseline file's own history.
              --version           Print the version and exit.

            Options for check:
              --format <name>     console (default), json or sarif.
              --fail-on <level>   error (default), warning, information, hint or never.
              --no-baseline       Ignore the baseline file and report every finding.
              --output <file>     Write the report to a file instead of standard output.

            Options for format:
              --check             Report which files would change, without writing anything.
                                   Exits 3 if any would, 0 if none would.

            Options for init:
              --force             Overwrite an existing .archon.json.

            Options for schema:
              --output <file>     Write to a file instead of standard output.

            Options for hotspots:
              --days <n>          How far back to count commits. Default 180.
              --top <n>           How many files to show. Default 20.
              --format <name>     console (default) or json.

            Options for debt:
              --top <n>           How many entries to show. Default 50, 0 for all.
              --format <name>     console (default) or json.
              --fail-over <age>   Fail if any entry is older than this, e.g. '180d'.

            Options for trend:
              --limit <n>         How many baseline revisions to show, newest first. Default 20, 0 for all.
              --format <name>     console (default) or json.

            Exit codes:
              0  no finding at or above the --fail-on level, or nothing 'format --check' would change
              1  at least one such finding
              2  the command could not run
              3  'format --check' found at least one file that would change

            Configuration entries that Archon cannot act on — an unknown rule id, a severity it
            cannot parse, a layer name that matches nothing — are reported on standard error by
            every command. They never stop a run.
            """);
    }

    private static int RunCheck(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        Options options = Options.Parse(args);
        if (!PathExists(options.Path))
        {
            Console.Error.WriteLine($"archon: '{options.Path}' does not exist.");
            return ExitUsage;
        }
        Session session = Session.Create(options.Path);

        WorkspaceModel workspace = WorkspaceModel.Discover(options.Path, session.Config.EffectiveExcludes());
        Baseline baseline = options.UseBaseline ? session.Baseline : Baseline.Empty;

        AnalysisResult result = session.Engine.AnalyseWorkspace(workspace, session.Config, baseline);
        string report = Reporter.Render(result, session.Engine.Registry, session.Config.WorkspaceRoot, options.Format);

        if (options.OutputPath is not null)
        {
            File.WriteAllText(options.OutputPath, report);
            Console.WriteLine($"Wrote {result.Findings.Count} finding(s) to {options.OutputPath}.");
        }
        else
        {
            Console.Write(report);
        }

        ReportMessages(session);

        if (options.FailOn is null)
        {
            return ExitClean;
        }
        return result.CountAtLeast(options.FailOn.Value) > 0 ? ExitFindings : ExitClean;
    }

    /// <summary>
    /// Formats every <c>*.sql</c> file under a path (or a single file directly) with
    /// <see cref="TSqlFormatter"/> — the same formatter the editor's Format Document command and
    /// the host's <c>formatFile</c> method use, so a file formatted here reads identically whether
    /// it happened on save, from the command line or in CI.
    /// </summary>
    private static int RunFormat(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        bool check = false;
        string? path = null;
        foreach (string argument in args)
        {
            if (argument == "--check")
            {
                check = true;
            }
            else if (argument.StartsWith('-'))
            {
                Console.Error.WriteLine($"archon: unknown option '{argument}' for format.");
                return ExitUsage;
            }
            else
            {
                path = argument;
            }
        }
        path ??= ".";

        if (!PathExists(path))
        {
            Console.Error.WriteLine($"archon: '{path}' does not exist.");
            return ExitUsage;
        }

        List<string> files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.sql", SearchOption.AllDirectories).ToList()
            : new List<string> { path };

        int changed = 0;
        int unchanged = 0;
        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            string formatted = TSqlFormatter.Format(content);

            if (TSqlFormatter.HasInlineComments(content))
            {
                Console.Error.WriteLine(
                    $"archon: note: {file} has comment(s) inside a statement; those cannot be preserved by " +
                    "the formatter and were dropped. Statement-level comments are kept.");
            }

            if (formatted == content)
            {
                unchanged++;
                continue;
            }

            if (check)
            {
                Console.Error.WriteLine($"archon: would reformat: {file}");
            }
            else
            {
                File.WriteAllText(file, formatted);
            }
            changed++;
        }

        Console.WriteLine($"formatted: {changed} file(s), {unchanged} unchanged.");
        return check && changed > 0 ? ExitWouldReformat : ExitClean;
    }

    private static int RunRules(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        Options options = Options.Parse(args);
        Session session = Session.Create(options.Path);

        List<RegisteredRule> rules = session.Engine.Registry.Descriptors
            .OrderBy(r => r.Descriptor.Id, StringComparer.Ordinal)
            .ToList();

        if (options.Format == ReportFormat.Json)
        {
            var payload = rules.Select(registered => new
            {
                id = registered.Descriptor.Id,
                severity = Reporter.Label(session.Config.SeverityFor(registered.Descriptor)),
                scope = registered.Rule.Scope.ToString(),
                category = registered.Descriptor.Category,
                title = registered.Descriptor.Title
            });
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            ReportMessages(session);
            return ExitClean;
        }

        Console.WriteLine($"{"Rule",-8} {"Severity",-12} {"Scope",-10} {"Category",-14} Title");
        foreach (RegisteredRule registered in rules)
        {
            Severity severity = session.Config.SeverityFor(registered.Descriptor);
            Console.WriteLine($"{registered.Descriptor.Id,-8} {Reporter.Label(severity),-12} {registered.Rule.Scope,-10} {registered.Descriptor.Category,-14} {registered.Descriptor.Title}");
        }

        Console.WriteLine();
        Console.WriteLine(session.Config.SourcePath is null
            ? "No .archon.json found; showing default severities."
            : $"Configuration: {session.Config.SourcePath}");
        ReportMessages(session);
        return ExitClean;
    }

    /// <summary>
    /// Writes configuration and rule-pack messages to standard error, so they stay out of a report
    /// being redirected to a file. Every command prints them: an entry that is being ignored is
    /// most likely to be noticed while running <c>rules</c> to find out why a rule is not behaving.
    /// </summary>
    private static void ReportMessages(Session session)
    {
        foreach (string message in session.Messages)
        {
            Console.Error.WriteLine($"archon: {message}");
        }
    }

    private static int RunBaseline(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        Options options = Options.Parse(args);
        if (!PathExists(options.Path))
        {
            Console.Error.WriteLine($"archon: '{options.Path}' does not exist.");
            return ExitUsage;
        }
        Session session = Session.Create(options.Path);

        WorkspaceModel workspace = WorkspaceModel.Discover(options.Path, session.Config.EffectiveExcludes());
        AnalysisResult result = session.Engine.AnalyseWorkspace(workspace, session.Config, Baseline.Empty);

        Baseline.Save(session.BaselinePath, result.Findings, session.Config.WorkspaceRoot);
        Console.WriteLine($"Recorded {result.Findings.Count} finding(s) in {session.BaselinePath}.");
        Console.WriteLine("These are now accepted; only new findings will fail a check.");
        ReportMessages(session);
        return ExitClean;
    }

    private static int RunExplain(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        if (args.Length == 0)
        {
            Console.Error.WriteLine("archon: explain needs a rule id.");
            return ExitUsage;
        }

        Session session = Session.Create(Directory.GetCurrentDirectory());
        RegisteredRule? registered = session.Engine.Registry.Find(args[0]);
        if (registered is null)
        {
            string? suggestion = ConfigValidator.Nearest(
                args[0], session.Engine.Registry.Descriptors.Select(d => d.Descriptor.Id));
            Console.Error.WriteLine(suggestion is null
                ? $"archon: no rule with id '{args[0]}'."
                : $"archon: no rule with id '{args[0]}' — did you mean '{suggestion}'?");
            return ExitUsage;
        }

        Console.WriteLine($"{registered.Descriptor.Id}  {registered.Descriptor.Title}");
        Console.WriteLine($"  category   {registered.Descriptor.Category}");
        Console.WriteLine($"  scope      {registered.Rule.Scope}");
        Console.WriteLine($"  language   {registered.Rule.Language}");
        Console.WriteLine($"  default    {Reporter.Label(registered.Descriptor.DefaultSeverity)}");
        Console.WriteLine($"  effective  {Reporter.Label(session.Config.SeverityFor(registered.Descriptor))}");
        Console.WriteLine($"  pack       {registered.PackName}");
        Console.WriteLine();
        Console.WriteLine($"  {registered.Descriptor.Description}");

        SnippetPointer? pointer = SnippetCatalog.ForRule(registered.Descriptor.Id);
        if (pointer is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  Approved pattern: {pointer.AsProse()}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Suppress one occurrence with: // archon-ignore[{registered.Descriptor.Id}] reason");
        Console.WriteLine($"  Disable entirely in .archon.json: \"rules\": {{ \"{registered.Descriptor.Id}\": \"off\" }}");
        return ExitClean;
    }

    /// <summary>
    /// Writes a starter configuration and the schema describing it. The schema is written beside
    /// the configuration and referenced from it, so completion and hover work in any editor that
    /// reads <c>$schema</c> without a network fetch, a marketplace extension or a URL naming
    /// anyone's server — the file describes the rules this installation has, including private packs.
    /// </summary>
    private static int RunInit(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        Options options = Options.Parse(args);
        string directory = Directory.Exists(options.Path)
            ? System.IO.Path.GetFullPath(options.Path)
            : throw new ArgumentException($"'{options.Path}' is not a directory.");

        string configPath = System.IO.Path.Combine(directory, ConfigLoader.FileName);
        string schemaPath = System.IO.Path.Combine(directory, ConfigSchema.FileName);

        if (File.Exists(configPath) && !options.Force)
        {
            Console.Error.WriteLine($"archon: {configPath} already exists. Pass --force to overwrite it.");
            return ExitUsage;
        }

        Session session = Session.Create(directory);
        File.WriteAllText(schemaPath, ConfigSchema.Generate(session.Engine.Registry));

        // Deliberately minimal. Every key here is one the reader must think about; a scaffold
        // pre-filled with plausible layers and excludes produces a file that looks configured while
        // describing someone else's repository, and the schema already documents what may be added.
        File.WriteAllText(configPath, $$"""
            {
              "$schema": "./{{ConfigSchema.FileName}}",
              "rules": {},
              "exclude": [],
              "rulePacks": [],
              "baseline": ".archon-baseline.json"
            }

            """);

        Console.WriteLine($"Wrote {configPath}");
        Console.WriteLine($"Wrote {schemaPath}  ({session.Engine.Registry.Descriptors.Count} rules described)");
        Console.WriteLine();
        Console.WriteLine("Next:");
        Console.WriteLine("  archon rules .      see every rule and its current severity");
        Console.WriteLine("  archon check .      analyse the workspace");
        Console.WriteLine("  archon baseline .   accept what is already there, so only new findings fail");
        return ExitClean;
    }

    /// <summary>
    /// Prints the schema for the currently registered rules. Separate from <c>init</c> so that a
    /// schema can be refreshed after adding a rule pack without touching an existing configuration.
    /// </summary>
    private static int RunSchema(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        Options options = Options.Parse(args);
        Session session = Session.Create(options.Path);
        string schema = ConfigSchema.Generate(session.Engine.Registry);

        if (options.OutputPath is not null)
        {
            File.WriteAllText(options.OutputPath, schema);
            Console.WriteLine($"Wrote {options.OutputPath} ({session.Engine.Registry.Descriptors.Count} rules described).");
        }
        else
        {
            Console.WriteLine(schema);
        }

        ReportMessages(session);
        return ExitClean;
    }

    /// <summary>
    /// Ranks C# files by cognitive complexity multiplied by how many commits touched them in the
    /// window, so files that are both hard to follow and frequently edited surface first. Needs a
    /// git repository; a workspace without one is told why rather than shown an empty table.
    /// </summary>
    private static int RunHotspots(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        string path = ".";
        int days = 180;
        int top = 20;
        bool json = false;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--days":
                    days = ParsePositiveInt(NextArg(args, ref i, "--days"), "--days");
                    break;
                case "--top":
                    top = ParsePositiveInt(NextArg(args, ref i, "--top"), "--top");
                    break;
                case "--format":
                    string value = NextArg(args, ref i, "--format");
                    json = value.Equals("json", StringComparison.OrdinalIgnoreCase);
                    if (!json && !value.Equals("console", StringComparison.OrdinalIgnoreCase) && !value.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"unknown format '{value}'.");
                    }
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"archon: unknown option '{argument}' for hotspots.");
                        return ExitUsage;
                    }
                    path = argument;
                    break;
            }
        }

        if (!PathExists(path))
        {
            Console.Error.WriteLine($"archon: '{path}' does not exist.");
            return ExitUsage;
        }

        string? repositoryRoot = GitHistory.FindRepositoryRoot(path);
        if (repositoryRoot is null)
        {
            Console.Error.WriteLine("archon: hotspots needs a git repository; none was found.");
            return ExitUsage;
        }

        Session session = Session.Create(path);
        WorkspaceModel workspace = WorkspaceModel.Discover(path, session.Config.EffectiveExcludes());

        var complexityByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFile file in workspace.FilesOfLanguage(RuleLanguages.CSharp))
        {
            ParsedCSharp? parsed = session.Engine.Sources.GetCSharp(file.Path);
            if (parsed is null)
            {
                continue;
            }
            string relative = System.IO.Path.GetRelativePath(repositoryRoot, file.Path).Replace('\\', '/');
            complexityByFile[relative] = ComplexityRule.ScoreFile(parsed);
        }

        IReadOnlyDictionary<string, int> churnByFile = GitHistory.ChurnByFile(repositoryRoot, DateTimeOffset.UtcNow.AddDays(-days));
        IReadOnlyList<HotspotEntry> ranked = HotspotAnalyzer.Rank(complexityByFile, churnByFile, top);

        if (json)
        {
            var payload = ranked.Select(e => new { file = e.File, complexity = e.Complexity, churn = e.ChurnCommits, score = e.Score });
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        }
        else if (ranked.Count == 0)
        {
            Console.WriteLine($"No file was both scored and changed in the last {days} day(s).");
        }
        else
        {
            Console.WriteLine($"{"Score",-8} {"Complexity",-11} {"Churn",-6} File");
            foreach (HotspotEntry entry in ranked)
            {
                Console.WriteLine($"{entry.Score,-8} {entry.Complexity,-11} {entry.ChurnCommits,-6} {entry.File}");
            }
        }

        ReportMessages(session);
        return ExitClean;
    }

    /// <summary>
    /// Ranks baseline entries by how long ago they were accepted, multiplied by how much their
    /// file has changed since. A suppression's own birthday is found by searching the baseline
    /// file's history for the commit that first introduced its fingerprint text — the same
    /// pickaxe technique <c>git log -S</c> uses for any other string. Needs a git repository.
    /// </summary>
    private static int RunDebt(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        string path = ".";
        int top = 50;
        bool json = false;
        int? failOverDays = null;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--top":
                    top = ParseNonNegativeInt(NextArg(args, ref i, "--top"), "--top");
                    break;
                case "--format":
                    string value = NextArg(args, ref i, "--format");
                    json = value.Equals("json", StringComparison.OrdinalIgnoreCase);
                    if (!json && !value.Equals("console", StringComparison.OrdinalIgnoreCase) && !value.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"unknown format '{value}'.");
                    }
                    break;
                case "--fail-over":
                    failOverDays = ParseDays(NextArg(args, ref i, "--fail-over"));
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"archon: unknown option '{argument}' for debt.");
                        return ExitUsage;
                    }
                    path = argument;
                    break;
            }
        }

        if (!PathExists(path))
        {
            Console.Error.WriteLine($"archon: '{path}' does not exist.");
            return ExitUsage;
        }

        string? repositoryRoot = GitHistory.FindRepositoryRoot(path);
        if (repositoryRoot is null)
        {
            Console.Error.WriteLine("archon: debt needs a git repository; none was found.");
            return ExitUsage;
        }

        Session session = Session.Create(path);
        if (session.Baseline.Entries.Count == 0)
        {
            Console.WriteLine("No baseline entries.");
            ReportMessages(session);
            return ExitClean;
        }

        string relativeBaselinePath = System.IO.Path.GetRelativePath(repositoryRoot, session.BaselinePath).Replace('\\', '/');
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var introducedByFingerprint = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var churnByFingerprint = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (BaselineEntry entry in session.Baseline.Entries)
        {
            IReadOnlyList<GitHistory.FileCommit> matches =
                GitHistory.CommitsTouching(repositoryRoot, relativeBaselinePath, pickaxe: entry.Fingerprint);
            if (matches.Count == 0)
            {
                continue;
            }
            // Newest first, as git log reports it: the last match is the oldest, and therefore
            // the commit that first introduced this fingerprint's text.
            DateTimeOffset introduced = matches[^1].When;
            introducedByFingerprint[entry.Fingerprint] = introduced;
            churnByFingerprint[entry.Fingerprint] = GitHistory.CommitCountSince(repositoryRoot, entry.File, introduced);
        }

        IReadOnlyList<DebtEntry> ranked = DebtAnalyzer.Rank(session.Baseline.Entries, introducedByFingerprint, churnByFingerprint, now);
        IReadOnlyList<DebtEntry> shown = top > 0 ? ranked.Take(top).ToList() : ranked;

        if (json)
        {
            var payload = shown.Select(e => new
            {
                fingerprint = e.Fingerprint,
                ruleId = e.RuleId,
                file = e.File,
                introduced = e.Introduced?.ToString("yyyy-MM-dd"),
                ageDays = e.AgeDays,
                churn = e.ChurnCommits,
                score = e.Score
            });
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        }
        else
        {
            Console.WriteLine($"{"Score",-8} {"Age",-8} {"Churn",-6} {"Rule",-8} File");
            foreach (DebtEntry entry in shown)
            {
                string age = entry.Introduced is null ? "unknown" : FormatAge(entry.AgeDays);
                Console.WriteLine($"{entry.Score,-8} {age,-8} {entry.ChurnCommits,-6} {entry.RuleId,-8} {entry.File}");
            }
            Console.WriteLine();
            string suffix = top > 0 && ranked.Count > top ? $", showing the {top} oldest x churniest" : "";
            Console.WriteLine($"{ranked.Count} baseline entrie(s){suffix}.");
        }

        ReportMessages(session);

        if (failOverDays is null)
        {
            return ExitClean;
        }
        return ranked.Any(e => e.AgeDays > failOverDays.Value) ? ExitFindings : ExitClean;
    }

    /// <summary>
    /// Reads the baseline file's own git history and turns it into a time series of finding
    /// counts. The baseline already lives in git, so this needs no storage of its own — walking a
    /// handful of past revisions is all it takes. Needs a git repository.
    /// </summary>
    private static int RunTrend(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return ExitClean;
        }

        string path = ".";
        int limit = 20;
        bool json = false;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--limit":
                    limit = ParseNonNegativeInt(NextArg(args, ref i, "--limit"), "--limit");
                    break;
                case "--format":
                    string value = NextArg(args, ref i, "--format");
                    json = value.Equals("json", StringComparison.OrdinalIgnoreCase);
                    if (!json && !value.Equals("console", StringComparison.OrdinalIgnoreCase) && !value.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"unknown format '{value}'.");
                    }
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"archon: unknown option '{argument}' for trend.");
                        return ExitUsage;
                    }
                    path = argument;
                    break;
            }
        }

        if (!PathExists(path))
        {
            Console.Error.WriteLine($"archon: '{path}' does not exist.");
            return ExitUsage;
        }

        string? repositoryRoot = GitHistory.FindRepositoryRoot(path);
        if (repositoryRoot is null)
        {
            Console.Error.WriteLine("archon: trend needs a git repository; none was found.");
            return ExitUsage;
        }

        Session session = Session.Create(path);
        string relativeBaselinePath = System.IO.Path.GetRelativePath(repositoryRoot, session.BaselinePath).Replace('\\', '/');

        IReadOnlyList<GitHistory.FileCommit> commits = GitHistory.CommitsTouching(repositoryRoot, relativeBaselinePath);
        if (commits.Count == 0)
        {
            Console.WriteLine($"No history found for {relativeBaselinePath}.");
            ReportMessages(session);
            return ExitClean;
        }

        IEnumerable<GitHistory.FileCommit> selected = limit > 0 ? commits.Take(limit) : commits;

        var snapshots = new List<BaselineSnapshot>();
        // 'selected' is newest first, as git log reports it; walk oldest to newest so the trend
        // reads left to right in time.
        foreach (GitHistory.FileCommit commit in selected.Reverse())
        {
            string? content = GitHistory.ShowFileAt(repositoryRoot, commit.Hash, relativeBaselinePath);
            if (content is null)
            {
                continue;
            }
            try
            {
                Baseline atCommit = Baseline.Parse(content);
                snapshots.Add(new BaselineSnapshot(commit.Hash, commit.When, atCommit.Entries));
            }
            catch (JsonException)
            {
                // A revision that predates the current baseline format, or content that does not
                // parse for some other reason. Skipped rather than counted as zero findings, so a
                // gap in the series is not misread as debt having been paid off.
            }
        }

        IReadOnlyList<TrendPoint> points = TrendAnalyzer.Summarize(snapshots);

        if (json)
        {
            var payload = points.Select(p => new
            {
                commit = p.CommitHash[..Math.Min(10, p.CommitHash.Length)],
                date = p.When.ToString("yyyy-MM-dd"),
                total = p.Total,
                delta = p.DeltaFromPrevious,
                byRule = p.ByRule
            });
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        }
        else if (points.Count == 0)
        {
            Console.WriteLine("No parseable baseline revision was found in the selected history.");
        }
        else
        {
            Console.WriteLine($"{"Commit",-10} {"Date",-12} {"Total",-7} Delta");
            foreach (TrendPoint point in points)
            {
                string delta = point.DeltaFromPrevious switch
                {
                    > 0 => $"+{point.DeltaFromPrevious}",
                    _ => point.DeltaFromPrevious.ToString()
                };
                Console.WriteLine($"{point.CommitHash[..Math.Min(10, point.CommitHash.Length)],-10} {point.When,-12:yyyy-MM-dd} {point.Total,-7} {delta}");
            }
        }

        ReportMessages(session);
        return ExitClean;
    }

    private static string FormatAge(int days) => days switch
    {
        < 30 => $"{days}d",
        < 365 => $"{days / 30}mo",
        _ => $"{days / 365}y"
    };

    private static int ParseDays(string value)
    {
        string trimmed = value.Trim();
        string numeric = trimmed.EndsWith('d') ? trimmed[..^1] : trimmed;
        if (!int.TryParse(numeric, out int days) || days <= 0)
        {
            throw new ArgumentException("--fail-over needs a value like '180d'.");
        }
        return days;
    }

    private static int ParseNonNegativeInt(string value, string option)
    {
        if (!int.TryParse(value, out int parsed) || parsed < 0)
        {
            throw new ArgumentException($"{option} needs a whole number of zero or more.");
        }
        return parsed;
    }

    private static string NextArg(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} needs a value.");
        }
        return args[++index];
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, out int parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{option} needs a positive whole number.");
        }
        return parsed;
    }

    /// <summary>Parsed command-line options, with defaults chosen so that a bare command is safe.</summary>
    private sealed record Options
    {
        public string Path { get; private init; } = ".";

        public ReportFormat Format { get; private init; } = ReportFormat.Console;

        /// <summary>The lowest severity that fails the command, or <c>null</c> to never fail.</summary>
        public Severity? FailOn { get; private init; } = Severity.Error;

        public bool UseBaseline { get; private init; } = true;

        public string? OutputPath { get; private init; }

        /// <summary>Permits <c>init</c> to overwrite a configuration file that is already there.</summary>
        public bool Force { get; private init; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                switch (argument)
                {
                    case "--format":
                        options = options with { Format = ParseFormat(Next(args, ref i, "--format")) };
                        break;
                    case "--fail-on":
                        options = options with { FailOn = ParseFailOn(Next(args, ref i, "--fail-on")) };
                        break;
                    case "--no-baseline":
                        options = options with { UseBaseline = false };
                        break;
                    case "--force":
                        options = options with { Force = true };
                        break;
                    case "--output":
                        options = options with { OutputPath = Next(args, ref i, "--output") };
                        break;
                    default:
                        if (argument.StartsWith('-'))
                        {
                            throw new ArgumentException($"unknown option '{argument}'.");
                        }
                        options = options with { Path = argument };
                        break;
                }
            }
            return options;
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} needs a value.");
            }
            return args[++index];
        }

        private static ReportFormat ParseFormat(string value) => value.ToLowerInvariant() switch
        {
            "console" or "text" => ReportFormat.Console,
            "json" => ReportFormat.Json,
            "sarif" => ReportFormat.Sarif,
            _ => throw new ArgumentException($"unknown format '{value}'.")
        };

        private static Severity? ParseFailOn(string value)
        {
            if (value.Equals("never", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (!ArchonConfig.TryParseSeverity(value, out Severity severity) || severity == Severity.Off)
            {
                throw new ArgumentException($"unknown --fail-on level '{value}'.");
            }
            return severity;
        }
    }

    /// <summary>Configuration, rule registry, baseline and engine assembled for one invocation.</summary>
    private sealed class Session
    {
        public required ArchonConfig Config { get; init; }

        public required AnalysisEngine Engine { get; init; }

        public required Baseline Baseline { get; init; }

        public required string BaselinePath { get; init; }

        public required List<string> Messages { get; init; }

        public static Session Create(string path)
        {
            string root = Directory.Exists(path)
                ? System.IO.Path.GetFullPath(path)
                : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();

            var messages = new List<string>();
            ArchonConfig config = ConfigLoader.Load(root, out string? configError);
            if (configError is not null)
            {
                messages.Add(configError);
            }

            var registry = new RuleRegistry();
            registry.Add(new BuiltInRulePack());
            foreach (string pack in config.RulePacks)
            {
                registry.AddFromAssembly(System.IO.Path.IsPathRooted(pack)
                    ? pack
                    : System.IO.Path.Combine(config.WorkspaceRoot, pack));
            }

            string baselinePath = System.IO.Path.IsPathRooted(config.Baseline)
                ? config.Baseline
                : System.IO.Path.Combine(config.WorkspaceRoot, config.Baseline);
            Baseline baseline = Baseline.Load(baselinePath, out string? baselineError);
            if (baselineError is not null)
            {
                messages.Add(baselineError);
            }

            // After the registry, so that a rule id from an external pack is not reported as a
            // misspelling merely because its pack had not loaded when the check ran.
            messages.AddRange(ConfigValidator.Validate(config, registry));

            return new Session
            {
                Config = config,
                Engine = new AnalysisEngine(registry, new SourceCache()),
                Baseline = baseline,
                BaselinePath = baselinePath,
                Messages = messages
            };
        }
    }
}
