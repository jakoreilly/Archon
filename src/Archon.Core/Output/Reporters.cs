using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archon.Core.Engine;
using Archon.Core.Findings;
using Archon.Core.Rules;

namespace Archon.Core.Output;

/// <summary>Output shapes a host can request. All three describe the same findings.</summary>
public enum ReportFormat
{
    Console,
    Json,
    Sarif
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

        int errors = result.Findings.Count(f => f.Severity == Severity.Error);
        int warnings = result.Findings.Count(f => f.Severity == Severity.Warning);
        int lower = result.Findings.Count - errors - warnings;

        builder.AppendLine($"{result.Findings.Count} finding(s): {errors} error, {warnings} warning, {lower} informational.");
        if (result.BaselinedFindings.Count > 0)
        {
            builder.AppendLine($"{result.BaselinedFindings.Count} baselined finding(s) not counted.");
        }

        var failures = result.Skipped.Where(s => s.Reason.StartsWith("failed:", StringComparison.Ordinal)).ToList();
        foreach (SkippedRule failure in failures)
        {
            builder.AppendLine($"warning: rule {failure.RuleId} did not run — {failure.Reason}");
        }

        builder.AppendLine($"Analysed {result.FilesAnalysed} file(s) in {result.ElapsedMilliseconds} ms.");
        return builder.ToString();
    }

    private static string RenderJson(AnalysisResult result, string workspaceRoot)
    {
        var payload = new
        {
            findings = result.Findings.Select(f => Describe(f, workspaceRoot)),
            baselined = result.BaselinedFindings.Select(f => Describe(f, workspaceRoot)),
            skipped = result.Skipped.Select(s => new { ruleId = s.RuleId, reason = s.Reason }),
            diagnostics = result.Diagnostics,
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
        explanation = finding.Explanation
    };

    private static string RenderSarif(AnalysisResult result, RuleRegistry registry, string workspaceRoot)
    {
        var reportedRuleIds = result.Findings.Select(f => f.RuleId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var rules = new JsonArray();
        foreach (string ruleId in reportedRuleIds)
        {
            RegisteredRule? registered = registry.Find(ruleId);
            rules.Add(new JsonObject
            {
                ["id"] = ruleId,
                ["name"] = registered?.Descriptor.Title ?? ruleId,
                ["shortDescription"] = new JsonObject { ["text"] = registered?.Descriptor.Description ?? ruleId },
                ["properties"] = new JsonObject
                {
                    ["category"] = registered?.Descriptor.Category ?? "general",
                    ["scope"] = registered?.Rule.Scope.ToString() ?? "File"
                }
            });
        }

        var results = new JsonArray();
        foreach (Finding finding in result.Findings)
        {
            results.Add(new JsonObject
            {
                ["ruleId"] = finding.RuleId,
                ["level"] = SarifLevel(finding.Severity),
                ["message"] = new JsonObject { ["text"] = finding.Message },
                ["partialFingerprints"] = new JsonObject { ["archonFingerprint/v1"] = finding.Fingerprint },
                ["locations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["physicalLocation"] = new JsonObject
                        {
                            ["artifactLocation"] = new JsonObject
                            {
                                ["uri"] = Fingerprint.ToRelative(finding.FilePath, workspaceRoot).Replace('\\', '/')
                            },
                            ["region"] = new JsonObject
                            {
                                ["startLine"] = finding.Span.StartLine + 1,
                                ["startColumn"] = finding.Span.StartColumn + 1,
                                ["endLine"] = finding.Span.EndLine + 1,
                                ["endColumn"] = finding.Span.EndColumn + 1
                            }
                        }
                    }
                }
            });
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
                            ["informationUri"] = "https://github.com/archon-tools/archon",
                            ["rules"] = rules
                        }
                    },
                    ["results"] = results
                }
            }
        };
        return log.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SarifLevel(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        Severity.Information => "note",
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
