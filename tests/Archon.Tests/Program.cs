using Archon.Core.Configuration;
using Archon.Core.Engine;
using Archon.Core.Explanations;
using Archon.Core.Findings;
using Archon.Core.Insights;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Archon.Core.Sql;
using Archon.Rules;
using Archon.Rules.CSharp;
using Archon.Rules.Sql;
using Archon.Tests.Corpus;
using ServiceConventionRules;

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
        SqlFormatterRules(harness);
        SecurityHotspotRules(harness);
        ComplexityRules(harness);
        UnusedSymbolsRules(harness);
        LogicHygieneRules(harness);
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
        SnippetExtractionRules(harness);
        SnippetCorpusRules(harness);
        ServiceConventionRules(harness);
        ConventionPackTier2Rules(harness);
        SnippetCatalogRules(harness);
        ConfigValidationRules(harness);
        ConfigSchemaRules(harness);
        HotspotRankingRules(harness);
        GitHistoryRules(harness);
        DebtRankingRules(harness);
        GitHistoryChurnSinceRules(harness);

        return harness.Report();
    }

    internal static void SqlWildcardRules(Harness harness)
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

    internal static void SqlConventionRules(Harness harness)
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

    /// <summary>
    /// Ported from Arch.Sql.Format's own test suite (the formatter's origin, before it was carried
    /// into Archon): idempotency, byte-identical passthrough for SQL it cannot parse, and comment /
    /// 'GO' separator retention. The one thing not ported is that source suite's semantic
    /// round-trip test, which depends on an Arch-specific object analyzer Archon does not have.
    /// </summary>
    internal static void SqlFormatterRules(Harness harness)
    {
        harness.Group("SQL formatter");

        string schema = FormatFixture("tsql_schema.sql");
        string once = TSqlFormatter.Format(schema);
        harness.Equal("formatting is idempotent", once, TSqlFormatter.Format(once));

        string broken = FormatFixture("tsql_broken.sql");
        harness.Equal("an unparseable file is returned byte-identical", broken, TSqlFormatter.Format(broken));

        string commented = FormatFixture("commented.sql");
        string formattedCommented = TSqlFormatter.Format(commented);
        harness.Check("retains the file header comment",
            formattedCommented.Contains("File header: this script creates the Widgets table"));
        harness.Check("retains a per-object doc comment",
            formattedCommented.Contains("Widgets: the core catalog table."));
        harness.Check("retains a doc comment ahead of a later object",
            formattedCommented.Contains("usp_GetWidget: fetch a single widget by id."));
        harness.Check("keeps the GO batch separator", formattedCommented.Contains("GO"));
        harness.Equal("comment preservation is itself idempotent",
            formattedCommented, TSqlFormatter.Format(formattedCommented));

        harness.Check("flags a comment sitting mid-expression, which cannot be preserved",
            TSqlFormatter.HasInlineComments(commented));
        harness.Check("does not flag comments that only sit between statements",
            !TSqlFormatter.HasInlineComments("-- header\nCREATE TABLE dbo.T (Id INT);\nGO\n-- footer\n"));

        // A comment between statements inside a procedure body reads, to whoever wrote it, exactly
        // like one between top-level statements — it should survive the same way.
        const string procedureBody = """
            CREATE PROCEDURE dbo.usp_DoWork
            AS
            BEGIN
                -- Step 1: validate input.
                DECLARE @x INT = 1;
                -- Step 2: do the work.
                UPDATE dbo.T1 SET Name = 'x' WHERE Id = @x;
            END
            """;
        string formattedProcedure = TSqlFormatter.Format(procedureBody);
        harness.Check("retains a comment between two statements inside a procedure body",
            formattedProcedure.Contains("Step 1: validate input."));
        harness.Check("retains a comment between the last inner statement and END",
            formattedProcedure.Contains("Step 2: do the work."));
        harness.Check("does not flag a procedure body's between-statement comments as unpreservable",
            !TSqlFormatter.HasInlineComments(procedureBody));
        harness.Equal("procedure-body comment preservation is idempotent",
            formattedProcedure, TSqlFormatter.Format(formattedProcedure));

        // The same guarantee has to hold when a BEGIN...END sits nested inside another one (an IF
        // written with braces, inside a procedure body), since that is exactly where a fix scoped
        // only to the outermost body would stop working.
        const string nestedBlocks = """
            CREATE PROCEDURE dbo.usp_Nested
            AS
            BEGIN
                -- outer step
                DECLARE @x INT = 1;
                IF @x > 0
                BEGIN
                    -- inner step
                    SET @x = @x + 1;
                END
            END
            """;
        string formattedNested = TSqlFormatter.Format(nestedBlocks);
        harness.Check("retains a comment in the outer body of two nested BEGIN...END blocks",
            formattedNested.Contains("outer step"));
        harness.Check("retains a comment in the inner body of two nested BEGIN...END blocks",
            formattedNested.Contains("inner step"));
        harness.Equal("nested-block comment preservation is idempotent",
            formattedNested, TSqlFormatter.Format(formattedNested));

        // A comment inside an empty BEGIN...END (no real statements at all) is still between
        // something, even though there is nothing either side of it to anchor the gap to.
        const string commentOnlyBlock = "CREATE PROCEDURE dbo.usp_Stub AS BEGIN -- not implemented yet\nEND";
        harness.Check("retains a comment that is the only thing inside a BEGIN...END",
            TSqlFormatter.Format(commentOnlyBlock).Contains("not implemented yet"));

        // Regression: each preserved body is marked internally with a numbered placeholder while
        // formatting is in progress. Marker '1' is a literal string-prefix of marker '10', '11', ...
        // so a nested block (which is assigned a low marker number, since numbering happens
        // innermost-first) followed by ten or more unrelated sibling procedures used to risk the
        // low marker's splice matching a high marker's placeholder line instead of its own.
        string manySiblings = """
            CREATE PROCEDURE dbo.usp_Nested
            AS
            BEGIN
                -- nested-outer comment
                IF 1 = 1
                BEGIN
                    -- nested-inner comment
                    SELECT 0;
                END
            END
            GO

            """ + string.Concat(Enumerable.Range(1, 15).Select(i => $"""
                CREATE PROCEDURE dbo.usp_Sib{i}
                AS
                BEGIN
                    -- sibling comment {i}
                    SELECT {i};
                END
                GO

                """));
        string formattedManySiblings = TSqlFormatter.Format(manySiblings);
        harness.Check("leaves no unresolved placeholder marker in the output",
            !formattedManySiblings.Contains("ARCHON_FMT_BLOCK"));
        harness.Check("retains a nested block's comment alongside fifteen unrelated siblings",
            formattedManySiblings.Contains("nested-outer comment") && formattedManySiblings.Contains("nested-inner comment"));
        for (var i = 1; i <= 15; i++)
        {
            harness.Check($"retains sibling {i}'s own comment, not a neighbour's",
                formattedManySiblings.Contains($"-- sibling comment {i}\n    SELECT {i}"));
        }
    }

    /// <summary>Walks up from the test binary to the repository root (marked by Archon.slnx), the
    /// same way <see cref="Archon.Tests.Corpus.SnippetCorpusLocator"/> finds the vendored snippet
    /// library, rather than copying fixtures into the output directory.</summary>
    private static string FormatFixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Archon.slnx")))
            {
                return File.ReadAllText(Path.Combine(directory.FullName, "tests", "Archon.Tests", "Corpus", "Format", name));
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("could not find the repository root (Archon.slnx) above the test binary.");
    }

    internal static void SecurityHotspotRules(Harness harness)
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

    internal static void ComplexityRules(Harness harness)
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

        harness.Group("Complexity: whole-file score for hotspot ranking");

        var cache = new SourceCache();
        string emptyPath = Path.Combine(Path.GetTempPath(), "archon-tests", "empty.cs");
        cache.SetText(emptyPath, "class C { void M() { } }");
        harness.Equal("a method with no branching scores zero", 0, ComplexityRule.ScoreFile(cache.GetCSharp(emptyPath)!));

        string branchyPath = Path.Combine(Path.GetTempPath(), "archon-tests", "branchy.cs");
        cache.SetText(branchyPath, "class C { void M(int x) { if (x > 0) { } } void N(int x) { if (x > 0) { } } }");
        harness.Equal("two methods each scoring one sum to two, unfiltered by any threshold", 2, ComplexityRule.ScoreFile(cache.GetCSharp(branchyPath)!));
    }

    internal static void UnusedSymbolsRules(Harness harness)
    {
        harness.Group("Unused parameters and locals");

        var unusedParam = new TestWorkspace();
        unusedParam.Add("a.cs", "class C { void M(int x) { } }");
        harness.Equal("flags an unused parameter", 1,
            unusedParam.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedParameter));

        var usedParam = new TestWorkspace();
        usedParam.Add("a.cs", "class C { void M(int x) { System.Console.WriteLine(x); } }");
        harness.Equal("does not flag a parameter that is read", 0,
            usedParam.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedParameter));

        var underscoreParam = new TestWorkspace();
        underscoreParam.Add("a.cs", "class C { void M(int _reserved) { } }");
        harness.Equal("does not flag a parameter prefixed with an underscore", 0,
            underscoreParam.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedParameter));

        // The rule reads only the 'override' modifier on the method being checked, not whether a
        // matching base member actually exists -- deliberate, same syntax-only stance as the rest
        // of this rule pack (see UnusedSymbolsRule's class doc comment).
        var overrideParam = new TestWorkspace();
        overrideParam.Add("a.cs", "class C : Base { public override void M(int x) { } } class Base { public virtual void M(int x) { } }");
        harness.Equal("does not flag an unused parameter on an override", 0,
            overrideParam.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedParameter));

        var explicitInterfaceParam = new TestWorkspace();
        explicitInterfaceParam.Add("a.cs", "interface IFoo { void M(int x); } class C : IFoo { void IFoo.M(int x) { } }");
        harness.Equal("does not flag an unused parameter on an explicit interface implementation", 0,
            explicitInterfaceParam.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedParameter));

        var eventHandlerParam = new TestWorkspace();
        eventHandlerParam.Add("a.cs", "class C { void OnClick(object sender, System.EventArgs e) { } }");
        harness.Equal("does not flag an event-handler-shaped method", 0,
            eventHandlerParam.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedParameter));

        var expressionBodied = new TestWorkspace();
        expressionBodied.Add("a.cs", "class C { int M(int x, int y) => x + 1; }");
        harness.Equal("flags an unused parameter on an expression-bodied method", 1,
            expressionBodied.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedParameter));

        var unusedLocal = new TestWorkspace();
        unusedLocal.Add("a.cs", "class C { void M() { int total = 0; } }");
        harness.Equal("flags an unused local", 1,
            unusedLocal.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedLocalVariable));

        var usedLocal = new TestWorkspace();
        usedLocal.Add("a.cs", "class C { void M() { int total = 0; System.Console.WriteLine(total); } }");
        harness.Equal("does not flag a local that is read later", 0,
            usedLocal.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedLocalVariable));

        var underscoreLocal = new TestWorkspace();
        underscoreLocal.Add("a.cs", "class C { void M() { int _ignored = Compute(); } int Compute() => 1; }");
        harness.Equal("does not flag a local prefixed with an underscore", 0,
            underscoreLocal.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedLocalVariable));

        var usingLocal = new TestWorkspace();
        usingLocal.Add("a.cs", "class C { void M() { using var f = System.IO.File.OpenRead(\"x\"); } }");
        harness.Equal("does not flag a 'using' local, which exists for disposal even if unread", 0,
            usingLocal.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedLocalVariable));

        var constLocal = new TestWorkspace();
        constLocal.Add("a.cs", "class C { void M() { const int Max = 10; } }");
        harness.Equal("does not flag a const local", 0,
            constLocal.Analyse().Findings.CountOf(UnusedSymbolsRule.UnusedLocalVariable));
    }

    internal static void LogicHygieneRules(Harness harness)
    {
        harness.Group("Logic hygiene");

        var ifTrue = new TestWorkspace();
        ifTrue.Add("a.cs", "class C { void M() { if (true) { } } }");
        harness.Equal("flags 'if (true)'", 1,
            ifTrue.Analyse().Findings.CountOf(LogicHygieneRule.AlwaysTrueOrFalseCondition));

        var ifFalse = new TestWorkspace();
        ifFalse.Add("a.cs", "class C { void M() { if (false) { } } }");
        harness.Equal("flags 'if (false)'", 1,
            ifFalse.Analyse().Findings.CountOf(LogicHygieneRule.AlwaysTrueOrFalseCondition));

        var ternaryLiteral = new TestWorkspace();
        ternaryLiteral.Add("a.cs", "class C { int M() => true ? 1 : 2; }");
        harness.Equal("flags a ternary with a literal condition", 1,
            ternaryLiteral.Analyse().Findings.CountOf(LogicHygieneRule.AlwaysTrueOrFalseCondition));

        var selfComparison = new TestWorkspace();
        selfComparison.Add("a.cs", "class C { void M(int x) { if (x == x) { } } }");
        harness.Equal("flags a self-comparison", 1,
            selfComparison.Analyse().Findings.CountOf(LogicHygieneRule.AlwaysTrueOrFalseCondition));

        var genuineComparison = new TestWorkspace();
        genuineComparison.Add("a.cs", "class C { void M(int x, int y) { if (x == y) { } } }");
        harness.Equal("does not flag comparing two different identifiers", 0,
            genuineComparison.Analyse().Findings.CountOf(LogicHygieneRule.AlwaysTrueOrFalseCondition));

        var infiniteLoop = new TestWorkspace();
        infiniteLoop.Add("a.cs", "class C { void M() { while (true) { break; } } }");
        harness.Equal("does not flag 'while (true)', an established idiom", 0,
            infiniteLoop.Analyse().Findings.CountOf(LogicHygieneRule.AlwaysTrueOrFalseCondition));

        var doWhileFalse = new TestWorkspace();
        doWhileFalse.Add("a.cs", "class C { void M() { do { break; } while (false); } }");
        harness.Equal("does not flag 'do { } while (false)', an established idiom", 0,
            doWhileFalse.Analyse().Findings.CountOf(LogicHygieneRule.AlwaysTrueOrFalseCondition));

        var writeLine = new TestWorkspace();
        writeLine.Add("a.cs", "class C { void M() { System.Console.WriteLine(\"hi\"); } }");
        harness.Equal("flags Console.WriteLine", 1,
            writeLine.Analyse().Findings.CountOf(LogicHygieneRule.ConsoleUsedForOutput));

        var consoleError = new TestWorkspace();
        consoleError.Add("a.cs", "class C { void M() { System.Console.Error.WriteLine(\"hi\"); } }");
        harness.Equal("flags Console.Error.WriteLine", 1,
            consoleError.Analyse().Findings.CountOf(LogicHygieneRule.ConsoleUsedForOutput));

        var loggerCall = new TestWorkspace();
        loggerCall.Add("a.cs", "class C { void M(ILogger log) { log.WriteLine(\"hi\"); } }");
        harness.Equal("does not flag a same-named method on an unrelated receiver", 0,
            loggerCall.Analyse().Findings.CountOf(LogicHygieneRule.ConsoleUsedForOutput));
    }

    internal static void AsyncSafetyRules(Harness harness)
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

    internal static void PerfHintRules(Harness harness)
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

    internal static void ConfigKeyRules(Harness harness)
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

    internal static void ProjectCycleRules(Harness harness)
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

    internal static void CallGraphChecks(Harness harness)
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
    internal static void CallGraphMemberChecks(Harness harness)
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
    internal static void BaselineStabilityRules(Harness harness)
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
    internal static void SourceCacheRules(Harness harness)
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

        {
            var raceCache = new SourceCache();
            string path = Path.Combine(Path.GetTempPath(), "archon-race-test.cs");
            raceCache.SetText(path, "class Race { void M() { } }");

            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var results = new ParsedCSharp?[50];
            var tasks = new Task[50];
            for (int i = 0; i < tasks.Length; i++)
            {
                int index = i;
                tasks[index] = Task.Run(() =>
                {
                    try
                    {
                        results[index] = raceCache.GetCSharp(path);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }
            Task.WaitAll(tasks);

            harness.Check("50 concurrent GetCSharp calls throw nothing", exceptions.IsEmpty);
            ParsedCSharp? final = raceCache.GetCSharp(path);
            harness.Check("every concurrent result is reference-equal to the cached parse",
                results.All(r => ReferenceEquals(r, final)));
        }
        {
            var raceCache = new SourceCache();
            string path = Path.Combine(Path.GetTempPath(), "archon-race-test.sql");
            raceCache.SetText(path, "SELECT 1;");

            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var results = new ParsedSql?[50];
            var tasks = new Task[50];
            for (int i = 0; i < tasks.Length; i++)
            {
                int index = i;
                tasks[index] = Task.Run(() =>
                {
                    try
                    {
                        results[index] = raceCache.GetSql(path);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }
            Task.WaitAll(tasks);

            harness.Check("50 concurrent GetSql calls throw nothing", exceptions.IsEmpty);
            ParsedSql? final = raceCache.GetSql(path);
            harness.Check("every concurrent result is reference-equal to the cached parse",
                results.All(r => ReferenceEquals(r, final)));
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Files are attributed to every project above them, so a project nested inside another is
    /// owned by both. This held when each project scanned the whole file list for itself, and must
    /// still hold now that files are walked upwards instead.
    /// </summary>
    internal static void ProjectAttributionRules(Harness harness)
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

    internal static void LayerRules(Harness harness)
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

    internal static void LifetimeRules(Harness harness)
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

    internal static void SuppressionRules(Harness harness)
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

    internal static void BaselineRules(Harness harness)
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

    internal static void ConfigurationRules(Harness harness)
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

    internal static void ScopeRules(Harness harness)
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

    internal static void RegistryRules(Harness harness)
    {
        harness.Group("Rule registry");

        var registry = new RuleRegistry();
        registry.Add(new BuiltInRulePack());

        harness.Equal("every built-in condition is registered", 32, registry.Descriptors.Count);
        harness.Equal("rules that report several conditions are counted once as rules", 12, registry.Rules.Count);
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

    internal static void GlobRules(Harness harness)
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

    /// <summary>
    /// The extractor and wrapper are pure, so they are asserted here against short inline markdown
    /// and code strings, exactly as every other rule is asserted against inline source. The last
    /// three assertions are the regression that protects Phase 2's central claim: a bare method at
    /// file scope is invisible to the method-shaped rules unless it is wrapped first.
    /// </summary>
    internal static void SnippetExtractionRules(Harness harness)
    {
        harness.Group("Snippet extraction and wrapping");

        IReadOnlyList<SnippetBlock> basic = SnippetExtractor.Extract(
            "### PUB-X-01 · A title\n```csharp\nvar x = 1;\n```\n", "test.md");
        harness.Check("extracts a snippet id and title",
            basic.Count == 1 && basic[0].SnippetId == "PUB-X-01" && basic[0].Title == "A title");

        IReadOnlyList<SnippetBlock> twoBlocks = SnippetExtractor.Extract(
            "### PUB-X-01 · A title\n```csharp\nvar x = 1;\n```\n```csharp\nvar y = 2;\n```\n", "test.md");
        harness.Check("numbers a snippet's second block",
            twoBlocks.Count == 2 && twoBlocks[0].Ordinal == 0 && twoBlocks[1].Ordinal == 1);

        IReadOnlyList<SnippetBlock> xmlBlock = SnippetExtractor.Extract(
            "### PUB-X-01 · A title\n```xml\n<a/>\n```\n", "test.md");
        harness.Check("records a non-C# language rather than dropping it",
            xmlBlock.Count == 1 && xmlBlock[0].Language == "xml");

        IReadOnlyList<SnippetBlock> unlabelled = SnippetExtractor.Extract(
            "### PUB-X-01 · A title\n```\nplain text\n```\n", "test.md");
        harness.Check("ignores an unlabelled fence's language",
            unlabelled.Count == 1 && unlabelled[0].Language == "");

        IReadOnlyList<SnippetBlock> notHeading = SnippetExtractor.Extract("### Adapting these snippets\n", "test.md");
        harness.Equal("ignores a '###' line that is not a snippet heading", 0, notHeading.Count);

        WrappedSnippet unit = SnippetWrapper.Wrap("public sealed class C { }");
        harness.Check("classifies a type declaration as Unit",
            unit.Shape == SnippetShape.Unit && unit.PrefixLines == 0);

        WrappedSnippet member = SnippetWrapper.Wrap("public static void M(this int x) { }");
        harness.Equal("classifies a bare extension method as Member", SnippetShape.Member, member.Shape);

        WrappedSnippet statements = SnippetWrapper.Wrap("var x = 1;");
        harness.Equal("classifies bare statements as Statements", SnippetShape.Statements, statements.Shape);

        WrappedSnippet hoisted = SnippetWrapper.Wrap("using System;\npublic static void M() { }");
        harness.Check("hoists leading usings above the wrapper",
            hoisted.Shape == SnippetShape.Member && hoisted.Text.StartsWith("using System;", StringComparison.Ordinal));

        WrappedSnippet usingStatement = SnippetWrapper.Wrap("using var r = new StringReader(s);");
        harness.Check("does not hoist a using-statement",
            usingStatement.Shape == SnippetShape.Statements && usingStatement.Text.Contains("using var r"));

        const string asyncVoidBody = "public static async void M(string a) { await Task.Delay(1); }";
        WrappedSnippet wrapped = SnippetWrapper.Wrap(asyncVoidBody);
        var wrappedWorkspace = new TestWorkspace();
        wrappedWorkspace.Add("wrapped.cs", wrapped.Text);
        harness.Equal("wrapping makes a method visible to the method-shaped rules", 1,
            wrappedWorkspace.Analyse().Findings.CountOf(AsyncSafetyRule.AsyncVoid));

        var unwrappedWorkspace = new TestWorkspace();
        unwrappedWorkspace.Add("unwrapped.cs", asyncVoidBody);
        harness.Equal("the same text unwrapped is invisible", 0,
            unwrappedWorkspace.Analyse().Findings.CountOf(AsyncSafetyRule.AsyncVoid));

        harness.Check("nothing wraps to a syntax error",
            unit.Shape != SnippetShape.Unparseable &&
            member.Shape != SnippetShape.Unparseable &&
            statements.Shape != SnippetShape.Unparseable &&
            wrapped.Shape != SnippetShape.Unparseable);
    }

    /// <summary>
    /// The only IO in the whole suite. Every vendored file is extracted, every C# block is
    /// wrapped and added to one workspace, and the workspace is analysed once — so workspace-scope
    /// rules (AR0002-AR0005, AR0040) see the corpus as one codebase, which is what makes the
    /// registration snippets in 01-bootstrap-and-di.md interesting. AR0030/AR0031 (project scope)
    /// and AR0040/AR0041 need .csproj files the corpus has none of, so they are silent by
    /// construction rather than by agreement. The workspace also carries service.conventions
    /// (Phase 3/4), so a rule change there that starts firing on idiomatic library code fails this
    /// suite exactly as a built-in rule change would.
    /// </summary>
    internal static void SnippetCorpusRules(Harness harness)
    {
        harness.Group("Snippet library corpus");

        string? root = SnippetCorpusLocator.Locate();
        if (root is null)
        {
            harness.Check("the snippet corpus is present at tests/fixtures/library", false);
            return;
        }

        string[] files = Directory.GetFiles(root, "*.md");
        harness.Equal("library files found", 11, files.Length);

        var allBlocks = new List<SnippetBlock>();
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            allBlocks.AddRange(SnippetExtractor.Extract(File.ReadAllText(file), Path.GetFileName(file)));
        }

        harness.Equal("snippets extracted", 82, allBlocks.Select(b => b.SnippetId).Distinct().Count());

        List<SnippetBlock> csharpBlocks = allBlocks.Where(b => b.Language == "csharp").ToList();
        harness.Equal("C# blocks extracted", 83, csharpBlocks.Count);
        harness.Equal("non-C# blocks skipped", 3, allBlocks.Count - csharpBlocks.Count);

        var workspace = new TestWorkspace(new BuiltInRulePack(), new ServiceConventionRulePack());
        var unparseable = new List<string>();
        var statementShaped = new HashSet<string>(StringComparer.Ordinal);

        foreach (SnippetBlock block in csharpBlocks)
        {
            WrappedSnippet wrapped = SnippetWrapper.Wrap(block.Text);
            string key = $"{block.SnippetId}-{block.Ordinal}";
            if (wrapped.Shape == SnippetShape.Unparseable)
            {
                unparseable.Add(key);
                continue;
            }
            if (wrapped.Shape == SnippetShape.Statements)
            {
                statementShaped.Add(block.SnippetId);
            }
            workspace.Add($"{key}.cs", wrapped.Text);
        }

        string actualUnparseable = string.Join(",", unparseable.OrderBy(s => s, StringComparer.Ordinal));
        string expectedUnparseable = string.Join(",", ExpectedCorpusFindings.Unparseable.OrderBy(s => s, StringComparer.Ordinal));
        harness.Equal("blocks that no shape could parse", expectedUnparseable, actualUnparseable);

        string actualStatementShaped = string.Join(",", statementShaped.OrderBy(s => s, StringComparer.Ordinal));
        string expectedStatementShaped = string.Join(",", ExpectedCorpusFindings.StatementShaped.OrderBy(s => s, StringComparer.Ordinal));
        harness.Equal("statement-shaped snippet ids", expectedStatementShaped, actualStatementShaped);

        AnalysisResult result = workspace.Analyse();

        var actualByBlock = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (Finding finding in result.Findings)
        {
            string key = Path.GetFileNameWithoutExtension(finding.FilePath);
            if (!actualByBlock.TryGetValue(key, out Dictionary<string, int>? perRule))
            {
                perRule = new Dictionary<string, int>(StringComparer.Ordinal);
                actualByBlock[key] = perRule;
            }
            perRule[finding.RuleId] = perRule.GetValueOrDefault(finding.RuleId) + 1;
        }

        var mismatches = new List<string>();
        foreach (string key in actualByBlock.Keys.Union(ExpectedCorpusFindings.ByBlock.Keys))
        {
            Dictionary<string, int> actualForBlock = actualByBlock.GetValueOrDefault(key, new Dictionary<string, int>());
            IReadOnlyDictionary<string, int> expectedForBlock = ExpectedCorpusFindings.ByBlock.GetValueOrDefault(
                key, new Dictionary<string, int>());
            foreach (string ruleId in actualForBlock.Keys.Union(expectedForBlock.Keys))
            {
                int actualCount = actualForBlock.GetValueOrDefault(ruleId);
                int expectedCount = expectedForBlock.GetValueOrDefault(ruleId);
                if (actualCount != expectedCount)
                {
                    mismatches.Add($"{key}:{ruleId} expected {expectedCount} actual {actualCount}");
                }
            }
        }
        harness.Check("per-block findings match the expected table" +
            (mismatches.Count == 0 ? "" : $" ({string.Join("; ", mismatches)})"), mismatches.Count == 0);

        int expectedTotal = ExpectedCorpusFindings.ByBlock.Values.Sum(d => d.Values.Sum());
        harness.Equal("total findings across the corpus", expectedTotal, result.Findings.Count);
    }

    /// <summary>
    /// The convention pack composes with the engine's own machinery — suppression, severity
    /// overrides — rather than re-implementing either, which is what the last three assertions
    /// prove.
    /// </summary>
    internal static void ServiceConventionRules(Harness harness)
    {
        harness.Group("Service convention rules");

        var now = new TestWorkspace(new ServiceConventionRulePack());
        now.Add("a.cs", "class C { void M() { var x = DateTime.Now; } }");
        harness.Equal("flags DateTime.Now", 1, now.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock));

        var offsetNow = new TestWorkspace(new ServiceConventionRulePack());
        offsetNow.Add("a.cs", "class C { void M() { var x = DateTimeOffset.Now; } }");
        harness.Equal("flags DateTimeOffset.Now", 1, offsetNow.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock));

        var today = new TestWorkspace(new ServiceConventionRulePack());
        today.Add("a.cs", "class C { void M() { var x = DateTime.Today; } }");
        harness.Equal("flags DateTime.Today", 1, today.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock));

        var qualified = new TestWorkspace(new ServiceConventionRulePack());
        qualified.Add("a.cs", "class C { void M() { var x = System.DateTime.Now; } }");
        harness.Equal("flags a fully-qualified System.DateTime.Now", 1,
            qualified.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock));

        var utc = new TestWorkspace(new ServiceConventionRulePack());
        utc.Add("a.cs", "class C { void M() { var a = DateTime.UtcNow; var b = DateTimeOffset.UtcNow; } }");
        harness.Equal("ignores DateTime.UtcNow and DateTimeOffset.UtcNow", 0,
            utc.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock));

        var offsetToday = new TestWorkspace(new ServiceConventionRulePack());
        offsetToday.Add("a.cs", "class C { void M() { var x = DateTimeOffset.Today; } }");
        harness.Equal("ignores DateTimeOffset.Today, which does not exist", 0,
            offsetToday.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock));

        var instanceNow = new TestWorkspace(new ServiceConventionRulePack());
        instanceNow.Add("a.cs", "class C { Clock clock; void M() { var x = clock.Now; } }");
        harness.Equal("ignores an instance '.Now' on a field", 0,
            instanceNow.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock));

        var culture = new TestWorkspace(new ServiceConventionRulePack());
        culture.Add("a.cs", "class C { void M() { var a = CultureInfo.CurrentCulture; var b = CultureInfo.CurrentUICulture; } }");
        harness.Equal("flags CultureInfo.CurrentCulture and CurrentUICulture", 2,
            culture.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientCulture));

        var invariant = new TestWorkspace(new ServiceConventionRulePack());
        invariant.Add("a.cs", "class C { void M() { var x = CultureInfo.InvariantCulture; } }");
        harness.Equal("ignores CultureInfo.InvariantCulture", 0,
            invariant.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientCulture));

        var cwd = new TestWorkspace(new ServiceConventionRulePack());
        cwd.Add("a.cs", "class C { void M() { var x = Directory.GetCurrentDirectory(); } }");
        harness.Equal("flags Directory.GetCurrentDirectory()", 1,
            cwd.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientWorkingDirectory));

        // A marker suppresses its own line and the line below it (SuppressionIndex, exercised in
        // "Suppression markers"), so the second finding sits two lines below the marker to prove
        // only the marked occurrence is silenced.
        const string suppressionSubject =
            "class C { void M() { var a = DateTime.Now;\nvar spacer = 1;\nvar b = DateTime.Today; } }";

        var suppressionBaseline = new TestWorkspace(new ServiceConventionRulePack());
        suppressionBaseline.Add("a.cs", suppressionSubject);
        int baselineCount = suppressionBaseline.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock);

        var suppressed = new TestWorkspace(new ServiceConventionRulePack());
        suppressed.Add("a.cs", suppressionSubject.Replace(
            "var a = DateTime.Now;", "var a = DateTime.Now; // archon-ignore[SVC0001]"));
        int suppressedCount = suppressed.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientClock);
        harness.Equal("honours a suppression marker on SVC0001", baselineCount - 1, suppressedCount);

        var off = new TestWorkspace(new ServiceConventionRulePack()).WithSeverity(AmbientEnvironmentRule.AmbientCulture, "off");
        off.Add("a.cs", "class C { void M() { var x = CultureInfo.CurrentCulture; } }");
        harness.Equal("honours 'SVC0002': 'off'", 0, off.Analyse().Findings.CountOf(AmbientEnvironmentRule.AmbientCulture));

        var everything = new TestWorkspace(new ServiceConventionRulePack());
        everything.Add("a.cs", """
            class C
            {
                void M()
                {
                    var a = DateTime.Now;
                    var b = DateTimeOffset.Now;
                    var c = DateTime.Today;
                    var d = System.DateTime.Now;
                    var e = CultureInfo.CurrentCulture;
                    var f = CultureInfo.CurrentUICulture;
                    var g = Directory.GetCurrentDirectory();
                }
            }
            """);
        harness.Check("every reported id is declared",
            everything.Analyse().Findings.All(f =>
                f.RuleId is AmbientEnvironmentRule.AmbientClock or AmbientEnvironmentRule.AmbientCulture
                    or AmbientEnvironmentRule.AmbientWorkingDirectory));
    }

    /// <summary>
    /// SVC0021 (missing CancellationToken) was measured against the vendored corpus and
    /// abandoned rather than shipped — see the phase report and the class doc comment on
    /// AsyncContractRule for why — so only SVC0010 and SVC0020 have assertions here.
    /// </summary>
    internal static void ConventionPackTier2Rules(Harness harness)
    {
        harness.Group("Convention pack tier 2");

        var httpUrl = new TestWorkspace(new ServiceConventionRulePack());
        httpUrl.Add("a.cs", "class C { string url = \"http://example.com/api\"; }");
        harness.Equal("flags a hardcoded http:// URL in an initialiser", 1,
            httpUrl.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var httpsUrl = new TestWorkspace(new ServiceConventionRulePack());
        httpsUrl.Add("a.cs", "class C { string url; void M() { url = \"https://example.com/api\"; } }");
        harness.Equal("flags a hardcoded https:// URL in an assignment", 1,
            httpsUrl.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var uncPath = new TestWorkspace(new ServiceConventionRulePack());
        uncPath.Add("a.cs", """class C { string share = "\\\\fileserver\\share"; } """);
        harness.Equal("flags a UNC path", 1, uncPath.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var ipv4 = new TestWorkspace(new ServiceConventionRulePack());
        ipv4.Add("a.cs", "class C { string endpoint = \"10.0.0.5\"; }");
        harness.Equal("flags an IPv4 literal", 1, ipv4.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var specHost = new TestWorkspace(new ServiceConventionRulePack());
        specHost.Add("a.cs", "class C { string url = \"http://tempuri.org/service\"; }");
        harness.Equal("ignores a well-known specification host", 0,
            specHost.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var loopback = new TestWorkspace(new ServiceConventionRulePack());
        loopback.Add("a.cs", "class C { string url = \"http://localhost:5000/api\"; }");
        harness.Equal("ignores localhost", 0, loopback.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var patternName = new TestWorkspace(new ServiceConventionRulePack());
        patternName.Add("a.cs", "class C { string UrlFormat = \"http://example.com/{0}\"; }");
        harness.Equal("ignores a literal whose name ends in 'Format'", 0,
            patternName.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var ordinary = new TestWorkspace(new ServiceConventionRulePack());
        ordinary.Add("a.cs", "class C { string title = \"hello world\"; }");
        harness.Equal("ignores a literal that is not a URL, UNC path or IPv4 address", 0,
            ordinary.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var additionalHost = new TestWorkspace(new ServiceConventionRulePack())
            .WithOption(HardcodedEndpointRule.Id, """{ "additionalAllowedHosts": ["internal.example.com"] }""");
        additionalHost.Add("a.cs", "class C { string url = \"https://internal.example.com/api\"; }");
        harness.Equal("honours an additionalAllowedHosts option", 0,
            additionalHost.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var malformedOption = new TestWorkspace(new ServiceConventionRulePack())
            .WithOption(HardcodedEndpointRule.Id, """{ "additionalAllowedHosts": "not-an-array" }""");
        malformedOption.Add("a.cs", "class C { string url = \"https://example.com/api\"; }");
        harness.Equal("a malformed option leaves the defaults rather than throwing", 1,
            malformedOption.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var svc0010Off = new TestWorkspace(new ServiceConventionRulePack()).WithSeverity(HardcodedEndpointRule.Id, "off");
        svc0010Off.Add("a.cs", "class C { string url = \"https://example.com/api\"; }");
        harness.Equal("honours 'SVC0010': 'off'", 0, svc0010Off.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var svc0010Baseline = new TestWorkspace(new ServiceConventionRulePack());
        svc0010Baseline.Add("a.cs", "class C { string a = \"https://example.com\";\nstring b = \"https://example.org\"; }");
        int svc0010BaselineCount = svc0010Baseline.Analyse().Findings.CountOf(HardcodedEndpointRule.Id);

        var svc0010Suppressed = new TestWorkspace(new ServiceConventionRulePack());
        svc0010Suppressed.Add("a.cs",
            "class C { string a = \"https://example.com\"; // archon-ignore[SVC0010]\nstring spacer = \"x\";\nstring b = \"https://example.org\"; }");
        harness.Equal("honours a suppression marker on SVC0010", svc0010BaselineCount - 1,
            svc0010Suppressed.Analyse().Findings.CountOf(HardcodedEndpointRule.Id));

        var notAsyncNamed = new TestWorkspace(new ServiceConventionRulePack());
        notAsyncNamed.Add("a.cs", "class C { async System.Threading.Tasks.Task Go() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("flags a method whose body awaits but whose name does not end in 'Async'", 1,
            notAsyncNamed.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var alreadyAsync = new TestWorkspace(new ServiceConventionRulePack());
        alreadyAsync.Add("a.cs", "class C { async System.Threading.Tasks.Task GoAsync() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("ignores a method already named with the 'Async' suffix", 0,
            alreadyAsync.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var main = new TestWorkspace(new ServiceConventionRulePack());
        main.Add("a.cs", "class C { static async System.Threading.Tasks.Task Main() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("ignores 'Main'", 0, main.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var overrideMethod = new TestWorkspace(new ServiceConventionRulePack());
        overrideMethod.Add("a.cs",
            "class Base { public virtual async System.Threading.Tasks.Task Go() { } } " +
            "class C : Base { public override async System.Threading.Tasks.Task Go() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("ignores an override", 0, overrideMethod.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var interfaceMember = new TestWorkspace(new ServiceConventionRulePack());
        interfaceMember.Add("a.cs",
            "interface IHandler { async System.Threading.Tasks.Task Handle() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("ignores a member of an interface named starting with 'I'", 0,
            interfaceMember.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var eventHandler = new TestWorkspace(new ServiceConventionRulePack());
        eventHandler.Add("a.cs",
            "class C { async System.Threading.Tasks.Task OnClick(object sender, System.EventArgs e) { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("ignores an event-handler shape", 0, eventHandler.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var controllerAction = new TestWorkspace(new ServiceConventionRulePack());
        controllerAction.Add("a.cs",
            "class C { [HttpGet] public async System.Threading.Tasks.Task Get() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("ignores an ASP.NET action method decorated with '[HttpGet]'", 0,
            controllerAction.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var testMethod = new TestWorkspace(new ServiceConventionRulePack());
        testMethod.Add("a.cs",
            "class C { [Test] public async System.Threading.Tasks.Task Widget_WhenValid_Succeeds() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("ignores a method decorated with '[Test]'", 0,
            testMethod.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var svc0020Off = new TestWorkspace(new ServiceConventionRulePack()).WithSeverity(AsyncContractRule.MissingAsyncSuffix, "off");
        svc0020Off.Add("a.cs", "class C { async System.Threading.Tasks.Task Go() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Equal("honours 'SVC0020': 'off'", 0, svc0020Off.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var svc0020Suppressed = new TestWorkspace(new ServiceConventionRulePack());
        svc0020Suppressed.Add("a.cs",
            "class C { async System.Threading.Tasks.Task Go() { await System.Threading.Tasks.Task.Delay(1); } } // archon-ignore[SVC0020]");
        harness.Equal("honours a suppression marker on SVC0020", 0,
            svc0020Suppressed.Analyse().Findings.CountOf(AsyncContractRule.MissingAsyncSuffix));

        var everythingTier2 = new TestWorkspace(new ServiceConventionRulePack());
        everythingTier2.Add("a.cs", "class C { string url = \"http://example.com\"; async System.Threading.Tasks.Task Go() { await System.Threading.Tasks.Task.Delay(1); } }");
        harness.Check("every reported id is declared",
            everythingTier2.Analyse().Findings.All(f => f.RuleId is HardcodedEndpointRule.Id or AsyncContractRule.MissingAsyncSuffix));
    }

    /// <summary>
    /// Detection never consults this catalog (constraint 2); these assertions exist only to catch
    /// the static field initialisation order trap called out in Phase 5's GOTCHA and a typo in a
    /// mapped key.
    /// </summary>
    internal static void SnippetCatalogRules(Harness harness)
    {
        harness.Group("Snippet catalog");

        string[] builtInMappedIds =
        {
            "AR0002", "AR0003", "AR0004", "AR0010", "AR0011", "AR0012", "AR0013", "AR0023", "SQ0001", "AR0030", "AR0073"
        };
        string[] conventionMappedIds = { "SVC0001", "SVC0003" };
        string[] mappedIds = builtInMappedIds.Concat(conventionMappedIds).ToArray();

        harness.Check("every mapped id resolves to a non-null pointer with a non-empty snippet id and reason",
            mappedIds.All(id =>
            {
                SnippetPointer? pointer = SnippetCatalog.ForRule(id);
                return pointer is not null && pointer.SnippetId.Length > 0 && pointer.Why.Length > 0;
            }));

        harness.Check("an unmapped id returns null", SnippetCatalog.ForRule("AR0060") is null);

        harness.Check("resolves case-insensitively", SnippetCatalog.ForRule("ar0002") is not null);

        var builtIn = new RuleRegistry();
        builtIn.Add(new BuiltInRulePack());
        harness.Check("every mapped built-in id is a registered rule id in BuiltInRulePack",
            builtInMappedIds.All(id => builtIn.Find(id) is not null));

        var conventions = new RuleRegistry();
        conventions.Add(new ServiceConventionRulePack());
        harness.Check("every mapped SVC id is a registered rule id from Phase 3",
            conventionMappedIds.All(id => conventions.Find(id) is not null));
    }

    /// <summary>
    /// Severity resolution is deliberately total: anything it cannot read is treated as absent. The
    /// assertions below fix the other half of that bargain — that every entry resolution had to
    /// ignore is reported — and, just as importantly, that a correct file reports nothing. A
    /// validator that cried wolf on a working configuration would be turned off within a week.
    /// </summary>
    internal static void ConfigValidationRules(Harness harness)
    {
        harness.Group("Configuration validation");

        var registry = new RuleRegistry();
        registry.Add(new BuiltInRulePack());

        harness.Check("a configuration with nothing in it reports nothing",
            ConfigValidator.Validate(new ArchonConfig(), registry).Count == 0);

        var wellFormed = new ArchonConfig();
        wellFormed.Rules["AR0001"] = "error";
        wellFormed.Rules["performance"] = "information";
        wellFormed.Rules["AR0005"] = "off";
        wellFormed.Layers = new LayerConfig
        {
            Mode = "denylist",
            Layers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Domain"] = new() { "MyApp.Domain" },
                ["Infrastructure"] = new() { "MyApp.Infrastructure" }
            },
            Deny = new List<LayerEdge> { new() { Id = "pure", From = "Domain", To = "Infrastructure" } }
        };
        harness.Check("a correct configuration reports nothing",
            ConfigValidator.Validate(wellFormed, registry).Count == 0);

        var misspelledId = new ArchonConfig();
        misspelledId.Rules["AR010"] = "off";
        IReadOnlyList<string> idMessages = ConfigValidator.Validate(misspelledId, registry);
        harness.Equal("a misspelled rule id is reported once", 1, idMessages.Count);
        harness.Check("a misspelled rule id suggests the nearest real one",
            idMessages[0].Contains("AR0010", StringComparison.Ordinal));

        var misspelledSeverity = new ArchonConfig();
        misspelledSeverity.Rules["AR0010"] = "eror";
        IReadOnlyList<string> severityMessages = ConfigValidator.Validate(misspelledSeverity, registry);
        harness.Equal("an unparseable severity is reported once", 1, severityMessages.Count);
        harness.Check("an unparseable severity suggests the nearest real one",
            severityMessages[0].Contains("'error'", StringComparison.Ordinal));
        harness.Check("an unparseable severity states the default the rule keeps instead",
            severityMessages[0].Contains("keeps its default of warning", StringComparison.Ordinal));

        var aliases = new ArchonConfig();
        aliases.Rules["AR0010"] = "warn";
        aliases.Rules["AR0011"] = "info";
        aliases.Rules["AR0012"] = "none";
        harness.Check("the documented severity aliases are not reported as mistakes",
            ConfigValidator.Validate(aliases, registry).Count == 0);

        // A category is a legitimate key, so it must not be reported merely for not being an id.
        var category = new ArchonConfig();
        category.Rules["security"] = "error";
        harness.Check("a category name is accepted as a rules key",
            ConfigValidator.Validate(category, registry).Count == 0);

        var badOption = new ArchonConfig();
        badOption.Options["SQ0013"] = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();
        harness.Check("an options entry for an unknown rule id is reported",
            ConfigValidator.Validate(badOption, registry).Any(m => m.Contains("never read", StringComparison.Ordinal)));

        // The mode fallback is the permissive one, so a typo silently widens what is allowed.
        var badMode = new ArchonConfig
        {
            Layers = new LayerConfig
            {
                Mode = "allowlst",
                Layers = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["Domain"] = new() { "A" } }
            }
        };
        harness.Check("an unrecognised layer mode is reported as falling back to denylist",
            ConfigValidator.Validate(badMode, registry).Any(m => m.Contains("treated as denylist", StringComparison.Ordinal)));

        // Layer names are compared with ordinal equality, unlike rule ids.
        var wrongCase = new ArchonConfig
        {
            Layers = new LayerConfig
            {
                Mode = "denylist",
                Layers = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["Domain"] = new() { "A" } },
                Deny = new List<LayerEdge> { new() { Id = "e", From = "domain", To = "Domain" } }
            }
        };
        harness.Check("a layer name differing only in case is reported as case-sensitive",
            ConfigValidator.Validate(wrongCase, registry).Any(m => m.Contains("case-sensitive", StringComparison.Ordinal)));

        var ignoredList = new ArchonConfig
        {
            Layers = new LayerConfig
            {
                Mode = "allowlist",
                Layers = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["Domain"] = new() { "A" } },
                Deny = new List<LayerEdge> { new() { Id = "e", From = "Domain", To = "Domain" } }
            }
        };
        harness.Check("edges in the list the mode does not read are reported as never read",
            ConfigValidator.Validate(ignoredList, registry).Any(m => m.Contains("never read", StringComparison.Ordinal)));

        // An external pack's ids are only spellable once its pack is registered, so validating
        // against the built-in set alone would report every private rule as a mistake.
        var external = new ArchonConfig();
        external.Rules["SVC0001"] = "error";
        harness.Check("a rule id from an unloaded pack is reported against the built-in registry",
            ConfigValidator.Validate(external, registry).Count == 1);

        var withPack = new RuleRegistry();
        withPack.Add(new BuiltInRulePack());
        withPack.Add(new ServiceConventionRulePack());
        harness.Check("the same id is accepted once its pack is registered",
            ConfigValidator.Validate(external, withPack).Count == 0);

        harness.Check("an unknown top-level key is reported with a suggestion",
            ConfigValidator.Validate(UnknownKeyConfig("rule"), registry)
                .Any(m => m.Contains("'rules'", StringComparison.Ordinal)));
    }

    private static ArchonConfig UnknownKeyConfig(string key)
    {
        var config = new ArchonConfig();
        config.UnknownKeys.Add(key);
        return config;
    }

    /// <summary>
    /// The schema is what stops most bad entries being typed at all, so what matters is that it is
    /// generated from the registry rather than fixed, and that it stays valid JSON an editor will load.
    /// </summary>
    internal static void ConfigSchemaRules(Harness harness)
    {
        harness.Group("Configuration schema");

        var registry = new RuleRegistry();
        registry.Add(new BuiltInRulePack());
        string schema = ConfigSchema.Generate(registry);

        System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(schema);
        System.Text.Json.JsonElement root = document.RootElement;

        harness.Check("the schema is a JSON object", root.ValueKind == System.Text.Json.JsonValueKind.Object);

        System.Text.Json.JsonElement ruleProperties = root
            .GetProperty("properties").GetProperty("rules").GetProperty("properties");

        harness.Check("every registered rule id appears as a property",
            registry.Descriptors.All(r => ruleProperties.TryGetProperty(r.Descriptor.Id, out _)));

        harness.Check("every category appears as a property",
            registry.Descriptors.Select(r => r.Descriptor.Category).Distinct()
                .All(c => ruleProperties.TryGetProperty(c, out _)));

        // Descriptions next to a $ref are not required to be honoured, so the enum is written in
        // place; this asserts the property is self-contained rather than a reference.
        System.Text.Json.JsonElement one = ruleProperties.GetProperty("AR0001");
        harness.Check("a rule property carries its own severity enum rather than a $ref",
            one.TryGetProperty("enum", out _) && !one.TryGetProperty("$ref", out _));
        harness.Check("a rule property is described with its title",
            one.GetProperty("description").GetString()!.Contains("Layer dependency", StringComparison.Ordinal));

        harness.Check("the severity enum is exactly the documented vocabulary",
            one.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!)
                .SequenceEqual(ArchonConfig.SeverityNames));

        harness.Check("unknown rule ids stay permitted, for packs absent on this machine",
            root.GetProperty("properties").GetProperty("rules")
                .GetProperty("additionalProperties").ValueKind == System.Text.Json.JsonValueKind.Object);

        harness.Check("the top level is closed, so a misspelled setting is caught",
            root.GetProperty("additionalProperties").ValueKind == System.Text.Json.JsonValueKind.False);

        harness.Check("layer mode is constrained to the two it accepts",
            root.GetProperty("properties").GetProperty("layers").GetProperty("properties")
                .GetProperty("mode").GetProperty("enum").EnumerateArray()
                .Select(e => e.GetString()).SequenceEqual(new[] { "denylist", "allowlist" }));

        // The generated schema must describe the file the scaffold writes, or `archon init`
        // produces a pair that disagree from the moment they are created.
        harness.Check("every key the scaffold writes is a key the schema permits",
            new[] { "rules", "exclude", "rulePacks", "baseline" }
                .All(k => root.GetProperty("properties").TryGetProperty(k, out _)));

        harness.Check("the keys the loader binds are the keys the schema declares",
            ConfigSchema.KnownKeys.All(k => root.GetProperty("properties").TryGetProperty(k, out _)));

        var withPack = new RuleRegistry();
        withPack.Add(new BuiltInRulePack());
        withPack.Add(new ServiceConventionRulePack());
        harness.Check("a registered external pack contributes its ids to the schema",
            System.Text.Json.JsonDocument.Parse(ConfigSchema.Generate(withPack)).RootElement
                .GetProperty("properties").GetProperty("rules").GetProperty("properties")
                .TryGetProperty("SVC0001", out _));

        document.Dispose();
    }

    internal static void HotspotRankingRules(Harness harness)
    {
        harness.Group("Hotspot ranking: complexity x churn");

        var complexity = new Dictionary<string, int>
        {
            ["Hot.cs"] = 10,
            ["ComplexButStable.cs"] = 50,
            ["ChurnyButSimple.cs"] = 1,
            ["NeverChanged.cs"] = 30
        };
        var churn = new Dictionary<string, int>
        {
            ["Hot.cs"] = 8,
            ["ComplexButStable.cs"] = 1,
            ["ChurnyButSimple.cs"] = 20
            // NeverChanged.cs absent: no commits in the window.
        };

        IReadOnlyList<HotspotEntry> ranked = HotspotAnalyzer.Rank(complexity, churn, top: 10);
        harness.Equal("a file with no churn in the window is excluded even if complex", 3, ranked.Count);
        harness.Equal("score is complexity times churn", 80, ranked.First(e => e.File == "Hot.cs").Score);
        harness.Equal("the highest score sorts first", "Hot.cs", ranked[0].File);

        IReadOnlyList<HotspotEntry> topOne = HotspotAnalyzer.Rank(complexity, churn, top: 1);
        harness.Equal("'top' caps the result count", 1, topOne.Count);

        var zeroComplexity = new Dictionary<string, int> { ["Trivial.cs"] = 0 };
        var alwaysChurns = new Dictionary<string, int> { ["Trivial.cs"] = 100 };
        harness.Equal("zero complexity is excluded regardless of churn", 0,
            HotspotAnalyzer.Rank(zeroComplexity, alwaysChurns, top: 10).Count);
    }

    /// <summary>
    /// Exercises <see cref="GitHistory"/> against a real repository built in a temp directory,
    /// since its job is entirely about what the git binary reports. A machine without git on its
    /// PATH is not expected in this project's own CI, so no attempt is made to skip gracefully.
    /// </summary>
    internal static void GitHistoryRules(Harness harness)
    {
        harness.Group("GitHistory: reading commit metadata via the git binary");

        string root = Path.Combine(Path.GetTempPath(), "archon-tests-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunGitOrThrow(root, "init", "--initial-branch=main");
            RunGitOrThrow(root, "config", "user.email", "test@example.com");
            RunGitOrThrow(root, "config", "user.name", "Archon Tests");

            File.WriteAllText(Path.Combine(root, "a.cs"), "class A { }");
            RunGitOrThrow(root, "add", "a.cs");
            RunGitOrThrow(root, "commit", "-m", "add a.cs");

            File.WriteAllText(Path.Combine(root, "b.cs"), "class B { }");
            RunGitOrThrow(root, "add", "b.cs");
            RunGitOrThrow(root, "commit", "-m", "add b.cs");

            File.WriteAllText(Path.Combine(root, "a.cs"), "class A { void M() { } }");
            RunGitOrThrow(root, "add", "a.cs");
            RunGitOrThrow(root, "commit", "-m", "touch a.cs again");

            string? foundRoot = GitHistory.FindRepositoryRoot(root);
            harness.Check("finds the repository root for a path inside it",
                foundRoot is not null && Path.GetFullPath(foundRoot).TrimEnd('\\', '/')
                    .Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

            harness.Check("a path outside any repository yields no root",
                GitHistory.FindRepositoryRoot(Path.GetTempPath()) is null ||
                !Path.GetFullPath(GitHistory.FindRepositoryRoot(Path.GetTempPath())!).StartsWith(root, StringComparison.OrdinalIgnoreCase));

            IReadOnlyDictionary<string, int> churn = GitHistory.ChurnByFile(root, DateTimeOffset.UtcNow.AddDays(-1));
            harness.Equal("a.cs was touched by two commits in the window", 2, churn.GetValueOrDefault("a.cs"));
            harness.Equal("b.cs was touched by one commit in the window", 1, churn.GetValueOrDefault("b.cs"));

            IReadOnlyDictionary<string, int> noChurn = GitHistory.ChurnByFile(root, DateTimeOffset.UtcNow.AddDays(1));
            harness.Equal("a window starting in the future sees no commits", 0, noChurn.Count);

            IReadOnlyList<GitHistory.FileCommit> commits = GitHistory.CommitsTouching(root, "a.cs");
            harness.Equal("two commits touched a.cs", 2, commits.Count);

            // -S matches every commit where the string's occurrence count changes, so both the
            // commit that introduced "class A { }" and the later one that edited it away match;
            // the oldest match (last, since git log lists newest first) is the introduction.
            IReadOnlyList<GitHistory.FileCommit> pickaxe = GitHistory.CommitsTouching(root, "a.cs", pickaxe: "class A { }");
            harness.Equal("pickaxe search matches both the commit that added the text and the one that changed it away", 2, pickaxe.Count);
            harness.Equal("the oldest pickaxe match is the commit that introduced the text", commits[^1].Hash, pickaxe[^1].Hash);

            string? shown = GitHistory.ShowFileAt(root, commits[^1].Hash, "a.cs");
            harness.Equal("show reads a file's content as of the commit that introduced it", "class A { }", shown?.Trim());
        }
        finally
        {
            DeleteDirectoryRobustly(root);
        }
    }

    private static void RunGitOrThrow(string workingDirectory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }
    }

    private static void DeleteDirectoryRobustly(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a stray handle on Windows should not fail the suite over a temp directory.
        }
    }

    internal static void DebtRankingRules(Harness harness)
    {
        harness.Group("Debt ranking: baseline age x churn since acceptance");

        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = new List<BaselineEntry>
        {
            new() { Fingerprint = "old-and-churny", RuleId = "AR0001", File = "a.cs", Message = "m" },
            new() { Fingerprint = "old-but-stable", RuleId = "AR0002", File = "b.cs", Message = "m" },
            new() { Fingerprint = "no-history-found", RuleId = "AR0003", File = "c.cs", Message = "m" }
        };
        var introduced = new Dictionary<string, DateTimeOffset>
        {
            ["old-and-churny"] = now.AddDays(-100),
            ["old-but-stable"] = now.AddDays(-200)
            // "no-history-found" deliberately absent.
        };
        var churn = new Dictionary<string, int>
        {
            ["old-and-churny"] = 10,
            ["old-but-stable"] = 1
            // "no-history-found" deliberately absent: GetValueOrDefault yields zero.
        };

        IReadOnlyList<DebtEntry> ranked = DebtAnalyzer.Rank(entries, introduced, churn, now);
        harness.Equal("every baseline entry is represented, even ones history could not date", 3, ranked.Count);
        harness.Equal("age x churn outranks mere age", "old-and-churny", ranked[0].Fingerprint);
        harness.Equal("an entry with no history found ages as zero rather than guessed", 0,
            ranked.First(e => e.Fingerprint == "no-history-found").AgeDays);
        harness.Check("an entry with no history found has a null introduced date",
            ranked.First(e => e.Fingerprint == "no-history-found").Introduced is null);
        harness.Equal("age in days matches the gap to 'now'", 100,
            ranked.First(e => e.Fingerprint == "old-and-churny").AgeDays);
        harness.Equal("score is age in days times churn", 1000,
            ranked.First(e => e.Fingerprint == "old-and-churny").Score);
    }

    internal static void GitHistoryChurnSinceRules(Harness harness)
    {
        harness.Group("GitHistory: commit count since a point in time");

        string root = Path.Combine(Path.GetTempPath(), "archon-tests-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunGitOrThrow(root, "init", "--initial-branch=main");
            RunGitOrThrow(root, "config", "user.email", "test@example.com");
            RunGitOrThrow(root, "config", "user.name", "Archon Tests");

            File.WriteAllText(Path.Combine(root, "a.cs"), "v1");
            RunGitOrThrow(root, "add", "a.cs");
            RunGitOrThrow(root, "commit", "-m", "v1");

            // Git commit timestamps have one-second resolution; without this gap, "now" can land
            // in the same second as the v1 commit and be counted as at-or-after it.
            Thread.Sleep(1100);
            DateTimeOffset beforeSecondCommit = DateTimeOffset.UtcNow;

            File.WriteAllText(Path.Combine(root, "a.cs"), "v2");
            RunGitOrThrow(root, "add", "a.cs");
            RunGitOrThrow(root, "commit", "-m", "v2");

            File.WriteAllText(Path.Combine(root, "a.cs"), "v3");
            RunGitOrThrow(root, "add", "a.cs");
            RunGitOrThrow(root, "commit", "-m", "v3");

            harness.Equal("counts every commit touching the file since the given time", 2,
                GitHistory.CommitCountSince(root, "a.cs", beforeSecondCommit));
            harness.Equal("a window starting after every commit counts zero", 0,
                GitHistory.CommitCountSince(root, "a.cs", DateTimeOffset.UtcNow.AddDays(1)));
        }
        finally
        {
            DeleteDirectoryRobustly(root);
        }
    }
}
