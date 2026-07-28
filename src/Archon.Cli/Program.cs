using Archon.Core.Configuration;
using Archon.Core.Engine;
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

    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? ExitUsage : ExitClean;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "check" => RunCheck(args[1..]),
                "rules" => RunRules(args[1..]),
                "baseline" => RunBaseline(args[1..]),
                "explain" => RunExplain(args[1..]),
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

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"archon: unknown command '{command}'.");
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

            Options for check:
              --format <name>     console (default), json or sarif.
              --fail-on <level>   error (default), warning, information, hint or never.
              --no-baseline       Ignore the baseline file and report every finding.
              --output <file>     Write the report to a file instead of standard output.

            Exit codes:
              0  no finding at or above the --fail-on level
              1  at least one such finding
              2  the command could not run
            """);
    }

    private static int RunCheck(string[] args)
    {
        Options options = Options.Parse(args);
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

        foreach (string message in session.Messages)
        {
            Console.Error.WriteLine($"archon: {message}");
        }

        if (options.FailOn is null)
        {
            return ExitClean;
        }
        return result.CountAtLeast(options.FailOn.Value) > 0 ? ExitFindings : ExitClean;
    }

    private static int RunRules(string[] args)
    {
        Options options = Options.Parse(args);
        Session session = Session.Create(options.Path);

        Console.WriteLine($"{"Rule",-8} {"Severity",-12} {"Scope",-10} {"Category",-14} Title");
        foreach (RegisteredRule registered in session.Engine.Registry.Descriptors
                     .OrderBy(r => r.Descriptor.Id, StringComparer.Ordinal))
        {
            Severity severity = session.Config.SeverityFor(registered.Descriptor);
            Console.WriteLine($"{registered.Descriptor.Id,-8} {Reporter.Label(severity),-12} {registered.Rule.Scope,-10} {registered.Descriptor.Category,-14} {registered.Descriptor.Title}");
        }

        Console.WriteLine();
        Console.WriteLine(session.Config.SourcePath is null
            ? "No .archon.json found; showing default severities."
            : $"Configuration: {session.Config.SourcePath}");
        return ExitClean;
    }

    private static int RunBaseline(string[] args)
    {
        Options options = Options.Parse(args);
        Session session = Session.Create(options.Path);

        WorkspaceModel workspace = WorkspaceModel.Discover(options.Path, session.Config.EffectiveExcludes());
        AnalysisResult result = session.Engine.AnalyseWorkspace(workspace, session.Config, Baseline.Empty);

        Baseline.Save(session.BaselinePath, result.Findings, session.Config.WorkspaceRoot);
        Console.WriteLine($"Recorded {result.Findings.Count} finding(s) in {session.BaselinePath}.");
        Console.WriteLine("These are now accepted; only new findings will fail a check.");
        return ExitClean;
    }

    private static int RunExplain(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("archon: explain needs a rule id.");
            return ExitUsage;
        }

        Session session = Session.Create(Directory.GetCurrentDirectory());
        RegisteredRule? registered = session.Engine.Registry.Find(args[0]);
        if (registered is null)
        {
            Console.Error.WriteLine($"archon: no rule with id '{args[0]}'.");
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
        Console.WriteLine();
        Console.WriteLine($"  Suppress one occurrence with: // archon-ignore[{registered.Descriptor.Id}] reason");
        Console.WriteLine($"  Disable entirely in .archon.json: \"rules\": {{ \"{registered.Descriptor.Id}\": \"off\" }}");
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
