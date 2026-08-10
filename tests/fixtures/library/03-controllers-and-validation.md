# Controllers and Validation — Public

### PUB-API-01 · Controller
**Use when** adding any endpoint. One service call, one result wrap, no logic.
Declare every status the pipeline can return so the generated OpenAPI document is
accurate.
**Needs** —

```csharp
/// <summary>Widget order endpoints.</summary>
[ApiController]
[Route(WidgetOrderRoutes.Base)]
public sealed class WidgetOrdersController(IWidgetOrderService widgetOrderService) : ControllerBase
{
    /// <summary>Creates a widget order.</summary>
    /// <param name="request">The order request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [HttpPost]
    [ProducesResponseType<CreateWidgetOrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateWidgetOrderResponse>> Post(
        [FromBody] CreateWidgetOrderRequest request,
        CancellationToken cancellationToken)
    {
        string reference = await widgetOrderService.CreateAsync(request.ToCommand(), cancellationToken);

        return CreatedAtAction(nameof(Get), new { reference }, new CreateWidgetOrderResponse { Reference = reference });
    }

    /// <summary>Gets a widget order by reference.</summary>
    /// <param name="reference">The order reference.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [HttpGet("{reference}")]
    [ProducesResponseType<WidgetOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WidgetOrderResponse>> Get(string reference, CancellationToken cancellationToken)
    {
        WidgetOrderModel order = await widgetOrderService.GetAsync(reference, cancellationToken);
        return Ok(order.ToResponse());
    }

    /// <summary>Updates a widget order status.</summary>
    /// <param name="reference">The order reference.</param>
    /// <param name="request">The status update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [HttpPut("{reference}/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutStatus(
        string reference,
        [FromBody] UpdateWidgetOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        await widgetOrderService.UpdateStatusAsync(reference, request.ToCommand(), cancellationToken);
        return NoContent();
    }
}
```

---

### PUB-API-02 · Route constants
**Use when** adding a controller. Routes are referenced by tests, clients and
scheduler configuration, so they belong in one place.
**Needs** —

```csharp
/// <summary>Widget order route templates.</summary>
public static class WidgetOrderRoutes
{
    /// <summary>The controller base route.</summary>
    public const string Base = "api/v1/widget-orders";
}
```

---

### PUB-API-03 · Paged query contract
**Use when** an endpoint returns a list. Bounds are enforced by the contract, not
by the caller's goodwill.
**Needs** `System.ComponentModel.DataAnnotations`

```csharp
/// <summary>Base query parameters for a paged endpoint.</summary>
public abstract class PagedRequest
{
    private const int MaxPageSize = 200;

    [Range(1, int.MaxValue)]
    public int PageNumber { get; init; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; init; } = 50;
}

/// <summary>Query parameters for listing audits.</summary>
public sealed class GetAuditsRequest : PagedRequest
{
    [Required]
    public DateOnly? StartDate { get; init; }

    [Required]
    public DateOnly? EndDate { get; init; }

    public int? UserId { get; init; }
}

/// <summary>A page of results plus the totals needed to render a pager.</summary>
public sealed class PagedResponse<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];

    public int PageNumber { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
```

---

### PUB-API-04 · Input contract with data annotations
**Use when** validation is structural. Use FluentValidation (`PUB-API-07`) for
conditional or cross-field rules.
**Needs** `System.ComponentModel.DataAnnotations`

```csharp
/// <summary>Request to create a widget order.</summary>
public sealed class CreateWidgetOrderRequest
{
    [Required]
    [MinimumCount(1)]
    public IList<WidgetLineRequest> Lines { get; init; } = [];

    [Required]
    public DeliveryAddressRequest? DeliveryAddress { get; init; }
}

/// <summary>A single line on a widget order.</summary>
public sealed class WidgetLineRequest
{
    [Required]
    [MaxLength(20)]
    public string WidgetCode { get; init; } = string.Empty;

    [Range(1, 1000)]
    public int Quantity { get; init; }

    [MaxLength(50)]
    public string? Label { get; init; }
}
```

---

### PUB-API-05 · Custom validation attribute
**Use when** a structural rule is missing from the framework. Keep the message
stable — tests assert on it.
**Needs** `System.ComponentModel.DataAnnotations`

```csharp
/// <summary>Requires a collection to hold at least a minimum number of items.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MinimumCountAttribute(int minCount) : ValidationAttribute
{
    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is ICollection collection && collection.Count >= minCount)
            return ValidationResult.Success;

        return new ValidationResult($"The {validationContext.DisplayName} list must contain at least {minCount} items.");
    }
}
```

---

### PUB-API-06 · Contract to command mapper
**Use when** converting a request into a domain command. Static extension, one
expression body, normalisation happens here — not in the service.
**Needs** —

```csharp
/// <summary>Maps widget order contracts to and from domain models.</summary>
public static class WidgetOrderMapper
{
    /// <summary>Converts the request into a create command.</summary>
    public static CreateWidgetOrderCommand ToCommand(this CreateWidgetOrderRequest request) => new()
    {
        Lines = request.Lines.Select(line => new WidgetLine
        {
            // Normalise the natural key once, at the boundary.
            WidgetCode = Normalise(line.WidgetCode),
            Quantity = line.Quantity,
            Label = line.Label
        }).ToArray(),
        DeliveryAddress = new DeliveryAddress
        {
            Line1 = request.DeliveryAddress!.Line1,

            // Downstream systems reject nulls on optional strings.
            Line2 = request.DeliveryAddress.Line2 ?? string.Empty,
            PostalCode = request.DeliveryAddress.PostalCode,
            CountryCode = request.DeliveryAddress.CountryCode
        }
    };

    /// <summary>Converts the domain model into a response.</summary>
    public static WidgetOrderResponse ToResponse(this WidgetOrderModel model) => new()
    {
        Reference = model.Reference,
        Status = model.Status.ToString(),
        CreatedAt = model.CreatedAt,
        Lines = model.Lines.Select(l => new WidgetLineResponse { WidgetCode = l.WidgetCode, Quantity = l.Quantity }).ToArray()
    };

    private static string Normalise(string value) =>
        string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));
}
```

---

### PUB-API-07 · FluentValidation validator
**Use when** rules are conditional, cross-field, or need custom messages.
`CascadeMode.Continue` reports every failure in one response.
**Needs** `FluentValidation.AspNetCore`

```csharp
/// <summary>Validates the account creation request.</summary>
public sealed class CreateAccountRequestValidator : AbstractValidator<CreateAccountRequest>
{
    /// <summary>Initialises the rules.</summary>
    public CreateAccountRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(ValidationConstants.EmailMaxLength);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(ValidationConstants.PasswordMinLength)
            .Matches(ValidationConstants.PasswordStrengthPattern)
            .WithMessage(ValidationConstants.PasswordStrengthMessage);

        // Cross-field rule, guarded so it does not fire while either field is empty.
        RuleFor(x => x.Password)
            .Must((request, password) => !password.Contains(request.Email, StringComparison.OrdinalIgnoreCase))
            .WithMessage("Password must not contain the email address.")
            .When(x => !string.IsNullOrWhiteSpace(x.Email) && !string.IsNullOrWhiteSpace(x.Password));

        // Conditional rule driven by another property.
        RuleFor(x => x.TopUpAmount)
            .NotNull()
            .GreaterThan(0)
            .When(x => x.PaymentOption == PaymentOption.PrepaidBalance);

        // Collection element rules.
        RuleForEach(x => x.Vehicles).ChildRules(vehicle =>
            vehicle.RuleFor(v => v.RegistrationNumber).NotEmpty().MaximumLength(20));
    }
}

/// <summary>Registers FluentValidation.</summary>
public static IServiceCollection AddRequestValidation(this IServiceCollection services)
{
    services.AddFluentValidationAutoValidation();
    services.AddValidatorsFromAssemblyContaining<CreateAccountRequestValidator>();

    // Report all failures rather than stopping at the first.
    ValidatorOptions.Global.DefaultClassLevelCascadeMode = CascadeMode.Continue;
    ValidatorOptions.Global.DefaultRuleLevelCascadeMode = CascadeMode.Continue;

    return services;
}
```

---

### PUB-API-08 · Validation error result
**Use when** validation failures must use the same envelope as other errors instead
of `ProblemDetails`.
**Needs** —

```csharp
/// <summary>Writes model state failures using the service error envelope.</summary>
public sealed class ValidationErrorResult(ModelStateDictionary modelState) : IActionResult
{
    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var fieldErrors = modelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .SelectMany(entry => entry.Value!.Errors.Select(error => new FieldError
            {
                // A blank key means a body-level failure, so omit the field name.
                Field = string.IsNullOrEmpty(entry.Key) ? null : entry.Key,
                Message = error.ErrorMessage
            }))
            .ToArray();

        var body = new ValidationErrorResponse
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ErrorCode = ErrorCodes.InvalidInput,
            Message = "One or more validation errors occurred.",
            Errors = fieldErrors
        };

        context.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.HttpContext.Response.WriteAsJsonAsync(body);
    }
}
```
