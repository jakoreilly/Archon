using System.Text.Json;
using Archon.Core.Configuration;
using Archon.Core.Engine;
using Archon.Core.Explanations;
using Archon.Core.Findings;
using Archon.Core.Output;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Archon.Rules;

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
                "rules" => RunRules(args[1..]),
                "baseline" => RunBaseline(args[1..]),
                "explain" => RunExplain(args[1..]),
                "init" => RunInit(args[1..]),
                "schema" => RunSchema(args[1..]),
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

    private static readonly string[] CommandNames = { "check", "rules", "baseline", "explain", "init", "schema" };

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
              rules [path]        List every registered rule and its effective severity.
              baseline [path]     Record current findings as accepted, so only new ones fail.
              explain <ruleId>    Describe one rule.
              init [path]         Write a starter .archon.json and its schema.
              schema [path]       Print the JSON Schema for .archon.json.
              --version           Print the version and exit.

            Options for check:
              --format <name>     console (default), json or sarif.
              --fail-on <level>   error (default), warning, information, hint or never.
              --no-baseline       Ignore the baseline file and report every finding.
              --output <file>     Write the report to a file instead of standard output.

            Options for init:
              --force             Overwrite an existing .archon.json.

            Options for schema:
              --output <file>     Write to a file instead of standard output.

            Exit codes:
              0  no finding at or above the --fail-on level
              1  at least one such finding
              2  the command could not run

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
