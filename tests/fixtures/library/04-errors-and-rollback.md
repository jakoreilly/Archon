# Errors and Rollback — Public

### PUB-ERR-01 · Exception envelope and codes
**Use when** creating a service. One response shape for every failure; codes are
part of the public contract, so append and never renumber.
**Needs** —

```csharp
/// <summary>The error body returned for every failed request.</summary>
public class ErrorResponse
{
    public int StatusCode { get; init; }

    public int ErrorCode { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>Correlates the response with the server log entry.</summary>
    public string? TraceId { get; init; }
}

/// <summary>The error body returned for validation failures.</summary>
public sealed class ValidationErrorResponse : ErrorResponse
{
    public IReadOnlyList<FieldError> Errors { get; init; } = [];
}

/// <summary>A single field-level validation failure.</summary>
public sealed class FieldError
{
    public string? Field { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>Machine-readable error codes. Append only.</summary>
public static class ErrorCodes
{
    public const int Unexpected = 1000;
    public const int InvalidInput = 1001;
    public const int Unauthorised = 1002;
    public const int NotFound = 1003;
    public const int Conflict = 1004;
    public const int DependencyFailure = 1005;
}
```

---

### PUB-ERR-02 · Domain exception hierarchy
**Use when** creating a service. Each type maps to exactly one HTTP status, and
carries a code so callers branch without parsing text.
**Needs** —

```csharp
/// <summary>Base type for failures the caller can be told about.</summary>
public abstract class DomainException(string message, int errorCode, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>The request is well formed but not valid in the current state. Maps to 400.</summary>
public sealed class InvalidInputException(string message, int errorCode = ErrorCodes.InvalidInput)
    : DomainException(message, errorCode);

/// <summary>The addressed resource does not exist. Maps to 404.</summary>
public sealed class ResourceNotFoundException(string message, int errorCode = ErrorCodes.NotFound)
    : DomainException(message, errorCode);

/// <summary>The request conflicts with current state. Maps to 409.</summary>
public sealed class ResourceConflictException(string message, int errorCode = ErrorCodes.Conflict)
    : DomainException(message, errorCode);

/// <summary>The caller is not permitted. Maps to 401.</summary>
public sealed class UnauthorisedException(string message = "Invalid session token.")
    : DomainException(message, ErrorCodes.Unauthorised);

/// <summary>A dependency failed in a way the caller cannot act on. Maps to 502.</summary>
public sealed class DependencyFailureException(string message, Exception? innerException = null)
    : DomainException(message, ErrorCodes.DependencyFailure, innerException);
```

---

### PUB-ERR-03 · Exception handling middleware
**Use when** creating a service. The only place a status code is derived from an
exception. Expected failures log at `Warning`; the unexpected branch never leaks the
exception message.
**Needs** —

```csharp
/// <summary>Converts unhandled exceptions into the service error envelope.</summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    /// <summary>Invokes the next delegate and handles any exception it throws.</summary>
    public async Task InvokeAsync(HttpContext context, IRollbackService rollbackService)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            await HandleAsync(context, exception, rollbackService);
        }
    }

    private static (int StatusCode, int ErrorCode) Map(Exception exception) => exception switch
    {
        InvalidInputException e => (StatusCodes.Status400BadRequest, e.ErrorCode),
        UnauthorisedException e => (StatusCodes.Status401Unauthorized, e.ErrorCode),
        ResourceNotFoundException e => (StatusCodes.Status404NotFound, e.ErrorCode),
        ResourceConflictException e => (StatusCodes.Status409Conflict, e.ErrorCode),
        DependencyFailureException e => (StatusCodes.Status502BadGateway, e.ErrorCode),
        OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, ErrorCodes.Unexpected),
        _ => (StatusCodes.Status500InternalServerError, ErrorCodes.Unexpected)
    };

    private async Task HandleAsync(HttpContext context, Exception exception, IRollbackService rollbackService)
    {
        // A cancelled request has no client left to answer.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request was cancelled by the client.");
            return;
        }

        (int statusCode, int errorCode) = Map(exception);

        if (statusCode >= StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled failure: {ExceptionMessage}", exception.Message);
        else
            logger.LogWarning(exception, "{ErrorMessage}", exception.Message);

        // Compensating actions run before the response is written.
        await rollbackService.RollbackAsync();

        // Response already started: nothing safe left to write.
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(new ErrorResponse
        {
            StatusCode = statusCode,
            ErrorCode = errorCode,

            // Generic text for 5xx: internal detail stays in the log.
            Message = statusCode >= StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred. Please try again."
                : exception.Message,
            TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
        });
    }
}
```

---

### PUB-ERR-04 · Throwing domain exceptions
**Use when** a service detects an expected failure.
**Needs** —

```csharp
// 404 — the addressed resource does not exist.
WidgetOrder order = await GetByReferenceAsync(reference, cancellationToken)
    ?? throw new ResourceNotFoundException($"Order reference {reference} not found.");

// 409 — the request conflicts with current state.
if (existingCodes is { Count: > 0 })
    throw new ResourceConflictException($"Order for widget code(s) '{string.Join(',', existingCodes)}' already exists.");

// 400 — well formed, invalid in this state.
if (order.Status is OrderStatus.Fulfilled or OrderStatus.Cancelled)
    throw new InvalidInputException($"Order {reference} cannot be updated because it is already {order.Status}.");

// 502 — a dependency failed in a way the caller cannot act on.
throw new DependencyFailureException("The fulfilment provider rejected the request.", ex);
```

---

### PUB-ERR-05 · Compensating rollback
**Use when** an operation commits locally, then calls an external system that may
fail. Register the undo immediately after the commit, before the external call.
**Needs** —

```csharp
/// <summary>Collects compensating actions to run if the request fails.</summary>
public interface IRollbackService
{
    void AddRollbackAction(Func<Task> action);

    Task RollbackAsync();
}

/// <summary>Runs compensating actions in reverse registration order.</summary>
public sealed class RollbackService(ILogger<RollbackService> logger) : IRollbackService
{
    private readonly Stack<Func<Task>> _actions = new();

    public void AddRollbackAction(Func<Task> action) => _actions.Push(action);

    public async Task RollbackAsync()
    {
        while (_actions.Count > 0)
        {
            Func<Task> action = _actions.Pop();

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                // One failed compensation must not prevent the rest from running.
                logger.LogError(ex, "A rollback action failed.");
            }
        }
    }
}

// Usage.
public async Task<string> CreateAsync(CreateWidgetOrderCommand command, CancellationToken cancellationToken)
{
    WidgetOrder order = await CreateAndSaveAsync(command, cancellationToken);

    // Registered before the external call so any throw past this point undoes the write.
    rollbackService.AddRollbackAction(async () =>
    {
        logger.LogError("Order creation failed for reference {Reference}.", order.Reference);

        await unitOfWork.Query<WidgetOrder>()
            .Where(x => x.Id == order.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.Status, OrderStatus.Declined), CancellationToken.None);
    });

    string externalReference = await fulfilmentClient.SubmitAsync(command, order.Reference, cancellationToken);

    await unitOfWork.Query<WidgetOrder>()
        .Where(x => x.Id == order.Id)
        .ExecuteUpdateAsync(set => set.SetProperty(o => o.ExternalReference, externalReference), cancellationToken);

    return order.Reference;
}
```

---

### PUB-ERR-06 · Non-critical side effect
**Use when** a follow-up action must not fail the request — notification, analytics
push. Catch inside the helper; never let a discarded task throw unobserved.
**Needs** —

```csharp
// Caller: no await, because the request must not wait on or fail from the notification.
_ = SendConfirmationAsync(order.Reference, order.CustomerId);

private async Task SendConfirmationAsync(string reference, int customerId)
{
    try
    {
        CustomerModel customer = await customerClient.GetAsync(customerId, CancellationToken.None);

        logger.LogInformation("Sending order confirmation for {Reference}.", reference);
        await emailService.SendOrderConfirmationAsync(customer, reference, CancellationToken.None);
    }
    catch (Exception ex)
    {
        // Swallowed deliberately: the order is already committed and valid.
        logger.LogError(ex, "Failed to send the order confirmation for {Reference}.", reference);
    }
}
```

---

### PUB-ERR-07 · Third-party response code mapping
**Use when** an external API signals failure in the body rather than the status code.
Translate once, at the client boundary, into local exceptions.
**Needs** —

```csharp
private static void EnsureSuccess(ExternalOrderResponse response)
{
    string message;

    switch (response.ResultCode)
    {
        case ExternalResultCode.Success:
            return;
        case ExternalResultCode.InvalidCustomerName:
            message = "The first name field is required.";
            break;
        case ExternalResultCode.InvalidProductClass:
            message = "Invalid product class.";
            break;
        case ExternalResultCode.OrderAlreadyExists:
        case ExternalResultCode.AlreadyAssigned:
            // Their message is caller-safe here, so pass it through.
            throw new ResourceConflictException(response.ResultMessage);
        default:
            message = response.ResultMessage;
            break;
    }

    throw new InvalidInputException(message);
}
```

---

### PUB-ERR-08 · Contained failure inside a batch
**Use when** processing many items and one bad item must not stop the run.
**Needs** —

```csharp
private static async Task<TResult?> TryAsync<TResult>(
    Func<Task<TResult>> operation,
    ILogger logger,
    string failureMessage)
    where TResult : class
{
    try
    {
        return await operation();
    }
    catch (Exception ex)
    {
        // Null return signals "retry later"; the caller increments the retry counter.
        logger.LogError(ex, "{FailureMessage}", failureMessage);
        return null;
    }
}
```
