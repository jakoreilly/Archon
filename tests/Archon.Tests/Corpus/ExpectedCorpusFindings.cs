namespace Archon.Tests.Corpus;

/// <summary>
/// What the rule set is expected to say about idiomatic code. Every entry is a decision that
/// the library's pattern and the rule's judgement genuinely differ, or that the rule is right
/// and the snippet is an accepted exception — never a number written down to make a run pass.
/// </summary>
internal static class ExpectedCorpusFindings
{
    /// <summary>Snippet id and ordinal → rule id → expected count. Absent means zero.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ByBlock =
        new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
        {
            // The snippet is illustrative and stops right after the assignment; 'affectedRows'
            // would be read by the caller in real code, but nothing after it exists in this block.
            ["PUB-DATA-06-0"] = new Dictionary<string, int> { ["AR0071"] = 1 },

            // 'cancellationToken' implements IHostedService.StopAsync, but the parameter is
            // unused because the service has nothing to do on shutdown. UnusedSymbolsRule only
            // exempts an override, an explicit interface implementation or an event-handler
            // shape (UnusedSymbolsRule.cs:104) — an implicit interface implementation like this
            // one is not syntactically distinguishable from an ordinary method, by the rule's own
            // documented design ("cannot always be told from syntax alone").
            ["PUB-JOB-01-0"] = new Dictionary<string, int> { ["AR0070"] = 1 },

            // Moq's fluent '.ReturnsAsync(...)' is a same-line continuation of '.Setup(...)' and
            // returns the mock's setup object, not a task — but AsyncSafetyRule decides
            // "task-shaped" purely from a call name ending in "Async" (AsyncSafetyRule.cs:234),
            // by its own documented, deliberate syntax-only stance.
            ["PUB-TEST-03-0"] = new Dictionary<string, int> { ["AR0011"] = 1 },
            ["PUB-TEST-06-0"] = new Dictionary<string, int> { ["AR0011"] = 1 },

            // 'Execute' implements Quartz's IJob.Execute(IJobExecutionContext) — a fixed interface
            // signature — but AsyncContractRule's SVC0020 only exempts an override, an explicit
            // interface implementation named 'I...', an HTTP/test attribute or an event-handler
            // shape; an implicit implementation of a third-party interface is the same "cannot
            // always be told from syntax alone" limitation AR0070 already accepts (see PUB-JOB-01
            // in Phase 2).
            ["PUB-JOB-03-0"] = new Dictionary<string, int> { ["SVC0020"] = 1 },

            // 'Consume' implements MassTransit's IConsumer<T>.Consume(ConsumeContext<T>) — the
            // same implicit-interface-implementation limitation as PUB-JOB-03 above.
            ["PUB-MSG-01-0"] = new Dictionary<string, int> { ["SVC0020"] = 1 },
        };

    /// <summary>Snippet ids whose block is bare statements, where method-shaped rules are blind.</summary>
    public static readonly IReadOnlySet<string> StatementShaped = new HashSet<string>(StringComparer.Ordinal)
    {
        "PUB-BOOT-01", "PUB-DATA-06", "PUB-ERR-04", "PUB-HTTP-04", "PUB-HTTP-05", "PUB-OBS-01", "PUB-OBS-03"
    };

    /// <summary>
    /// Blocks no candidate shape could parse without a syntax error. Every one of these mixes a
    /// type or member declaration with executable usage code in the same fenced block — two
    /// snippets in one, shown together for the reader's benefit. Wrapping as Member fails because
    /// a bare usage statement is not a valid class member; wrapping as Statements fails because a
    /// declaration with an access modifier ('public'/'private') is not a valid local function.
    /// The snippet library is entitled to do this (it is prose, not compilable code by contract);
    /// the corpus records these as excluded rather than inventing a third wrapper shape to parse
    /// "declaration followed by its own usage example", which no rule needs to see.
    /// </summary>
    public static readonly IReadOnlySet<string> Unparseable = new HashSet<string>(StringComparer.Ordinal)
    {
        // Extension method declaration followed by "// Usage." and the calling code.
        "PUB-CFG-01-0",
        // Options registration statements followed by the class that consumes them.
        "PUB-CFG-03-0",
        // Options types followed by the binding statements that use them.
        "PUB-CFG-07-0",
        // The discarded fire-and-forget call followed by the helper method it calls.
        "PUB-ERR-06-0",
        // A partial class of [LoggerMessage] declarations followed by a call to one of them.
        "PUB-OBS-04-0",
        // Type declarations followed by a trailing 'public override ToString()' — Roslyn requires
        // top-level statements to precede type declarations, so the trailing method is absorbed
        // into parse-error recovery rather than recognised as a local function; ClassifyShape
        // therefore sees zero global statements and classifies this Unit, whose only candidate
        // (unwrapped) inherits that same error with no Member fallback to fall back on.
        "PUB-OBS-05-0",
    };
}
