using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags asynchronous code that discards failures or blocks a thread.
///
/// Whether an expression is task-typed is decided from syntax rather than from a resolved type, by
/// three signals: the receiver is a call whose name ends in <c>Async</c>, the receiver is named
/// after a task, or the construct sits inside an <c>async</c> method. A blocking access on a
/// receiver matching none of these is left alone, so an unrelated member called <c>Result</c> is
/// not reported.
/// </summary>
public sealed class AsyncSafetyRule : IRule
{
    /// <summary>Blocking on a task rather than awaiting it.</summary>
    public const string BlockingOnTask = "AR0010";

    /// <summary>A task-returning call whose result and failures are both discarded.</summary>
    public const string UnawaitedTask = "AR0011";

    /// <summary>An <c>async void</c> method, which cannot be awaited and hides its exceptions.</summary>
    public const string AsyncVoid = "AR0012";

    /// <summary>A catch block that discards the exception without logging or rethrowing.</summary>
    public const string SwallowedException = "AR0013";

    /// <summary>'throw ex;' inside a catch block, which resets the stack trace to the rethrow site.</summary>
    public const string RethrowLosesStackTrace = "AR0014";

    private const string Category = "async";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            BlockingOnTask,
            "Blocking on a task",
            Category,
            Severity.Warning,
            "A task is blocked on with .Result, .Wait() or .GetAwaiter().GetResult() instead of awaited."),
        new RuleDescriptor(
            UnawaitedTask,
            "Task result discarded",
            Category,
            Severity.Warning,
            "A task-returning call is neither awaited nor assigned, so its failures are lost."),
        new RuleDescriptor(
            AsyncVoid,
            "async void method",
            Category,
            Severity.Warning,
            "An async void method cannot be awaited and its exceptions cannot be caught by the caller."),
        new RuleDescriptor(
            SwallowedException,
            "Exception discarded",
            Category,
            Severity.Warning,
            "An empty catch block hides a failure completely."),
        new RuleDescriptor(
            RethrowLosesStackTrace,
            "Rethrow resets the stack trace",
            Category,
            Severity.Warning,
            "'throw ex;' inside a catch block resets the stack trace to this line instead of preserving where the exception actually originated. Use a bare 'throw;' instead.")
    };

    public RuleScope Scope => RuleScope.File;

    public string Language => RuleLanguages.CSharp;

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        if (context.TargetFile is not SourceFile file)
        {
            return Array.Empty<Finding>();
        }
        ParsedCSharp? parsed = context.Sources.GetCSharp(file.Path);
        if (parsed is null)
        {
            return Array.Empty<Finding>();
        }

        var findings = new List<Finding>();
        SyntaxNode root = parsed.Root;

        if (context.IsEnabled(BlockingOnTask))
        {
            findings.AddRange(FindBlocking(parsed, file.Path));
        }
        if (context.IsEnabled(UnawaitedTask))
        {
            findings.AddRange(FindUnawaited(parsed, file.Path));
        }
        if (context.IsEnabled(AsyncVoid))
        {
            findings.AddRange(FindAsyncVoid(parsed, root, file.Path));
        }
        if (context.IsEnabled(SwallowedException))
        {
            findings.AddRange(FindEmptyCatch(parsed, root, file.Path));
        }
        if (context.IsEnabled(RethrowLosesStackTrace))
        {
            findings.AddRange(FindRethrowLosesStackTrace(parsed, root, file.Path));
        }
        return findings;
    }

    private IEnumerable<Finding> FindBlocking(ParsedCSharp parsed, string filePath)
    {
        var reported = new HashSet<TextSpan>();

        foreach (MemberAccessExpressionSyntax access in parsed.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Name.Identifier.Text != "Result")
            {
                continue;
            }
            if (access.Parent is InvocationExpressionSyntax)
            {
                continue;
            }
            if (!LooksTaskLike(access.Expression) && !IsInsideAsyncMethod(access))
            {
                continue;
            }
            if (reported.Add(access.Span))
            {
                yield return Create(BlockingOnTask, parsed, access.Span, filePath, "BlockingOnTask",
                    "Blocking on a task with '.Result' risks a deadlock and holds a thread; await it instead.");
            }
        }

        foreach (InvocationExpressionSyntax invocation in parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                continue;
            }
            string name = member.Name.Identifier.Text;

            if (name is "Wait" or "WaitAll" && invocation.ArgumentList.Arguments.Count == 0)
            {
                if (!LooksTaskLike(member.Expression) && !IsInsideAsyncMethod(invocation))
                {
                    continue;
                }
                if (reported.Add(invocation.Span))
                {
                    yield return Create(BlockingOnTask, parsed, invocation.Span, filePath, "BlockingOnTask",
                        $"Blocking on a task with '.{name}()' risks a deadlock and holds a thread; await it instead.");
                }
                continue;
            }

            if (name == "GetResult" && member.Expression is InvocationExpressionSyntax awaiter &&
                awaiter.Expression is MemberAccessExpressionSyntax awaiterMember &&
                awaiterMember.Name.Identifier.Text == "GetAwaiter")
            {
                if (!LooksTaskLike(awaiterMember.Expression) && !IsInsideAsyncMethod(invocation))
                {
                    continue;
                }
                if (reported.Add(invocation.Span))
                {
                    yield return Create(BlockingOnTask, parsed, invocation.Span, filePath, "BlockingOnTask",
                        "Blocking on a task with '.GetAwaiter().GetResult()' risks a deadlock and holds a thread; await it instead.");
                }
            }
        }
    }

    private IEnumerable<Finding> FindUnawaited(ParsedCSharp parsed, string filePath)
    {
        foreach (ExpressionStatementSyntax statement in parsed.Root.DescendantNodes().OfType<ExpressionStatementSyntax>())
        {
            if (statement.Expression is not InvocationExpressionSyntax invocation)
            {
                continue;
            }
            if (!NameEndsWithAsync(invocation))
            {
                continue;
            }
            yield return Create(UnawaitedTask, parsed, invocation.Span, filePath, "UnawaitedTask",
                "This call returns a task that is neither awaited nor assigned, so a failure inside it is lost. " +
                "Await it, or assign it and observe it deliberately.");
        }
    }

    private IEnumerable<Finding> FindAsyncVoid(ParsedCSharp parsed, SyntaxNode root, string filePath)
    {
        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            bool returnsVoid = method.ReturnType is PredefinedTypeSyntax predefined && predefined.Keyword.Text == "void";
            if (!isAsync || !returnsVoid || LooksLikeEventHandler(method))
            {
                continue;
            }
            yield return Create(AsyncVoid, parsed, method.ReturnType.Span, filePath, "AsyncVoid",
                $"'{method.Identifier.Text}' is async void, so it cannot be awaited and the caller cannot catch its exceptions. " +
                "Return Task instead.");
        }
    }

    private IEnumerable<Finding> FindEmptyCatch(ParsedCSharp parsed, SyntaxNode root, string filePath)
    {
        foreach (CatchClauseSyntax catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (catchClause.Block.Statements.Count != 0)
            {
                continue;
            }
            yield return Create(SwallowedException, parsed, catchClause.Block.Span, filePath, "SwallowedException",
                "This catch block discards the exception; log it, rethrow it, or state in a comment why it is safe to ignore.");
        }
    }

    private IEnumerable<Finding> FindRethrowLosesStackTrace(ParsedCSharp parsed, SyntaxNode root, string filePath)
    {
        foreach (CatchClauseSyntax catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            string? exceptionName = catchClause.Declaration?.Identifier.Text;
            if (string.IsNullOrEmpty(exceptionName))
            {
                continue;
            }

            foreach (ThrowStatementSyntax throwStatement in catchClause.Block.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStatement.Expression is not IdentifierNameSyntax identifier || identifier.Identifier.Text != exceptionName)
                {
                    continue;
                }
                // A throw belongs to the nearest enclosing catch clause, so a nested catch shadowing
                // the same variable name is its own case, not this one.
                if (identifier.Ancestors().OfType<CatchClauseSyntax>().First() != catchClause)
                {
                    continue;
                }
                yield return Create(RethrowLosesStackTrace, parsed, throwStatement.Span, filePath, "RethrowLosesStackTrace",
                    $"'throw {exceptionName};' resets the stack trace to this line. Use a bare 'throw;' to preserve where '{exceptionName}' was actually thrown.");
            }
        }
    }

    /// <summary>
    /// Whether an expression is task-typed as far as syntax can tell: a call named for an
    /// asynchronous operation, or an identifier named after a task.
    /// </summary>
    private static bool LooksTaskLike(ExpressionSyntax expression)
    {
        if (expression is InvocationExpressionSyntax invocation)
        {
            return NameEndsWithAsync(invocation);
        }

        string? name = expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            _ => null
        };
        return name is not null &&
               (name.EndsWith("Task", StringComparison.Ordinal) || name.EndsWith("task", StringComparison.Ordinal));
    }

    private static bool NameEndsWithAsync(InvocationExpressionSyntax invocation)
    {
        string? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null
        };
        return name is not null && name.EndsWith("Async", StringComparison.Ordinal);
    }

    private static bool IsInsideAsyncMethod(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) == true;

    /// <summary>
    /// Recognises the one shape where <c>async void</c> is the required signature rather than a
    /// mistake, so the common correct case is not reported.
    /// </summary>
    private static bool LooksLikeEventHandler(MethodDeclarationSyntax method)
    {
        SeparatedSyntaxList<ParameterSyntax> parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2)
        {
            return false;
        }
        string second = parameters[1].Type?.ToString().TrimEnd('?') ?? "";
        return second.EndsWith("EventArgs", StringComparison.Ordinal);
    }

    private static Finding Create(string ruleId, ParsedCSharp parsed, TextSpan span, string filePath, string kind, string message)
    {
        LinePositionSpan lineSpan = parsed.Tree.GetLineSpan(span).Span;
        return new Finding
        {
            RuleId = ruleId,
            FilePath = filePath,
            Kind = kind,
            Span = new SourceSpan(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character),
            Message = message
        };
    }
}
