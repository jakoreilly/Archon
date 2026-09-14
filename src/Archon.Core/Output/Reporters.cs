using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archon.Core.Engine;
using Archon.Core.Findings;
using Archon.Core.Rules;

namespace Archon.Core.Output;

/// <summary>Output shapes a host can request. All of them describe the same findings.</summary>
public enum ReportFormat
{
    Console,
    Json,
    Sarif,

    /// <summary>
    /// GitHub Actions workflow commands, one per finding, so a check run annotates the diff of a
    /// pull request without a separate upload step.
    /// </summary>
    GitHub
}

/// <summary>
/// Renders an analysis result. A single writer per format is shared by every rule so that a
/// result reads the same regardless of which language or rule produced it.
/// </summary>
public static class Reporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Render(AnalysisResult result, RuleRegistry registry, string workspaceRoot, ReportFormat format) => format switch
    {
        ReportFormat.Json => RenderJson(result, workspaceRoot),
        ReportFormat.Sarif => RenderSarif(result, registry, workspaceRoot),
        ReportFormat.GitHub => RenderGitHub(result, workspaceRoot),
        _ => RenderConsole(result, workspaceRoot)
    };

    private static string RenderConsole(AnalysisResult result, string workspaceRoot)
    {
        var builder = new StringBuilder();
        foreach (IGrouping<string, Finding> group in result.Findings.GroupBy(f => f.FilePath).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(Fingerprint.ToRelative(group.Key, workspaceRoot).Replace('\\', '/'));
            foreach (Finding finding in group.OrderBy(f => f.Span.StartLine))
            {
                builder.AppendLine($"  {finding.Span.StartLine + 1,5}:{finding.Span.StartColumn + 1,-4} {Label(finding.Severity),-11} {finding.RuleId}  {finding.Message}");
            }
            builder.AppendLine();
        }

        foreach (string diagnostic in result.Diagnostics)
        {
            builder.AppendLine($"note: {diagnostic}");
        }

        AppendSummary(builder, result);
        return builder.ToString();
    }

    /// <summary>The closing lines shared by the human-readable formats.</summary>
    private static void AppendSummary(StringBuilder builder, AnalysisResult result)
    {
        int errors = result.Findings.Count(f => f.Severity == Severity.Error);
        int warnings = result.Findings.Count(f => f.Severity == Severity.Warning);
        int lower = result.Findings.Count - errors - warnings;

        string scope = result.Scope is null ? "" : $" in {result.Scope}";
        builder.AppendLine($"{result.Findings.Count} finding(s){scope}: {errors} error, {warnings} warning, {lower} informational.");
        if (result.Scope is not null)
        {
            builder.AppendLine($"{result.OutsideScope} finding(s) outside the change not shown.");
        }
        if (result.BaselinedFindings.Count > 0)
        {
            builder.AppendLine($"{result.BaselinedFindings.Count} baselined finding(s) not counted.");
        }
        if (result.StaleBaselineEntries.Count > 0)
        {
            builder.AppendLine($"{result.StaleBaselineEntries.Count} baseline entrie(s) no longer match anything; 'archon baseline --prune' drops them.");
        }

        var failures = result.Skipped.Where(s => s.Reason.StartsWith("failed:", StringComparison.Ordinal)).ToList();
        foreach (SkippedRule failure in failures)
        {
            builder.AppendLine($"warning: rule {failure.RuleId} did not run — {failure.Reason}");
        }

        builder.AppendLine($"Analysed {result.FilesAnalysed} file(s) in {result.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// One workflow command per finding. The runner turns each into an annotation on the file and
    /// line, shown inline on a pull request's diff when the line is part of the change. Messages
    /// and property values are percent-encoded as the runner requires, since a newline or a comma
    /// in a message would otherwise end the command early.
    /// </summary>
    private static string RenderGitHub(AnalysisResult result, string workspaceRoot)
    {
        var builder = new StringBuilder();
        foreach (Finding finding in result.Findings
                     .OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(f => f.Span.StartLine))
        {
            string command = finding.Severity switch
            {
                Severity.Error => "error",
                Severity.Warning => "warning",
                _ => "notice"
            };
            string file = Fingerprint.ToRelative(finding.FilePath, workspaceRoot).Replace('\\', '/');
            int endLine = Math.Max(finding.Span.StartLine, finding.Span.EndLine);
            builder.Append("::").Append(command)
                .Append(" file=").Append(EscapeProperty(file))
                .Append(",line=").Append(finding.Span.StartLine + 1)
                .Append(",col=").Append(finding.Span.StartColumn + 1)
                .Append(",endLine=").Append(endLine + 1);
            if (endLine == finding.Span.StartLine && finding.Span.EndColumn > finding.Span.StartColumn)
            {
                builder.Append(",endColumn=").Append(finding.Span.EndColumn + 1);
            }
            builder.Append(",title=").Append(EscapeProperty(finding.RuleId))
                .Append("::").Append(EscapeData($"{finding.RuleId}: {finding.Message}"))
                .Append('\n');
        }

        foreach (string diagnostic in result.Diagnostics)
        {
            builder.Append("::notice::").Append(EscapeData(diagnostic)).Append('\n');
        }

        AppendSummary(builder, result);
        return builder.ToString();
    }

    private static string EscapeData(string value) =>
        value.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private static string EscapeProperty(string value) =>
        EscapeData(value).Replace(":", "%3A").Replace(",", "%2C");

    private static string RenderJson(AnalysisResult result, string workspaceRoot)
    {
        var payload = new
        {
            findings = result.Findings.Select(f => Describe(f, workspaceRoot)),
            baselined = result.BaselinedFindings.Select(f => Describe(f, workspaceRoot)),
            staleBaseline = result.StaleBaselineEntries.Select(e => new
            {
                fingerprint = e.Fingerprint,
                ruleId = e.RuleId,
                file = e.File,
                message = e.Message
            }),
            skipped = result.Skipped.Select(s => new { ruleId = s.RuleId, reason = s.Reason }),
            diagnostics = result.Diagnostics,
            scope = result.Scope,
            outsideScope = result.OutsideScope,
            filesAnalysed = result.FilesAnalysed,
            elapsedMilliseconds = result.ElapsedMilliseconds
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static object Describe(Finding finding, string workspaceRoot) => new
    {
        ruleId = finding.RuleId,
        severity = Label(finding.Severity),
        category = finding.Category,
        kind = finding.Kind,
        message = finding.Message,
        file = Fingerprint.ToRelative(finding.FilePath, workspaceRoot).Replace('\\', '/'),
        startLine = finding.Span.StartLine,
        startColumn = finding.Span.StartColumn,
        endLine = finding.Span.EndLine,
        endColumn = finding.Span.EndColumn,
        fingerprint = finding.Fingerprint,
        explanation = finding.Explanation,
        fix = finding.Fix is null ? null : new
        {
            title = finding.Fix.Title,
            edits = finding.Fix.Edits.Select(e => new
            {
                startLine = e.Span.StartLine,
                startColumn = e.Span.StartColumn,
                endLine = e.Span.EndLine,
                endColumn = e.Span.EndColumn,
                newText = e.NewText
            })
        }
    };

    /// <summary>Where the repository and its rule reference live, for a SARIF viewer's links.</summary>
    private const string ProjectUri = "https://github.com/jakoreilly/Archon";

    /// <summary>The pack whose rules the README documents; rules from other packs get no help link.</summary>
    private const string BuiltInPack = "archon.builtin";

    /// <summary>
    /// The engine's version as the assembly carries it, for a log's <c>driver.version</c>. Read
    /// rather than written so it cannot disagree with what <c>archon --version</c> prints.
    /// </summary>
    private static string EngineVersion =>
        typeof(Reporter).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "unknown";

    /// <summary>
    /// SARIF 2.1.0. Baselined findings are emitted as well as reportable ones, each marked with
    /// its <c>baselineState</c> and, for a baselined one, an external suppression naming the
    /// baseline file: a viewer that tracks alerts across uploads then sees accepted debt as
    /// accepted rather than as absent one run and new the next, and a gate that reads the log
    /// can still tell the two apart.
    /// </summary>
    private static string RenderSarif(AnalysisResult result, RuleRegistry registry, string workspaceRoot)
    {
        var reportedRuleIds = result.Findings.Concat(result.BaselinedFindings)
            .Select(f => f.RuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var ruleIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var rules = new JsonArray();
        foreach (string ruleId in reportedRuleIds)
        {
            ruleIndex[ruleId] = rules.Count;
            rules.Add(SarifRule(ruleId, registry.Find(ruleId)));
        }

        var results = new JsonArray();
        foreach (Finding finding in result.Findings)
        {
            results.Add(SarifResult(finding, ruleIndex[finding.RuleId], workspaceRoot, baselined: false));
        }
        foreach (Finding finding in result.BaselinedFindings)
        {
            results.Add(SarifResult(finding, ruleIndex[finding.RuleId], workspaceRoot, baselined: true));
        }

        var log = new JsonObject
        {
            ["$schema"] = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject
                        {
                            ["name"] = "Archon",
                            ["version"] = EngineVersion,
                            ["informationUri"] = ProjectUri,
                            ["rules"] = rules
                        }
                    },
                    ["results"] = results
                }
            }
        };
        return log.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject SarifRule(string ruleId, RegisteredRule? registered)
    {
        var rule = new JsonObject
        {
            ["id"] = ruleId,
            ["name"] = registered?.Descriptor.Title ?? ruleId,
            ["shortDescription"] = new JsonObject { ["text"] = registered?.Descriptor.Description ?? ruleId },
            ["properties"] = new JsonObject
            {
                ["category"] = registered?.Descriptor.Category ?? "general",
                ["scope"] = registered?.Rule.Scope.ToString() ?? "File"
            }
        };
        if (registered is null)
        {
            return rule;
        }
        rule["defaultConfiguration"] = new JsonObject { ["level"] = SarifLevel(registered.Descriptor.DefaultSeverity) };
        if (registered.PackName == BuiltInPack)
        {
            rule["helpUri"] = $"{ProjectUri}#rules";
        }
        return rule;
    }

    private static JsonObject SarifResult(Finding finding, int ruleIndex, string workspaceRoot, bool baselined)
    {
        string uri = Fingerprint.ToRelative(finding.FilePath, workspaceRoot).Replace('\\', '/');
        var entry = new JsonObject
        {
            ["ruleId"] = finding.RuleId,
            ["ruleIndex"] = ruleIndex,
            ["level"] = SarifLevel(finding.Severity),
            ["message"] = new JsonObject { ["text"] = finding.Message },
            ["baselineState"] = baselined ? "unchanged" : "new",
            ["partialFingerprints"] = new JsonObject { ["archonFingerprint/v1"] = finding.Fingerprint },
            ["locations"] = new JsonArray
            {
                new JsonObject
                {
                    ["physicalLocation"] = new JsonObject
                    {
                        ["artifactLocation"] = new JsonObject { ["uri"] = uri },
                        ["region"] = SarifRegion(finding.Span)
                    }
                }
            }
        };
        if (baselined)
        {
            entry["suppressions"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "external",
                    ["status"] = "accepted",
                    ["justification"] = "Accepted in the Archon baseline file."
                }
            };
        }
        if (finding.Fix is not null)
        {
            entry["fixes"] = new JsonArray { SarifFix(finding.Fix, uri) };
        }
        return entry;
    }

    private static JsonObject SarifRegion(SourceSpan span) => new()
    {
        ["startLine"] = span.StartLine + 1,
        ["startColumn"] = span.StartColumn + 1,
        ["endLine"] = span.EndLine + 1,
        ["endColumn"] = span.EndColumn + 1
    };

    /// <summary>A fix in SARIF's own shape, so a viewer that offers fixes from a log can apply this one.</summary>
    private static JsonObject SarifFix(FindingFix fix, string uri)
    {
        var replacements = new JsonArray();
        foreach (TextEdit edit in fix.Edits)
        {
            replacements.Add(new JsonObject
            {
                ["deletedRegion"] = SarifRegion(edit.Span),
                ["insertedContent"] = new JsonObject { ["text"] = edit.NewText }
            });
        }
        return new JsonObject
        {
            ["description"] = new JsonObject { ["text"] = fix.Title },
            ["artifactChanges"] = new JsonArray
            {
                new JsonObject
                {
                    ["artifactLocation"] = new JsonObject { ["uri"] = uri },
                    ["replacements"] = replacements
                }
            }
        };
    }

    private static string SarifLevel(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        Severity.Information => "note",
        // A rule whose default is off still has a level to declare: SARIF's own word for it.
        Severity.Off => "none",
        _ => "note"
    };

    public static string Label(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        Severity.Information => "information",
        Severity.Hint => "hint",
        _ => "off"
    };
}
