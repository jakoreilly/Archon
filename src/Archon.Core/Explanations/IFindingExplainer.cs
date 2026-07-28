using Archon.Core.Findings;

namespace Archon.Core.Explanations;

/// <summary>
/// Optional prose for a finding that has already been detected. Detection is entirely
/// deterministic and never consults an explainer, so results stay reproducible and identical
/// offline; an explainer only ever adds commentary to a result that was produced without it, and
/// is invoked on explicit request rather than during analysis.
/// </summary>
public interface IFindingExplainer
{
    /// <summary>True when the explainer is configured and able to answer.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns an explanation for a finding, or <c>null</c> when unavailable. Implementations must
    /// fail soft: a failure to explain must never alter or invalidate the finding itself.
    /// </summary>
    Task<string?> ExplainAsync(Finding finding, string? surroundingSource, CancellationToken cancellationToken);
}

/// <summary>The default explainer, which never explains anything and requires no configuration.</summary>
public sealed class NullFindingExplainer : IFindingExplainer
{
    public static readonly NullFindingExplainer Instance = new();

    public bool IsAvailable => false;

    public Task<string?> ExplainAsync(Finding finding, string? surroundingSource, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
