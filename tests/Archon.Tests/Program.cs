using Archon.Core.Configuration;
using Archon.Core.Engine;
using Archon.Core.Findings;
using Archon.Core.Insights;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Archon.Rules;
using Archon.Rules.CSharp;
using Archon.Rules.Sql;

namespace Archon.Tests;

/// <summary>
/// Behaviour this suite guarantees, expressed as assertions rather than prose. Each group covers
/// one property that a future change could break silently: what a rule does and does not flag,
/// and how configuration, suppression and the baseline compose on top of it.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var harness = new Harness();

        SqlWildcardRules(harness);
        SqlConventionRules(harness);
        SecurityHotspotRules(harness);
        ComplexityRules(harness);
        LayerRules(harness);
        LifetimeRules(harness);
        AsyncSafetyRules(harness);
        PerfHintRules(harness);
        ConfigKeyRules(harness);
        ProjectCycleRules(harness);
        CallGraphChecks(harness);
        CallGraphMemberChecks(harness);
        SuppressionRules(harness);
        BaselineRules(harness);
        BaselineStabilityRules(harness);
        SourceCacheRules(harness);
        ProjectAttributionRules(harness);
        ConfigurationRules(harness);
        ScopeRules(harness);
        RegistryRules(harness);
        GlobRules(harness);

        return harness.Report();
    }

    private static void SqlWildcardRules(Harness harness)
    {
        harness.Group("SQL wildcard column list");

        var workspace = new TestWorkspace();
        workspace.Add("a.sql", "CREATE VIEW dbo.V AS SELECT * FROM dbo.T;");
        AnalysisResult result = workspace.Analyse();
        harness.Equal("flags a genuine wildcard select", 1, result.Findings.CountOf(SelectStarRule.Id));

        var counted = new TestWorkspace();
        counted.Add("b.sql", "SELECT COUNT(*) FROM dbo.T;");
        harness.Equal("ignores COUNT(*)", 0, counted.Analyse().Findings.CountOf(SelectStarRule.Id));

        var prose = new TestWorkspace();
        prose.Add("c.sql", "SELECT 'please select * from the menu' AS Note;");
        harness.Equal("ignores a wildcard inside a string literal", 0, prose.Analyse().Findings.CountOf(SelectStarRule.Id));

        var unparseable = new TestWorkspace();
        unparseable.Add("d.sql", "THIS IS NOT SQL AT ALL (((");
        AnalysisResult broken = unparseable.Analyse();
        harness.Equal("reports a parse failure once", 1, broken.Findings.CountOf(SelectStarRule.ParseFailed));
        harness.Equal("reports no wildcard finding for a file that did not parse", 0, broken.Findings.CountOf(SelectStarRule.Id));

        var multiple = new TestWorkspace();
        multiple.Add("e.sql", "SELECT * FROM dbo.A;\nGO\nSELECT * FROM dbo.B;");
        harness.Equal("flags each wildcard separately", 2, multiple.Analyse().Findings.CountOf(SelectStarRule.Id));
    }

    private static void SqlConventionRules(Harness harness)
    {
        harness.Group("SQL conventions");

        var unconfigured = new TestWorkspace();
        unconfigured.Add("a.sql", "SELECT Id FROM dbo.Orders;");
        harness.Equal("enforces no hint policy until one is configured", 0,
            unconfigured.Analyse().Findings.CountOf(SqlConventionRule.TableHintPolicy));

        var required = new TestWorkspace().WithOption(SqlConventionRule.TableHintPolicy, """{ "policy": "required" }""");
        required.Add("a.sql", "SELECT Id FROM dbo.Orders;");
        harness.Equal("flags a missing hint when the policy requires one", 1,
            required.Analyse().Findings.CountOf(SqlConventionRule.TableHintPolicy));

        var requiredSatisfied = new TestWorkspace().WithOption(SqlConventionRule.TableHintPolicy, """{ "policy": "required" }""");
        requiredSatisfied.Add("a.sql", "SELECT Id FROM dbo.Orders WITH (NOLOCK);");
        harness.Equal("accepts a present hint under a required policy", 0,
            requiredSatisfied.Analyse().Findings.CountOf(SqlConventionRule.TableHintPolicy));

        var forbidden = new TestWorkspace().WithOption(SqlConventionRule.TableHintPolicy, """{ "policy": "forbidden" }""");
        forbidden.Add("a.sql", "SELECT Id FROM dbo.Orders WITH (NOLOCK);");
        harness.Equal("flags a present hint when the policy forbids one", 1,
            forbidden.Analyse().Findings.CountOf(SqlConventionRule.TableHintPolicy));

        var tempNaming = new TestWorkspace().WithOption(SqlConventionRule.TemporaryTableNaming, """{ "pattern": "^#tmp[A-Z]" }""");
        tempNaming.Add("a.sql", "CREATE TABLE #working (Id INT);");
        harness.Equal("flags a temporary table that breaks the pattern", 1,
            tempNaming.Analyse().Findings.CountOf(SqlConventionRule.TemporaryTableNaming));

        var tempNamingOk = new TestWorkspace().WithOption(SqlConventionRule.TemporaryTableNaming, """{ "pattern": "^#tmp[A-Z]" }""");
        tempNamingOk.Add("a.sql", "CREATE TABLE #tmpOrders (Id INT);");
        harness.Equal("accepts a temporary table matching the pattern", 0,
            tempNamingOk.Analyse().Findings.CountOf(SqlConventionRule.TemporaryTableNaming));

        var permanentTable = new TestWorkspace().WithOption(SqlConventionRule.TemporaryTableNaming, """{ "pattern": "^#tmp[A-Z]" }""");
        permanentTable.Add("a.sql", "CREATE TABLE dbo.Orders (Id INT);");
        harness.Equal("does not apply the temporary-table pattern to a permanent table", 0,
            permanentTable.Analyse().Findings.CountOf(SqlConventionRule.TemporaryTableNaming));

        var procNaming = new TestWorkspace().WithOption(SqlConventionRule.ProcedureNaming, """{ "pattern": "^usp_" }""");
        procNaming.Add("a.sql", "CREATE PROCEDURE dbo.DoThing AS SELECT 1;");
        harness.Equal("flags a procedure that breaks the pattern", 1,
            procNaming.Analyse().Findings.CountOf(SqlConventionRule.ProcedureNaming));

        var procNamingOk = new TestWorkspace().WithOption(SqlConventionRule.ProcedureNaming, """{ "pattern": "^usp_" }""");
        procNamingOk.Add("a.sql", "CREATE PROCEDURE dbo.usp_DoThing AS SELECT 1;");
        harness.Equal("accepts a procedure matching the pattern", 0,
            procNamingOk.Analyse().Findings.CountOf(SqlConventionRule.ProcedureNaming));

        var badPattern = new TestWorkspace().WithOption(SqlConventionRule.ProcedureNaming, """{ "pattern": "^(unclosed" }""");
        badPattern.Add("a.sql", "CREATE PROCEDURE dbo.DoThing AS SELECT 1;");
        harness.Equal("ignores a pattern that does not compile rather than failing the pass", 0,
            badPattern.Analyse().Findings.CountOf(SqlConventionRule.ProcedureNaming));
    }

    private static void SecurityHotspotRules(Harness harness)
    {
        harness.Group("Security hotspots");

        var credentialField = new TestWorkspace();
        credentialField.Add("a.cs", "class C { string password = \"hunter2\"; }");
        harness.Equal("flags a field initialised from a literal named like a credential", 1,
            credentialField.Analyse().Findings.CountOf(SecurityHotspotRule.HardcodedCredential));

        var credentialAssignment = new TestWorkspace();
        credentialAssignment.Add("a.cs", "class C { string apiKey; void M() { apiKey = \"sk-live-abc\"; } }");
        harness.Equal("flags an assignment to a credential-shaped name", 1,
            credentialAssignment.Analyse().Findings.CountOf(SecurityHotspotRule.HardcodedCredential));

        var emptyCredential = new TestWorkspace();
        emptyCredential.Add("a.cs", "class C { string password = \"\"; }");
        harness.Equal("does not flag an empty literal", 0,
            emptyCredential.Analyse().Findings.CountOf(SecurityHotspotRule.HardcodedCredential));

        var unrelatedField = new TestWorkspace();
        unrelatedField.Add("a.cs", "class C { string title = \"hello\"; }");
        harness.Equal("does not flag a field whose name does not read as a credential", 0,
            unrelatedField.Analyse().Findings.CountOf(SecurityHotspotRule.HardcodedCredential));

        var weakHashCreate = new TestWorkspace();
        weakHashCreate.Add("a.cs", "class C { void M() { var h = System.Security.Cryptography.MD5.Create(); } }");
        harness.Equal("flags MD5.Create()", 1,
            weakHashCreate.Analyse().Findings.CountOf(SecurityHotspotRule.WeakCryptographicPrimitive));

        var weakCipherNew = new TestWorkspace();
        weakCipherNew.Add("a.cs", "class C { void M() { var d = new System.Security.Cryptography.DESCryptoServiceProvider(); } }");
        harness.Equal("flags new DESCryptoServiceProvider()", 1,
            weakCipherNew.Analyse().Findings.CountOf(SecurityHotspotRule.WeakCryptographicPrimitive));

        var strongHash = new TestWorkspace();
        strongHash.Add("a.cs", "class C { void M() { var h = System.Security.Cryptography.SHA256.Create(); } }");
        harness.Equal("does not flag SHA256", 0,
            strongHash.Analyse().Findings.CountOf(SecurityHotspotRule.WeakCryptographicPrimitive));

        var insecureToken = new TestWorkspace();
        insecureToken.Add("a.cs", "class C { void M() { var sessionToken = new System.Random(); } }");
        harness.Equal("flags Random assigned to a security-shaped name", 1,
            insecureToken.Analyse().Findings.CountOf(SecurityHotspotRule.InsecureRandomness));

        var ordinaryRandom = new TestWorkspace();
        ordinaryRandom.Add("a.cs", "class C { void M() { var dice = new System.Random(); } }");
        harness.Equal("does not flag Random assigned to an ordinary name", 0,
            ordinaryRandom.Analyse().Findings.CountOf(SecurityHotspotRule.InsecureRandomness));

        var nestedQuantifier = new TestWorkspace();
        nestedQuantifier.Add("a.cs", "class C { void M() { System.Text.RegularExpressions.Regex.IsMatch(\"x\", \"(a+)+\"); } }");
        harness.Equal("flags a nested-quantifier pattern", 1,
            nestedQuantifier.Analyse().Findings.CountOf(SecurityHotspotRule.CatastrophicBacktrackingRegex));

        var safePattern = new TestWorkspace();
        safePattern.Add("a.cs", "class C { void M() { System.Text.RegularExpressions.Regex.IsMatch(\"x\", \"^[a-z]+$\"); } }");
        harness.Equal("does not flag a simple anchored pattern", 0,
            safePattern.Analyse().Findings.CountOf(SecurityHotspotRule.CatastrophicBacktrackingRegex));

        var newRegexNested = new TestWorkspace();
        newRegexNested.Add("a.cs", "class C { void M() { var r = new System.Text.RegularExpressions.Regex(\"(\\\\d+)*\"); } }");
        harness.Equal("flags a nested-quantifier pattern passed to 'new Regex(...)'", 1,
            newRegexNested.Analyse().Findings.CountOf(SecurityHotspotRule.CatastrophicBacktrackingRegex));

        var unrelatedRegexLikeCall = new TestWorkspace();
        unrelatedRegexLikeCall.Add("a.cs", "class C { void M() { Validator.IsMatch(\"x\", \"(a+)+\"); } }");
        harness.Equal("does not flag a same-named method on an unrelated type", 0,
            unrelatedRegexLikeCall.Analyse().Findings.CountOf(SecurityHotspotRule.CatastrophicBacktrackingRegex));
    }

    private static void ComplexityRules(Harness harness)
    {
        harness.Group("Complexity and duplication");

        var simple = new TestWorkspace();
        simple.Add("a.cs", "class C { void M(int x) { if (x > 0) { } } }");
        harness.Equal("a single if stays under the default threshold", 0,
            simple.Analyse().Findings.CountOf(ComplexityRule.CognitiveComplexity));

        var nested = new TestWorkspace();
        nested.Add("a.cs", """
            class C
            {
                void M(int a, int b, int c, int d, int e)
                {
                    if (a > 0)
                    {
                        if (b > 0)
                        {
                            if (c > 0)
                            {
                                if (d > 0)
                                {
                                    if (e > 0) { }
                                    else if (e < 0) { }
                                    else { }
                                }
                            }
                        }
                    }
                }
            }
            """);
        harness.Check("five levels of nested if crosses the default threshold of 15",
            nested.Analyse().Findings.CountOf(ComplexityRule.CognitiveComplexity) == 1);

        var lowThreshold = new TestWorkspace().WithOption(ComplexityRule.CognitiveComplexity, """{ "threshold": 0 }""");
        lowThreshold.Add("a.cs", "class C { void M(int x) { if (x > 0) { } } }");
        harness.Equal("a threshold of zero flags any branching at all", 1,
            lowThreshold.Analyse().Findings.CountOf(ComplexityRule.CognitiveComplexity));

        var recursive = new TestWorkspace().WithOption(ComplexityRule.CognitiveComplexity, """{ "threshold": 0 }""");
        recursive.Add("a.cs", "class C { int M(int x) { return x <= 0 ? 0 : M(x - 1); } }");
        harness.Check("a recursive call and its own ternary both raise complexity",
            recursive.Analyse().Findings.CountOf(ComplexityRule.CognitiveComplexity) == 1);

        var duplicated = new TestWorkspace();
        duplicated.Add("a.cs", """
            class C
            {
                string A() => "not found in catalog";
                string B() => "not found in catalog";
                string D() => "not found in catalog";
            }
            """);
        harness.Equal("flags the second and third copy, not the first", 2,
            duplicated.Analyse().Findings.CountOf(ComplexityRule.DuplicatedStringLiteral));

        var shortLiteral = new TestWorkspace();
        shortLiteral.Add("a.cs", "class C { string A() => \"ok\"; string B() => \"ok\"; string D() => \"ok\"; }");
        harness.Equal("does not flag a literal shorter than the default minimum length", 0,
            shortLiteral.Analyse().Findings.CountOf(ComplexityRule.DuplicatedStringLiteral));

        var constant = new TestWorkspace();
        constant.Add("a.cs", """
            class C
            {
                const string Message = "not found in catalog";
                string A() => Message;
            }
            """);
        harness.Equal("does not flag a literal already assigned to a const", 0,
            constant.Analyse().Findings.CountOf(ComplexityRule.DuplicatedStringLiteral));

        var belowOccurrenceThreshold = new TestWorkspace();
        belowOccurrenceThreshold.Add("a.cs", "class C { string A() => \"not found in catalog\"; string B() => \"not found in catalog\"; }");
        harness.Equal("does not flag a literal repeated only twice against the default minimum of three", 0,
            belowOccurrenceThreshold.Analyse().Findings.CountOf(ComplexityRule.DuplicatedStringLiteral));
    }

    private static void AsyncSafetyRules(Harness harness)
    {
        harness.Group("Async safety rules");

        var blockingOnCall = new TestWorkspace();
        blockingOnCall.Add("a.cs", "class C { void M() { var x = Fetch().Result; } object Fetch() => null; }"
            .Replace("Fetch()", "FetchAsync()").Replace("object FetchAsync", "object Fetch"));
        harness.Equal("flags blocking on a call named for an asynchronous operation", 1,
            blockingOnCall.Analyse().Findings.CountOf(AsyncSafetyRule.BlockingOnTask));

        var unrelatedResult = new TestWorkspace();
        unrelatedResult.Add("a.cs", "class C { void M() { var x = Compute().Result; } Outcome Compute() => null; }");
        harness.Equal("leaves an unrelated member called Result alone", 0,
            unrelatedResult.Analyse().Findings.CountOf(AsyncSafetyRule.BlockingOnTask));

        var namedTask = new TestWorkspace();
        namedTask.Add("a.cs", "class C { void M() { var t = Start(); var x = t.Result; } object Start() => null; }"
            .Replace("var t =", "var loadTask =").Replace("t.Result", "loadTask.Result"));
        harness.Equal("flags blocking on a variable named after a task", 1,
            namedTask.Analyse().Findings.CountOf(AsyncSafetyRule.BlockingOnTask));

        var waitCall = new TestWorkspace();
        waitCall.Add("a.cs", "class C { async System.Threading.Tasks.Task M() { Save().Wait(); } }");
        harness.Equal("flags Wait() inside an async method", 1,
            waitCall.Analyse().Findings.CountOf(AsyncSafetyRule.BlockingOnTask));

        var getAwaiter = new TestWorkspace();
        getAwaiter.Add("a.cs", "class C { void M() { var x = LoadAsync().GetAwaiter().GetResult(); } }");
        harness.Equal("flags GetAwaiter().GetResult()", 1,
            getAwaiter.Analyse().Findings.CountOf(AsyncSafetyRule.BlockingOnTask));

        var unawaited = new TestWorkspace();
        unawaited.Add("a.cs", "class C { void M() { SaveAsync(); } }");
        harness.Equal("flags a discarded task-returning call", 1,
            unawaited.Analyse().Findings.CountOf(AsyncSafetyRule.UnawaitedTask));

        var awaited = new TestWorkspace();
        awaited.Add("a.cs", "class C { async System.Threading.Tasks.Task M() { await SaveAsync(); } }");
        harness.Equal("does not flag an awaited call", 0,
            awaited.Analyse().Findings.CountOf(AsyncSafetyRule.UnawaitedTask));

        var assigned = new TestWorkspace();
        assigned.Add("a.cs", "class C { void M() { var t = SaveAsync(); } }");
        harness.Equal("does not flag a call whose task is kept", 0,
            assigned.Analyse().Findings.CountOf(AsyncSafetyRule.UnawaitedTask));

        var discarded = new TestWorkspace();
        discarded.Add("a.cs", "class C { void M() { _ = SaveAsync(); } }");
        harness.Equal("does not flag a deliberately discarded call", 0,
            discarded.Analyse().Findings.CountOf(AsyncSafetyRule.UnawaitedTask));

        var syncCall = new TestWorkspace();
        syncCall.Add("a.cs", "class C { void M() { Save(); } }");
        harness.Equal("does not flag an ordinary synchronous call", 0,
            syncCall.Analyse().Findings.CountOf(AsyncSafetyRule.UnawaitedTask));

        var asyncVoid = new TestWorkspace();
        asyncVoid.Add("a.cs", "class C { async void Go() { } }");
        harness.Equal("flags async void", 1, asyncVoid.Analyse().Findings.CountOf(AsyncSafetyRule.AsyncVoid));

        var eventHandler = new TestWorkspace();
        eventHandler.Add("a.cs", "class C { async void OnClick(object sender, System.EventArgs e) { } }");
        harness.Equal("exempts an event handler, where async void is the required signature", 0,
            eventHandler.Analyse().Findings.CountOf(AsyncSafetyRule.AsyncVoid));

        var asyncTask = new TestWorkspace();
        asyncTask.Add("a.cs", "class C { async System.Threading.Tasks.Task Go() { } }");
        harness.Equal("does not flag async Task", 0, asyncTask.Analyse().Findings.CountOf(AsyncSafetyRule.AsyncVoid));

        var emptyCatch = new TestWorkspace();
        emptyCatch.Add("a.cs", "class C { void M() { try { } catch { } } }");
        harness.Equal("flags an empty catch block", 1,
            emptyCatch.Analyse().Findings.CountOf(AsyncSafetyRule.SwallowedException));

        var handledCatch = new TestWorkspace();
        handledCatch.Add("a.cs", "class C { void M() { try { } catch (System.Exception e) { Log(e); } } }");
        harness.Equal("does not flag a catch block that does something", 0,
            handledCatch.Analyse().Findings.CountOf(AsyncSafetyRule.SwallowedException));

        var suppressed = new TestWorkspace();
        suppressed.Add("a.cs", "class C { async void Go() { } // archon-ignore[AR0012] required by the framework\n }");
        harness.Equal("honours a suppression marker on an async void method", 0,
            suppressed.Analyse().Findings.CountOf(AsyncSafetyRule.AsyncVoid));
    }

    private static void PerfHintRules(Harness harness)
    {
        harness.Group("Performance hints");

        var countGreater = new TestWorkspace();
        countGreater.Add("a.cs", "class C { bool M(System.Collections.Generic.IEnumerable<int> s) => s.Count() > 0; }");
        harness.Equal("flags Count() > 0", 1, countGreater.Analyse().Findings.CountOf(PerfHintRule.CountInsteadOfAny));

        var countZero = new TestWorkspace();
        countZero.Add("a.cs", "class C { bool M(System.Collections.Generic.IEnumerable<int> s) => s.Count() == 0; }");
        harness.Equal("flags Count() == 0", 1, countZero.Analyse().Findings.CountOf(PerfHintRule.CountInsteadOfAny));

        var countProperty = new TestWorkspace();
        countProperty.Add("a.cs", "class C { bool M(System.Collections.Generic.List<int> s) => s.Count > 0; }");
        harness.Equal("does not flag the Count property, which is not a walk of the sequence", 0,
            countProperty.Analyse().Findings.CountOf(PerfHintRule.CountInsteadOfAny));

        var countComparedToMore = new TestWorkspace();
        countComparedToMore.Add("a.cs", "class C { bool M(System.Collections.Generic.IEnumerable<int> s) => s.Count() > 5; }");
        harness.Equal("does not flag a genuine count comparison", 0,
            countComparedToMore.Analyse().Findings.CountOf(PerfHintRule.CountInsteadOfAny));

        var concatInLoop = new TestWorkspace();
        concatInLoop.Add("a.cs", "class C { void M() { string s = \"\"; for (int i = 0; i < 10; i++) { s += i; } } }");
        harness.Equal("flags string concatenation in a loop", 1,
            concatInLoop.Analyse().Findings.CountOf(PerfHintRule.ConcatenationInLoop));

        var concatOutsideLoop = new TestWorkspace();
        concatOutsideLoop.Add("a.cs", "class C { void M() { string s = \"\"; s += \"x\"; } }");
        harness.Equal("does not flag concatenation outside a loop", 0,
            concatOutsideLoop.Analyse().Findings.CountOf(PerfHintRule.ConcatenationInLoop));

        var numericInLoop = new TestWorkspace();
        numericInLoop.Add("a.cs", "class C { void M() { int total = 0; for (int i = 0; i < 10; i++) { total += i; } } }");
        harness.Equal("does not flag numeric accumulation in a loop", 0,
            numericInLoop.Analyse().Findings.CountOf(PerfHintRule.ConcatenationInLoop));

        var inferredType = new TestWorkspace();
        inferredType.Add("a.cs", "class C { void M() { var s = \"\"; for (int i = 0; i < 10; i++) { s += i; } } }");
        harness.Equal("stays silent when the declared type is inferred rather than written", 0,
            inferredType.Analyse().Findings.CountOf(PerfHintRule.ConcatenationInLoop));

        var redundant = new TestWorkspace();
        redundant.Add("a.cs", "class C { void M(System.Collections.Generic.IEnumerable<int> s) { var x = s.ToList().Where(i => i > 1); } }");
        harness.Equal("flags a copy that is immediately filtered again", 1,
            redundant.Analyse().Findings.CountOf(PerfHintRule.RedundantMaterialisation));

        var terminalToList = new TestWorkspace();
        terminalToList.Add("a.cs", "class C { void M(System.Collections.Generic.IEnumerable<int> s) { var x = s.Where(i => i > 1).ToList(); } }");
        harness.Equal("does not flag a copy taken at the end of a chain", 0,
            terminalToList.Analyse().Findings.CountOf(PerfHintRule.RedundantMaterialisation));

        var inlineSql = new TestWorkspace();
        inlineSql.Add("a.cs", "class C { string Q = \"SELECT * FROM dbo.Orders\"; }");
        harness.Equal("flags a wildcard select inside a string literal that parses as SQL", 1,
            inlineSql.Analyse().Findings.CountOf(PerfHintRule.InlineWildcardSelect));

        var inlineSqlNamed = new TestWorkspace();
        inlineSqlNamed.Add("a.cs", "class C { string Q = \"SELECT Id, Name FROM dbo.Orders\"; }");
        harness.Equal("does not flag inline SQL that names its columns", 0,
            inlineSqlNamed.Analyse().Findings.CountOf(PerfHintRule.InlineWildcardSelect));

        var prose = new TestWorkspace();
        prose.Add("a.cs", "class C { string M = \"Please select * from the list of options\"; }");
        harness.Equal("does not flag prose that merely mentions selecting and an asterisk", 0,
            prose.Analyse().Findings.CountOf(PerfHintRule.InlineWildcardSelect));
    }

    private static void ConfigKeyRules(Harness harness)
    {
        harness.Group("Configuration keys");

        string root = Path.Combine(Path.GetTempPath(), "archon-config-tests");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "appsettings.json"), """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "ConnectionStrings": { "Main": "Server=." },
              "Feature": { "Enabled": true }
            }
            """);
        File.WriteAllText(Path.Combine(root, "appsettings.Development.json"), """
            { "DevOnly": { "Value": 1 } }
            """);
        File.WriteAllText(Path.Combine(root, "Reader.cs"), """
            namespace App
            {
                class Reader
                {
                    public Reader(IConfiguration configuration, Other other)
                    {
                        var a = configuration["Feature:Enabled"];
                        var b = configuration["Missing:Key"];
                        var c = configuration.GetConnectionString("Main");
                        var d = configuration.GetConnectionString("Absent");
                        var e = configuration.GetSection("Logging");
                        var f = configuration.GetSection("DevOnly");
                        var g = other["Feature:Enabled"];
                    }
                }
            }
            """);

        AnalysisResult result = AnalyseDirectory(root);
        var keys = result.Findings.Where(f => f.RuleId == ConfigKeyRule.Id).Select(f => f.Message).ToList();

        harness.Equal("reports exactly the keys that are absent", 2, keys.Count);
        harness.Check("reports a missing indexer key", keys.Any(m => m.Contains("Missing:Key")));
        harness.Check("maps a connection-string lookup onto its settings path", keys.Any(m => m.Contains("ConnectionStrings:Absent")));
        harness.Check("accepts a key present in the base settings file", !keys.Any(m => m.Contains("Feature:Enabled")));
        harness.Check("accepts a key that names a section rather than a leaf", !keys.Any(m => m.Contains("'Logging'")));
        harness.Check("unions overlay settings files, so a development-only key is known",
            !keys.Any(m => m.Contains("DevOnly")));
        harness.Check("ignores an indexer whose receiver is not a configuration type",
            keys.Count(m => m.Contains("Feature:Enabled")) == 0);

        string malformed = Path.Combine(root, "appsettings.Broken.json");
        File.WriteAllText(malformed, "{ this is not json");
        AnalysisResult withMalformed = AnalyseDirectory(root);
        harness.Equal("reports an unreadable settings file once", 1,
            withMalformed.Findings.CountOf(ConfigKeyRule.SettingsUnreadable));
        harness.Check("still reports key findings despite one unreadable file",
            withMalformed.Findings.CountOf(ConfigKeyRule.Id) > 0);
        File.Delete(malformed);

        string bare = Path.Combine(root, "no-settings");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "Bare.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(bare, "Bare.cs"), """
            namespace App { class Bare { public Bare(IConfiguration c) { var x = c["Any:Key"]; } } }
            """);
        AnalysisResult bareResult = AnalyseDirectory(bare);
        harness.Equal("reports nothing when a project has no settings files at all", 0,
            bareResult.Findings.CountOf(ConfigKeyRule.Id));

        Directory.Delete(root, recursive: true);
    }

    private static void ProjectCycleRules(Harness harness)
    {
        harness.Group("Project reference cycles");

        harness.Check("finds a cycle of three",
            ProjectCycleRule.StronglyConnectedComponents(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = new() { "b" },
                ["b"] = new() { "c" },
                ["c"] = new() { "a" }
            }).Any(c => c.Count == 3));

        harness.Check("finds no cycle in a chain",
            ProjectCycleRule.StronglyConnectedComponents(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = new() { "b" },
                ["b"] = new() { "c" },
                ["c"] = new List<string>()
            }).All(c => c.Count == 1));

        harness.Check("finds a two-project cycle",
            ProjectCycleRule.StronglyConnectedComponents(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = new() { "b" },
                ["b"] = new() { "a" }
            }).Any(c => c.Count == 2));

        harness.Check("finds no cycle in a diamond",
            ProjectCycleRule.StronglyConnectedComponents(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["top"] = new() { "left", "right" },
                ["left"] = new() { "bottom" },
                ["right"] = new() { "bottom" },
                ["bottom"] = new List<string>()
            }).All(c => c.Count == 1));

        string root = Path.Combine(Path.GetTempPath(), "archon-cycle-tests");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        foreach (string name in new[] { "A", "B", "C" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, $"{name}.cs"), $"namespace {name} {{ class T {{ }} }}");
        }
        Write(root, "A", "B");
        Write(root, "B", "C");
        Write(root, "C", "A");

        AnalysisResult cyclic = AnalyseDirectory(root);
        harness.Equal("reports every project in the cycle, not one arbitrary edge", 3,
            cyclic.Findings.CountOf(ProjectCycleRule.Id));

        Write(root, "C", null);
        AnalysisResult broken = AnalyseDirectory(root);
        harness.Equal("reports nothing once the cycle is broken", 0, broken.Findings.CountOf(ProjectCycleRule.Id));

        File.WriteAllText(Path.Combine(root, "C", "C.csproj"), "<Project><ItemGroup><Unclosed></Project>");
        AnalysisResult unreadable = AnalyseDirectory(root);
        harness.Equal("reports a project file it could not read rather than dropping it silently", 1,
            unreadable.Findings.CountOf(ProjectCycleRule.ProjectUnreadable));

        Directory.Delete(root, recursive: true);

        static void Write(string root, string name, string? referenceTo)
        {
            string reference = referenceTo is null
                ? ""
                : $"<ItemGroup><ProjectReference Include=\"..\\{referenceTo}\\{referenceTo}.csproj\" /></ItemGroup>";
            File.WriteAllText(Path.Combine(root, name, $"{name}.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">{reference}</Project>");
        }
    }

    private static void CallGraphChecks(Harness harness)
    {
        harness.Group("Method impact");

        string root = Path.Combine(Path.GetTempPath(), "archon-callgraph-tests");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "Tests"));
        File.WriteAllText(Path.Combine(root, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "Tests", "Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string target = Path.Combine(root, "App", "Orders.cs");
        File.WriteAllText(target, """
            namespace App
            {
                class Orders
                {
                    public void Place(int id) { Validate(id); }
                    public void Validate(int id) { }
                    public void Unused() { }
                    public void Overloaded(int a) { }
                    public void Overloaded(int a, int b) { }
                    public string Describe() => Format();
                    public string Format() => "";
                }
            }
            """);

        File.WriteAllText(Path.Combine(root, "App", "Callers.cs"), """
            namespace App
            {
                class Checkout
                {
                    public void Run(Orders orders)
                    {
                        orders.Place(1);
                        orders.Validate(1);
                        orders.Overloaded(1);
                    }
                }
            }
            """);

        File.WriteAllText(Path.Combine(root, "Tests", "OrderTests.cs"), """
            namespace Tests
            {
                class OrderTests
                {
                    [Fact]
                    public void PlacesAnOrder() { new App.Orders().Place(1); }

                    [Theory]
                    public void PlacesAnotherOrder() { new App.Orders().Place(2); }

                    public void NotATest() { new App.Orders().Unused(); }
                }
            }
            """);

        var sources = new SourceCache();
        var graph = new CallGraph(sources);
        WorkspaceModel workspace = WorkspaceModel.Discover(root, ArchonConfig.DefaultExcludes);
        ImpactResult result = graph.Impact(workspace, target, maxDepth: 6, maxCallers: 50);

        MethodImpact? Find(string name, int arity = 0) =>
            result.Methods.FirstOrDefault(m => m.MethodName == name && (arity == 0 || m.Arity == arity));

        harness.Equal("indexes every method in the target file", 7, result.Methods.Count);
        harness.Equal("counts a call from another file", 3, Find("Place")?.ReferenceCount);
        harness.Equal("attributes callers to the projects they sit in", 2, Find("Place")?.ProjectCount);
        harness.Equal("reports no callers for a method nothing calls", 0, Find("Overloaded", 2)?.ReferenceCount);
        harness.Equal("distinguishes overloads by argument count", 1, Find("Overloaded", 1)?.ReferenceCount);
        harness.Equal("counts distinct test methods that reach a method", 2, Find("Place")?.CoveringTestCount);
        harness.Equal("counts tests reaching a method indirectly", 2, Find("Validate")?.CoveringTestCount);
        harness.Equal("does not count a method without a test attribute as a test", 0, Find("Unused")?.CoveringTestCount);
        harness.Equal("reads calls from an expression-bodied method", 1, Find("Format")?.ReferenceCount);
        harness.Check("does not report the bound as hit when the search completed",
            Find("Place")?.DepthBounded == false);
        harness.Check("names the caller locations it counted",
            Find("Place")?.Callers.Count == 3 && Find("Place")!.Callers.All(c => c.Line >= 0));

        ImpactResult shallow = graph.Impact(workspace, target, maxDepth: 1, maxCallers: 50);
        harness.Check("reports the bound as hit when callers remain beyond it",
            shallow.Methods.First(m => m.MethodName == "Validate").DepthBounded);
        harness.Equal("finds only direct tests within a depth of one", 0,
            shallow.Methods.First(m => m.MethodName == "Validate").CoveringTestCount);

        harness.Equal("caps the caller list at the requested limit", 1,
            graph.Impact(workspace, target, maxDepth: 6, maxCallers: 1)
                .Methods.First(m => m.MethodName == "Place").Callers.Count);

        string extra = Path.Combine(root, "App", "More.cs");
        File.WriteAllText(extra, "namespace App { class More { public void Go(Orders o) { o.Place(9); } } }");
        WorkspaceModel grown = WorkspaceModel.Discover(root, ArchonConfig.DefaultExcludes);
        harness.Equal("a new file adds its calls on the next query", 4,
            graph.Impact(grown, target, maxDepth: 6, maxCallers: 50)
                .Methods.First(m => m.MethodName == "Place").ReferenceCount);

        File.Delete(extra);
        WorkspaceModel shrunk = WorkspaceModel.Discover(root, ArchonConfig.DefaultExcludes);
        harness.Equal("a deleted file stops contributing calls", 3,
            graph.Impact(shrunk, target, maxDepth: 6, maxCallers: 50)
                .Methods.First(m => m.MethodName == "Place").ReferenceCount);

        File.WriteAllText(Path.Combine(root, "App", "Callers.cs"), """
            namespace App
            {
                class Checkout
                {
                    public void Run(Orders orders) { }
                }
            }
            """);
        sources.Invalidate(Path.Combine(root, "App", "Callers.cs"));
        graph.Invalidate(Path.Combine(root, "App", "Callers.cs"));
        harness.Equal("an edited file is re-read once invalidated", 2,
            graph.Impact(shrunk, target, maxDepth: 6, maxCallers: 50)
                .Methods.First(m => m.MethodName == "Place").ReferenceCount);

        harness.Equal("reports nothing for a file it has never seen", 0,
            graph.Impact(shrunk, Path.Combine(root, "App", "Absent.cs"), maxDepth: 6, maxCallers: 50).Methods.Count);

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Members other than plain methods hold calls too. A codebase that injects its dependencies
    /// makes most of its calls from constructors, so counting only method bodies reports nothing
    /// for methods that are in constant use.
    /// </summary>
    private static void CallGraphMemberChecks(Harness harness)
    {
        harness.Group("Method impact across member kinds");

        string root = Path.Combine(Path.GetTempPath(), "archon-callgraph-members");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string target = Path.Combine(root, "Service.cs");
        File.WriteAllText(target, """
            namespace App
            {
                class Service
                {
                    public Service() { Configure(); }
                    public void Configure() { }
                    public void FromProperty() { }
                    public void FromLocal() { }
                    public void FromIndexer() { }
                    public int Value { get { FromProperty(); return 1; } }
                    public int Auto { get; set; }
                    public string this[int i] { get { FromIndexer(); return ""; } }
                    public void Outer() { void Inner() { FromLocal(); } Inner(); }
                }
            }
            """);

        File.WriteAllText(Path.Combine(root, "Uses.cs"), """
            namespace App
            {
                class Uses
                {
                    public void Build() { var s = new Service(); }
                }
            }
            """);

        var sources = new SourceCache();
        var graph = new CallGraph(sources);
        WorkspaceModel workspace = WorkspaceModel.Discover(root, ArchonConfig.DefaultExcludes);
        ImpactResult result = graph.Impact(workspace, target, maxDepth: 6, maxCallers: 50);

        int Count(string name) =>
            result.Methods.FirstOrDefault(m => m.MethodName == name)?.ReferenceCount ?? -1;

        harness.Equal("counts a call made from a constructor", 1, Count("Configure"));
        harness.Equal("counts a call made from a property accessor", 1, Count("FromProperty"));
        harness.Equal("counts a call made from a local function", 1, Count("FromLocal"));
        harness.Equal("counts a call made from an indexer", 1, Count("FromIndexer"));
        harness.Equal("treats object creation as reaching the constructor", 1, Count("Service"));
        harness.Check("indexes a local function in its own right",
            result.Methods.Any(m => m.MethodName == "Inner"));
        harness.Check("does not index an auto-property, which holds no calls",
            result.Methods.All(m => m.MethodName != "Auto"));
        harness.Check("indexes a property with a body exactly once",
            result.Methods.Count(m => m.MethodName == "Value") == 1);

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// A baseline is only useful if fixing one finding leaves the others accepted. Numbering
    /// duplicates by position alone breaks that: removing the first renumbers the rest, so a
    /// developer is failed by a check for findings they never touched.
    /// </summary>
    private static void BaselineStabilityRules(Harness harness)
    {
        harness.Group("Baseline stability");

        var workspace = new TestWorkspace();
        workspace.Add("a.sql", "SELECT * FROM dbo.First;\nSELECT * FROM dbo.Second;\nSELECT * FROM dbo.Third;");
        AnalysisResult all = workspace.Analyse();
        harness.Equal("reports each of three findings", 3, all.Findings.Count);

        var baseline = new Baseline(all.Findings.Select(f => new BaselineEntry { Fingerprint = f.Fingerprint }));

        var fixedFirst = new TestWorkspace();
        fixedFirst.Add("a.sql", "SELECT Id FROM dbo.First;\nSELECT * FROM dbo.Second;\nSELECT * FROM dbo.Third;");
        harness.Equal("fixing the first leaves the other two accepted",
            0, fixedFirst.Analyse(baseline).Findings.Count);

        var fixedMiddle = new TestWorkspace();
        fixedMiddle.Add("a.sql", "SELECT * FROM dbo.First;\nSELECT Id FROM dbo.Second;\nSELECT * FROM dbo.Third;");
        harness.Equal("fixing the middle leaves the other two accepted",
            0, fixedMiddle.Analyse(baseline).Findings.Count);

        var reordered = new TestWorkspace();
        reordered.Add("a.sql", "SELECT * FROM dbo.Third;\nSELECT * FROM dbo.First;\nSELECT * FROM dbo.Second;");
        harness.Equal("reordering the statements keeps every finding accepted",
            0, reordered.Analyse(baseline).Findings.Count);

        var added = new TestWorkspace();
        added.Add("a.sql", "SELECT * FROM dbo.First;\nSELECT * FROM dbo.Second;\nSELECT * FROM dbo.Third;\nSELECT * FROM dbo.Fourth;");
        harness.Equal("a genuinely new finding is still reported",
            1, added.Analyse(baseline).Findings.Count);
    }

    /// <summary>
    /// The cache is bounded, because a long-lived process holding a syntax tree for every file it
    /// ever touched is the problem the warm process was meant to avoid. Editor text is exempt:
    /// evicting it would substitute what is on disk for what the user is looking at.
    /// </summary>
    private static void SourceCacheRules(Harness harness)
    {
        harness.Group("Source cache");

        string root = Path.Combine(Path.GetTempPath(), "archon-cache-tests");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);

        var cache = new SourceCache(capacity: 16);
        for (int i = 0; i < 64; i++)
        {
            string path = Path.Combine(root, $"F{i}.cs");
            File.WriteAllText(path, $"class C{i} {{ }}");
            cache.GetCSharp(path);
        }
        harness.Check("stays within its capacity once full", cache.Count <= 16);

        string unsaved = Path.Combine(root, "Unsaved.cs");
        cache.SetText(unsaved, "class Held { }");
        for (int i = 0; i < 64; i++)
        {
            cache.GetCSharp(Path.Combine(root, $"F{i}.cs"));
        }
        harness.Equal("never evicts unsaved editor text", "class Held { }", cache.GetText(unsaved));

        cache.SetText(unsaved, "class Replaced { }");
        harness.Equal("replaces editor text when it changes", "class Replaced { }", cache.GetText(unsaved));

        string evicted = Path.Combine(root, "F0.cs");
        File.WriteAllText(evicted, "class Rewritten { }");
        cache.Invalidate(evicted);
        harness.Equal("re-reads a file once invalidated", "class Rewritten { }", cache.GetText(evicted));

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Files are attributed to every project above them, so a project nested inside another is
    /// owned by both. This held when each project scanned the whole file list for itself, and must
    /// still hold now that files are walked upwards instead.
    /// </summary>
    private static void ProjectAttributionRules(Harness harness)
    {
        harness.Group("Project attribution");

        string root = Path.Combine(Path.GetTempPath(), "archon-project-tests");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(root, "Outer", "Inner", "Deep"));
        File.WriteAllText(Path.Combine(root, "Outer", "Outer.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Outer", "Inner", "Inner.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Outer", "Top.cs"), "class Top { }");
        File.WriteAllText(Path.Combine(root, "Outer", "Inner", "Mid.cs"), "class Mid { }");
        File.WriteAllText(Path.Combine(root, "Outer", "Inner", "Deep", "Low.cs"), "class Low { }");

        WorkspaceModel workspace = WorkspaceModel.Discover(root, ArchonConfig.DefaultExcludes);
        ProjectModel? outer = workspace.Projects.FirstOrDefault(p => p.Name == "Outer");
        ProjectModel? inner = workspace.Projects.FirstOrDefault(p => p.Name == "Inner");

        harness.Equal("finds both projects in one walk", 2, workspace.Projects.Count);
        harness.Equal("discovers every source file", 3, workspace.Files.Count);
        harness.Equal("the outer project owns everything beneath it", 3, outer?.Files.Count);
        harness.Equal("the nested project owns only its own subtree", 2, inner?.Files.Count);
        harness.Equal("the most specific project wins", "Inner",
            workspace.ProjectOf(Path.Combine(root, "Outer", "Inner", "Deep", "Low.cs"))?.Name);
        harness.Equal("a file above the nested project belongs to the outer one", "Outer",
            workspace.ProjectOf(Path.Combine(root, "Outer", "Top.cs"))?.Name);

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Runs a full pass over a real directory, for rules that must read files from disk.</summary>
    private static AnalysisResult AnalyseDirectory(string root)
    {
        var registry = new RuleRegistry();
        registry.Add(new BuiltInRulePack());
        var config = new ArchonConfig { WorkspaceRoot = Path.GetFullPath(root) };
        var engine = new AnalysisEngine(registry, new SourceCache());
        WorkspaceModel workspace = WorkspaceModel.Discover(root, config.EffectiveExcludes());
        return engine.AnalyseWorkspace(workspace, config, Baseline.Empty);
    }

    private static void LayerRules(Harness harness)
    {
        harness.Group("Layer dependency rules");

        Dictionary<string, List<string>> layers = new()
        {
            ["Web"] = new List<string> { "Sample.Web" },
            ["Infrastructure"] = new List<string> { "Sample.Infrastructure" }
        };
        List<LayerEdge> deny = new() { new LayerEdge { Id = "no-web-to-infra", From = "Web", To = "Infrastructure" } };

        var byUsing = new TestWorkspace().WithLayers("denylist", layers, deny);
        byUsing.Add("Web/A.cs", "using Sample.Infrastructure;\nnamespace Sample.Web { class A { } }");
        harness.Equal("flags a forbidden using directive", 1, byUsing.Analyse().Findings.CountOf(LayerDependencyRule.Id));

        var byQualifiedName = new TestWorkspace().WithLayers("denylist", layers, deny);
        byQualifiedName.Add("Web/B.cs", "namespace Sample.Web { class B { object x = new Sample.Infrastructure.Store(); } }");
        harness.Equal("flags a fully qualified reference with no using", 1, byQualifiedName.Analyse().Findings.CountOf(LayerDependencyRule.Id));

        var both = new TestWorkspace().WithLayers("denylist", layers, deny);
        both.Add("Web/C.cs", "using Sample.Infrastructure;\nnamespace Sample.Web { class C { object x = new Sample.Infrastructure.Store(); } }");
        harness.Equal("counts a using and a qualified name on different lines separately", 2, both.Analyse().Findings.CountOf(LayerDependencyRule.Id));

        var allowedDirection = new TestWorkspace().WithLayers("denylist", layers, deny);
        allowedDirection.Add("Infra/D.cs", "using Sample.Web;\nnamespace Sample.Infrastructure { class D { } }");
        harness.Equal("permits the reverse direction under a denylist", 0, allowedDirection.Analyse().Findings.CountOf(LayerDependencyRule.Id));

        var ungoverned = new TestWorkspace().WithLayers("denylist", layers, deny);
        ungoverned.Add("Other/E.cs", "using Sample.Infrastructure;\nnamespace Other.Tooling { class E { } }");
        harness.Equal("never flags a namespace belonging to no layer", 0, ungoverned.Analyse().Findings.CountOf(LayerDependencyRule.Id));

        var allowlist = new TestWorkspace().WithLayers("allowlist", layers, deny: null,
            allow: new List<LayerEdge> { new() { Id = "infra-to-web", From = "Infrastructure", To = "Web" } });
        allowlist.Add("Web/F.cs", "using Sample.Infrastructure;\nnamespace Sample.Web { class F { } }");
        harness.Equal("flags an unlisted edge under an allowlist", 1, allowlist.Analyse().Findings.CountOf(LayerDependencyRule.Id));

        var nested = new TestWorkspace().WithLayers("denylist",
            new Dictionary<string, List<string>>
            {
                ["Web"] = new List<string> { "Sample.Web" },
                ["Admin"] = new List<string> { "Sample.Web.Admin" },
                ["Infrastructure"] = new List<string> { "Sample.Infrastructure" }
            },
            new List<LayerEdge> { new() { Id = "no-web-to-infra", From = "Web", To = "Infrastructure" } });
        nested.Add("Admin/G.cs", "using Sample.Infrastructure;\nnamespace Sample.Web.Admin { class G { } }");
        harness.Equal("resolves the most specific layer prefix, so a nested layer is not governed by its parent's rule",
            0, nested.Analyse().Findings.CountOf(LayerDependencyRule.Id));

        var unconfigured = new TestWorkspace();
        unconfigured.Add("Web/H.cs", "using Sample.Infrastructure;\nnamespace Sample.Web { class H { } }");
        harness.Equal("stays silent when no layers are configured", 0, unconfigured.Analyse().Findings.CountOf(LayerDependencyRule.Id));
    }

    private static void LifetimeRules(Harness harness)
    {
        harness.Group("Service lifetime rules");

        const string services = """
            namespace App
            {
                interface ICache { }
                interface IStore { }
                class Cache : ICache { public Cache(IStore store) { } }
                class Store : IStore { }
                class Setup
                {
                    void Configure(IServiceCollection services)
                    {
                        services.AddSingleton<ICache, Cache>();
                        services.AddScoped<IStore, Store>();
                    }
                }
            }
            """;

        var singletonOverScoped = new TestWorkspace();
        singletonOverScoped.Add("Setup.cs", services);
        AnalysisResult result = singletonOverScoped.Analyse();
        harness.Equal("flags a singleton holding a scoped service", 1, result.Findings.CountOf(CaptiveDependencyRule.SingletonCapturesScoped));
        harness.Equal("reports it as an error by default", Severity.Error,
            result.Findings.FirstOf(CaptiveDependencyRule.SingletonCapturesScoped)?.Severity ?? Severity.Off);

        var matched = new TestWorkspace();
        matched.Add("Setup.cs", services.Replace("AddSingleton<ICache, Cache>", "AddScoped<ICache, Cache>"));
        harness.Equal("stays silent when both lifetimes match", 0,
            matched.Analyse().Findings.Count(f => f.Category == "lifetime"));

        var singletonOverTransient = new TestWorkspace();
        singletonOverTransient.Add("Setup.cs", services.Replace("AddScoped<IStore, Store>", "AddTransient<IStore, Store>"));
        AnalysisResult transient = singletonOverTransient.Analyse();
        harness.Equal("flags a singleton holding a transient service under its own id", 1,
            transient.Findings.CountOf(CaptiveDependencyRule.SingletonCapturesTransient));
        harness.Equal("reports that as a warning, not an error", Severity.Warning,
            transient.Findings.FirstOf(CaptiveDependencyRule.SingletonCapturesTransient)?.Severity ?? Severity.Off);

        var scopedOverTransient = new TestWorkspace();
        scopedOverTransient.Add("Setup.cs", services
            .Replace("AddSingleton<ICache, Cache>", "AddScoped<ICache, Cache>")
            .Replace("AddScoped<IStore, Store>", "AddTransient<IStore, Store>"));
        harness.Equal("reports a scoped service holding a transient one as information", Severity.Information,
            scopedOverTransient.Analyse().Findings.FirstOf(CaptiveDependencyRule.ScopedCapturesTransient)?.Severity ?? Severity.Off);

        var unregistered = new TestWorkspace();
        unregistered.Add("Setup.cs", """
            namespace App
            {
                interface ICache { }
                class Cache : ICache { public Cache(IMysteryService other) { } }
                class Setup { void Configure(IServiceCollection s) { s.AddSingleton<ICache, Cache>(); } }
            }
            """);
        harness.Equal("leaves the unregistered-dependency check off by default", 0,
            unregistered.Analyse().Findings.CountOf(CaptiveDependencyRule.UnregisteredDependency));

        var unregisteredOn = new TestWorkspace().WithSeverity(CaptiveDependencyRule.UnregisteredDependency, "information");
        unregisteredOn.Add("Setup.cs", """
            namespace App
            {
                interface ICache { }
                class Cache : ICache { public Cache(IMysteryService other) { } }
                class Setup { void Configure(IServiceCollection s) { s.AddSingleton<ICache, Cache>(); } }
            }
            """);
        harness.Equal("reports it once switched on", 1,
            unregisteredOn.Analyse().Findings.CountOf(CaptiveDependencyRule.UnregisteredDependency));

        var frameworkType = new TestWorkspace().WithSeverity(CaptiveDependencyRule.UnregisteredDependency, "information");
        frameworkType.Add("Setup.cs", """
            namespace App
            {
                interface ICache { }
                class Cache : ICache { public Cache(ILogger log, IConfiguration config) { } }
                class Setup { void Configure(IServiceCollection s) { s.AddSingleton<ICache, Cache>(); } }
            }
            """);
        harness.Equal("does not flag well-known framework dependencies as unregistered", 0,
            frameworkType.Analyse().Findings.CountOf(CaptiveDependencyRule.UnregisteredDependency));

        var factory = new TestWorkspace();
        factory.Add("Setup.cs", """
            namespace App
            {
                interface ICache { }
                interface IStore { }
                class Cache : ICache { public Cache(IStore store) { } }
                class Store : IStore { }
                class Setup
                {
                    void Configure(IServiceCollection s)
                    {
                        s.AddSingleton<ICache>(p => new Cache(null));
                        s.AddScoped<IStore, Store>();
                    }
                }
            }
            """);
        harness.Equal("stays silent for a factory registration whose implementation is not statically known", 0,
            factory.Analyse().Findings.Count(f => f.Category == "lifetime"));
    }

    private static void SuppressionRules(Harness harness)
    {
        harness.Group("Suppression markers");

        SuppressionIndex sameLine = SuppressionIndex.Build("SELECT * FROM T; -- archon-ignore[SQ0001]");
        harness.Check("a marker suppresses its own line", sameLine.IsSuppressed("SQ0001", 0));
        harness.Check("a marker does not suppress an unnamed rule", !sameLine.IsSuppressed("AR0001", 0));

        SuppressionIndex previousLine = SuppressionIndex.Build("-- archon-ignore[SQ0001]\nSELECT * FROM T;");
        harness.Check("a marker suppresses the line below it", previousLine.IsSuppressed("SQ0001", 1));
        harness.Check("a marker does not reach two lines below", !previousLine.IsSuppressed("SQ0001", 2));

        SuppressionIndex bare = SuppressionIndex.Build("SELECT * FROM T; -- archon-ignore");
        harness.Check("a marker naming no rule suppresses every rule on the line", bare.IsSuppressed("ANYTHING", 0));

        SuppressionIndex several = SuppressionIndex.Build("x -- archon-ignore[SQ0001, AR0001]");
        harness.Check("a marker accepts several ids", several.IsSuppressed("SQ0001", 0) && several.IsSuppressed("AR0001", 0));
        harness.Check("a marker with a list does not suppress an id outside it", !several.IsSuppressed("AR0002", 0));

        SuppressionIndex fileWide = SuppressionIndex.Build("// archon-ignore-file[SQ0001]\nline\nline\nline");
        harness.Check("a file-wide marker suppresses every line", fileWide.IsSuppressed("SQ0001", 3));
        harness.Check("a file-wide marker respects its id list", !fileWide.IsSuppressed("AR0001", 3));

        harness.Check("text with no marker is cheap and suppresses nothing",
            !SuppressionIndex.Build("SELECT * FROM T;").IsSuppressed("SQ0001", 0));

        SuppressionIndex withReason = SuppressionIndex.Build("SELECT * FROM T; -- archon-ignore[SQ0001] a stable column set, covered by a contract test");
        harness.Check("a reason after the marker does not stop it matching", withReason.IsSuppressed("SQ0001", 0));

        var suppressed = new TestWorkspace();
        suppressed.Add("a.sql", "-- archon-ignore[SQ0001] reviewed\nSELECT * FROM dbo.T;");
        harness.Equal("the engine applies suppression without the rule knowing", 0,
            suppressed.Analyse().Findings.CountOf(SelectStarRule.Id));
    }

    private static void BaselineRules(Harness harness)
    {
        harness.Group("Baseline");

        var workspace = new TestWorkspace();
        workspace.Add("a.sql", "SELECT * FROM dbo.T;");
        AnalysisResult first = workspace.Analyse();
        harness.Equal("reports a finding with no baseline", 1, first.Findings.Count);

        var baseline = new Baseline(first.Findings.Select(f => new BaselineEntry
        {
            Fingerprint = f.Fingerprint,
            RuleId = f.RuleId,
            File = f.FilePath,
            Message = f.Message
        }));

        AnalysisResult second = workspace.Analyse(baseline);
        harness.Equal("an accepted finding is no longer reported", 0, second.Findings.Count);
        harness.Equal("an accepted finding is still counted separately", 1, second.BaselinedFindings.Count);

        var shifted = new TestWorkspace();
        shifted.Add("a.sql", "-- an unrelated comment added above\nSELECT * FROM dbo.T;");
        harness.Equal("an accepted finding stays accepted when lines move", 0, shifted.Analyse(baseline).Findings.Count);

        var extra = new TestWorkspace();
        extra.Add("a.sql", "SELECT * FROM dbo.T;");
        extra.Add("b.sql", "SELECT * FROM dbo.Other;");
        harness.Equal("a new finding is reported even while others are accepted", 1, extra.Analyse(baseline).Findings.Count);

        var changedMessage = new TestWorkspace();
        changedMessage.Add("a.sql", "SELECT * FROM dbo.T;\nSELECT * FROM dbo.T;");
        harness.Equal("a second identical finding in the same file is not covered by one accepted entry",
            1, changedMessage.Analyse(baseline).Findings.Count);

        harness.Equal("a missing baseline file reads as empty", 0,
            Baseline.Load(Path.Combine(Path.GetTempPath(), "archon-absent-baseline.json"), out _).Count);
    }

    private static void ConfigurationRules(Harness harness)
    {
        harness.Group("Configuration");

        var off = new TestWorkspace().WithSeverity(SelectStarRule.Id, "off");
        off.Add("a.sql", "SELECT * FROM dbo.T;");
        AnalysisResult result = off.Analyse();
        harness.Equal("a rule set to off produces nothing", 0, result.Findings.CountOf(SelectStarRule.Id));
        harness.Check("a disabled rule is reported as skipped",
            result.Skipped.Any(s => s.RuleId == SelectStarRule.Id && s.Reason.Contains("disabled")));

        var raised = new TestWorkspace().WithSeverity(SelectStarRule.Id, "error");
        raised.Add("a.sql", "SELECT * FROM dbo.T;");
        harness.Equal("an overridden severity is applied", Severity.Error,
            raised.Analyse().Findings.FirstOf(SelectStarRule.Id)?.Severity ?? Severity.Off);

        var byCategory = new TestWorkspace().WithSeverity("sql", "off");
        byCategory.Add("a.sql", "SELECT * FROM dbo.T;");
        harness.Equal("a category key disables every rule in it", 0, byCategory.Analyse().Findings.CountOf(SelectStarRule.Id));

        var specificWins = new TestWorkspace().WithSeverity("sql", "off").WithSeverity(SelectStarRule.Id, "warning");
        specificWins.Add("a.sql", "SELECT * FROM dbo.T;");
        harness.Equal("an explicit rule id overrides its category", 1, specificWins.Analyse().Findings.CountOf(SelectStarRule.Id));

        var session = new TestWorkspace().WithSeverity(SelectStarRule.Id, "error");
        session.Config.SessionOverrides[SelectStarRule.Id] = Severity.Off;
        session.Add("a.sql", "SELECT * FROM dbo.T;");
        harness.Equal("a session override takes precedence over the file", 0, session.Analyse().Findings.CountOf(SelectStarRule.Id));

        harness.Check("severity names are accepted in their common spellings",
            ArchonConfig.TryParseSeverity("warn", out Severity warn) && warn == Severity.Warning &&
            ArchonConfig.TryParseSeverity("info", out Severity info) && info == Severity.Information &&
            ArchonConfig.TryParseSeverity("none", out Severity none) && none == Severity.Off);
        harness.Check("an unknown severity name is rejected rather than guessed",
            !ArchonConfig.TryParseSeverity("severe", out _));
    }

    private static void ScopeRules(Harness harness)
    {
        harness.Group("Rule scope");

        Dictionary<string, List<string>> layers = new()
        {
            ["Web"] = new List<string> { "App.Web" },
            ["Data"] = new List<string> { "App.Data" }
        };

        var workspace = new TestWorkspace().WithLayers("denylist", layers,
            new List<LayerEdge> { new() { Id = "no-web-to-data", From = "Web", To = "Data" } });
        string file = workspace.Add("Web/A.cs", """
            using App.Data;
            namespace App.Web
            {
                interface ICache { }
                interface IStore { }
                class Cache : ICache { public Cache(IStore store) { } }
                class Store : IStore { }
                class Setup
                {
                    void Configure(IServiceCollection s)
                    {
                        s.AddSingleton<ICache, Cache>();
                        s.AddScoped<IStore, Store>();
                    }
                }
            }
            """);

        AnalysisResult wholeWorkspace = workspace.Analyse();
        harness.Equal("a full pass runs file-scope rules", 1, wholeWorkspace.Findings.CountOf(LayerDependencyRule.Id));
        harness.Equal("a full pass runs workspace-scope rules", 1,
            wholeWorkspace.Findings.CountOf(CaptiveDependencyRule.SingletonCapturesScoped));

        AnalysisResult singleFile = workspace.AnalyseFileOnly(file);
        harness.Equal("a single-file pass still runs file-scope rules", 1, singleFile.Findings.CountOf(LayerDependencyRule.Id));
        harness.Equal("a single-file pass does not run workspace-scope rules", 0,
            singleFile.Findings.CountOf(CaptiveDependencyRule.SingletonCapturesScoped));
    }

    private static void RegistryRules(Harness harness)
    {
        harness.Group("Rule registry");

        var registry = new RuleRegistry();
        registry.Add(new BuiltInRulePack());

        harness.Equal("every built-in condition is registered", 28, registry.Descriptors.Count);
        harness.Equal("rules that report several conditions are counted once as rules", 10, registry.Rules.Count);
        harness.Check("a descriptor can be found by id", registry.Find(SelectStarRule.Id) is not null);
        harness.Check("an unknown id resolves to nothing", registry.Find("ZZ9999") is null);
        harness.Check("registering the same pack twice is refused rather than duplicated",
            RegisterTwice().LoadDiagnostics.Count > 0);
        harness.Check("a missing rule pack file is reported and skipped", MissingPack().LoadDiagnostics.Count == 1);

        harness.Check("every descriptor id is unique",
            registry.Descriptors.Select(d => d.Descriptor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                == registry.Descriptors.Count);
        harness.Check("every descriptor has a title and a description",
            registry.Descriptors.All(d => d.Descriptor.Title.Length > 0 && d.Descriptor.Description.Length > 0));

        static RuleRegistry RegisterTwice()
        {
            var twice = new RuleRegistry();
            twice.Add(new BuiltInRulePack());
            twice.Add(new BuiltInRulePack());
            return twice;
        }

        static RuleRegistry MissingPack()
        {
            var registry = new RuleRegistry();
            registry.AddFromAssembly(Path.Combine(Path.GetTempPath(), "archon-absent-pack.dll"));
            return registry;
        }
    }

    private static void GlobRules(Harness harness)
    {
        harness.Group("Path exclusion");

        var matcher = new GlobMatcher(ArchonConfig.DefaultExcludes);
        harness.Check("excludes a nested build output folder", matcher.IsExcluded("src/App/bin/Debug/App.dll"));
        harness.Check("excludes a build output folder at the root", matcher.IsExcluded("bin/App.dll"));
        harness.Check("excludes intermediate output", matcher.IsExcluded("src/App/obj/project.assets.json"));
        harness.Check("does not exclude ordinary source", !matcher.IsExcluded("src/App/Program.cs"));
        harness.Check("does not exclude a file whose name merely contains an excluded word",
            !matcher.IsExcluded("src/App/Binder.cs"));

        var single = new GlobMatcher(new[] { "src/*.cs" });
        harness.Check("a single star stays within one path segment", single.IsExcluded("src/A.cs"));
        harness.Check("a single star does not cross a separator", !single.IsExcluded("src/Nested/A.cs"));
    }
}
