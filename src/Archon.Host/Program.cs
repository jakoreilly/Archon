using System.Text.Json;
using System.Text.Json.Nodes;
using Archon.Core.Configuration;
using Archon.Core.Engine;
using Archon.Core.Explanations;
using Archon.Core.Findings;
using Archon.Core.Insights;
using Archon.Core.Output;
using Archon.Core.Rules;
using Archon.Core.Sources;

namespace Archon.Host;

/// <summary>
/// A long-lived analysis process driven by one JSON object per line on standard input, answering
/// with one JSON object per line on standard output. Line framing keeps the protocol readable and
/// scriptable, so the same process can be driven by an editor or by hand while diagnosing it.
///
/// Requests are handled one at a time in arrival order, and a response is always written for each.
/// A client that wants to avoid queueing work it no longer needs, such as an editor reacting to
/// every keystroke, is responsible for waiting until the previous response arrives before sending
/// the next request.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static AnalysisSession? _session;

    private static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            // Read from the assembly rather than written here. The extension checks this against the
            // host it bundles, and a literal that has to be edited in step with the build is exactly
            // the kind of thing that ships one release behind without anyone noticing.
            Version? version = typeof(Program).Assembly.GetName().Version;
            Console.WriteLine($"archon-host {(version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}")}");
            return 0;
        }

        string? initialRoot = ReadOption(args, "--root");
        if (initialRoot is not null)
        {
            _session = AnalysisSession.Create(initialRoot);
        }

        // Both streams are read and written as UTF-8 explicitly. They carry file content, and the
        // console's own code page would otherwise decide how a source file's non-ASCII characters
        // survive the trip — which on Windows means it usually would not.
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), encoding);
        using var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = false };
        Console.SetOut(output);

        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? id = null;
            try
            {
                JsonNode request = JsonNode.Parse(line) ?? throw new InvalidOperationException("empty request");
                id = request["id"];
                string method = request["method"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("a request needs a method");
                JsonNode? parameters = request["params"];

                if (method == "shutdown")
                {
                    Respond(id, new JsonObject { ["stopped"] = true });
                    return 0;
                }

                JsonNode result = Dispatch(method, parameters);
                Respond(id, result);
            }
            catch (Exception ex)
            {
                Fail(id, ex.Message);
            }
        }
        return 0;
    }

    private static JsonNode Dispatch(string method, JsonNode? parameters) => method switch
    {
        "initialize" => Initialize(Required(parameters, "root")),
        "listRules" => ListRules(),
        "analyzeFile" => AnalyzeFile(parameters),
        "analyzeWorkspace" => AnalyzeWorkspace(),
        "methodImpact" => MethodImpact(parameters),
        "setSeverity" => SetSeverity(Required(parameters, "ruleId"), Required(parameters, "severity")),
        "invalidate" => Invalidate(parameters),
        "reloadConfig" => ReloadConfig(),
        "writeBaseline" => WriteBaseline(),
        _ => throw new InvalidOperationException($"unknown method '{method}'")
    };

    private static AnalysisSession Session =>
        _session ?? throw new InvalidOperationException("initialize must be called first");

    private static JsonNode Initialize(string root)
    {
        _session = AnalysisSession.Create(root);
        return new JsonObject
        {
            ["root"] = _session.Root,
            ["configPath"] = _session.Config.SourcePath,
            ["baselinePath"] = _session.BaselinePath,
            ["baselineCount"] = _session.Baseline.Count,
            ["rules"] = RuleArray(),
            ["messages"] = ToArray(_session.Messages)
        };
    }

    private static JsonNode ListRules() => new JsonObject { ["rules"] = RuleArray() };

    private static JsonArray RuleArray()
    {
        var rules = new JsonArray();
        foreach (RegisteredRule registered in Session.Registry.Descriptors
                     .OrderBy(r => r.Descriptor.Id, StringComparer.Ordinal))
        {
            SnippetPointer? pointer = SnippetCatalog.ForRule(registered.Descriptor.Id);
            rules.Add(new JsonObject
            {
                ["id"] = registered.Descriptor.Id,
                ["title"] = registered.Descriptor.Title,
                ["category"] = registered.Descriptor.Category,
                ["description"] = registered.Descriptor.Description,
                ["scope"] = registered.Rule.Scope.ToString(),
                ["language"] = registered.Rule.Language,
                ["defaultSeverity"] = Reporter.Label(registered.Descriptor.DefaultSeverity),
                ["severity"] = Reporter.Label(Session.Config.SeverityFor(registered.Descriptor)),
                ["pack"] = registered.PackName,
                ["snippetId"] = pointer?.SnippetId,
                ["snippetTitle"] = pointer?.Title,
                ["snippetWhy"] = pointer?.Why
            });
        }
        return rules;
    }

    private static JsonNode AnalyzeFile(JsonNode? parameters)
    {
        string path = Path.GetFullPath(Required(parameters, "path"));
        string? text = parameters?["text"]?.GetValue<string>();
        if (text is not null)
        {
            Session.SetText(path, text);
        }
        else
        {
            Session.Invalidate(path);
        }

        bool includeProject = parameters?["includeProject"]?.GetValue<bool>() ?? true;
        AnalysisResult result = includeProject
            ? Session.Engine.AnalyseFileInProject(path, Session.Config, Session.Baseline)
            : Session.Engine.AnalyseFile(path, Session.Config, Session.Baseline);
        return Describe(result, path);
    }

    /// <summary>
    /// Runs every rule over every file. Discovery is refreshed first: this is the pass a user asks
    /// for by name, so it answers for the tree as it is now rather than as it was last seen.
    /// </summary>
    private static JsonNode AnalyzeWorkspace()
    {
        Session.InvalidateWorkspace();
        WorkspaceModel workspace = Session.DiscoverWorkspace();
        AnalysisResult result = Session.Engine.AnalyseWorkspace(workspace, Session.Config, Session.Baseline);
        return Describe(result, null);
    }

    /// <summary>
    /// Reports how far each method in one file reaches. The graph spans the whole workspace and is
    /// kept between requests, so the first call pays for discovery and later calls pay only for the
    /// files that changed.
    /// </summary>
    private static JsonNode MethodImpact(JsonNode? parameters)
    {
        string path = Path.GetFullPath(Required(parameters, "path"));
        string? text = parameters?["text"]?.GetValue<string>();
        if (text is not null)
        {
            Session.SetText(path, text);
        }

        int maxDepth = parameters?["maxDepth"]?.GetValue<int>() ?? 6;
        int maxCallers = parameters?["maxCallers"]?.GetValue<int>() ?? 50;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WorkspaceModel workspace = Session.DiscoverWorkspace();
        ImpactResult result = Session.CallGraph.Impact(workspace, path, maxDepth, maxCallers);
        stopwatch.Stop();

        var methods = new JsonArray();
        foreach (MethodImpact method in result.Methods)
        {
            var callers = new JsonArray();
            foreach (CallerLocation caller in method.Callers)
            {
                callers.Add(new JsonObject
                {
                    ["methodName"] = caller.MethodName,
                    ["file"] = caller.FilePath,
                    ["line"] = caller.Line,
                    ["column"] = caller.Column
                });
            }

            methods.Add(new JsonObject
            {
                ["methodName"] = method.MethodName,
                ["arity"] = method.Arity,
                ["line"] = method.Line,
                ["column"] = method.Column,
                ["referenceCount"] = method.ReferenceCount,
                ["projectCount"] = method.ProjectCount,
                ["coveringTestCount"] = method.CoveringTestCount,
                ["depthBounded"] = method.DepthBounded,
                ["callers"] = callers
            });
        }

        return new JsonObject
        {
            ["scope"] = path,
            ["methods"] = methods,
            ["graphMethodCount"] = result.MethodCount,
            ["graphFileCount"] = result.FileCount,
            ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds
        };
    }

    private static JsonNode SetSeverity(string ruleId, string severityName)
    {
        if (Session.Registry.Find(ruleId) is null)
        {
            throw new InvalidOperationException($"no rule with id '{ruleId}'");
        }
        if (!ArchonConfig.TryParseSeverity(severityName, out Severity severity))
        {
            throw new InvalidOperationException($"unknown severity '{severityName}'");
        }
        Session.Config.SessionOverrides[ruleId] = severity;
        return new JsonObject
        {
            ["ruleId"] = ruleId,
            ["severity"] = Reporter.Label(severity)
        };
    }

    /// <summary>
    /// Drops cached content for files changed outside the editor. <c>structural</c> marks a change
    /// that adds or removes files rather than editing them, which also retires the discovered file
    /// set. Paths arrive in a batch because the events that cause this — switching branches, or a
    /// code generator running — touch many files at once.
    /// </summary>
    private static JsonNode Invalidate(JsonNode? parameters)
    {
        var paths = new List<string>();
        if (parameters?["path"] is JsonNode single)
        {
            paths.Add(single.GetValue<string>());
        }
        if (parameters?["paths"] is JsonArray many)
        {
            paths.AddRange(many.Where(p => p is not null).Select(p => p!.GetValue<string>()));
        }
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("'path' or 'paths' is required");
        }

        foreach (string path in paths)
        {
            Session.Invalidate(Path.GetFullPath(path));
        }
        if (parameters?["structural"]?.GetValue<bool>() == true)
        {
            Session.InvalidateWorkspace();
        }

        return new JsonObject { ["invalidated"] = paths.Count, ["cached"] = Session.Sources.Count };
    }

    private static JsonNode ReloadConfig()
    {
        Session.ReloadConfiguration();
        return new JsonObject
        {
            ["configPath"] = Session.Config.SourcePath,
            ["baselineCount"] = Session.Baseline.Count,
            ["rules"] = RuleArray(),
            ["messages"] = ToArray(Session.Messages)
        };
    }

    private static JsonNode WriteBaseline()
    {
        Session.InvalidateWorkspace();
        WorkspaceModel workspace = Session.DiscoverWorkspace();
        AnalysisResult result = Session.Engine.AnalyseWorkspace(workspace, Session.Config, Baseline.Empty);
        Baseline.Save(Session.BaselinePath, result.Findings, Session.Config.WorkspaceRoot);
        Session.ReloadConfiguration();
        return new JsonObject
        {
            ["path"] = Session.BaselinePath,
            ["recorded"] = result.Findings.Count
        };
    }

    private static JsonNode Describe(AnalysisResult result, string? scopePath)
    {
        var findings = new JsonArray();
        foreach (Finding finding in result.Findings)
        {
            findings.Add(new JsonObject
            {
                ["ruleId"] = finding.RuleId,
                ["severity"] = Reporter.Label(finding.Severity),
                ["category"] = finding.Category,
                ["kind"] = finding.Kind,
                ["message"] = finding.Message,
                ["file"] = finding.FilePath,
                ["startLine"] = finding.Span.StartLine,
                ["startColumn"] = finding.Span.StartColumn,
                ["endLine"] = finding.Span.EndLine,
                ["endColumn"] = finding.Span.EndColumn,
                ["fingerprint"] = finding.Fingerprint
            });
        }

        var skipped = new JsonArray();
        foreach (SkippedRule rule in result.Skipped.Where(s => s.Reason.StartsWith("failed:", StringComparison.Ordinal)))
        {
            skipped.Add(new JsonObject { ["ruleId"] = rule.RuleId, ["reason"] = rule.Reason });
        }

        return new JsonObject
        {
            ["scope"] = scopePath,
            ["findings"] = findings,
            ["baselinedCount"] = result.BaselinedFindings.Count,
            ["failedRules"] = skipped,
            ["diagnostics"] = ToArray(result.Diagnostics),
            ["filesAnalysed"] = result.FilesAnalysed,
            ["elapsedMilliseconds"] = result.ElapsedMilliseconds
        };
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            array.Add(value);
        }
        return array;
    }

    private static string Required(JsonNode? parameters, string name) =>
        parameters?[name]?.GetValue<string>()
            ?? throw new InvalidOperationException($"'{name}' is required");

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Respond(JsonNode? id, JsonNode result) =>
        Write(new JsonObject { ["id"] = id?.DeepClone(), ["ok"] = true, ["result"] = result });

    private static void Fail(JsonNode? id, string error) =>
        Write(new JsonObject { ["id"] = id?.DeepClone(), ["ok"] = false, ["error"] = error });

    private static void Write(JsonNode payload)
    {
        Console.WriteLine(payload.ToJsonString(Options));
        Console.Out.Flush();
    }
}
