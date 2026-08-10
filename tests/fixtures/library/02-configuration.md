# Configuration and Settings — Public

### PUB-CFG-01 · Bind a required section
**Use when** the service cannot start without the section. Fail at startup with a
message naming the section, not at first use with a null reference.
**Needs** —

```csharp
public static T GetRequiredOptions<T>(this IConfiguration configuration, string sectionName)
    where T : class =>
    configuration.GetRequiredSection(sectionName).Get<T>()
        ?? throw new InvalidOperationException($"Configuration section '{sectionName}' is missing or empty.");

// Usage.
ExternalApiOptions apiOptions = configuration.GetRequiredOptions<ExternalApiOptions>(ExternalApiOptions.SectionName);
services.AddSingleton(apiOptions);
```

---

### PUB-CFG-02 · Options with startup validation
**Use when** invalid configuration should stop the service rather than fail the
first request. `ValidateOnStart` runs the data annotations at build time.
**Needs** `Microsoft.Extensions.Options.DataAnnotations`

```csharp
/// <summary>Registers and validates the application settings.</summary>
public static IServiceCollection AddApplicationSettings(this IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<ExternalApiOptions>()
        .Bind(configuration.GetSection(ExternalApiOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddOptions<ImportOptions>()
        .Bind(configuration.GetSection(ImportOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(o => o.BatchSize > 0, "BatchSize must be greater than zero.")
        .ValidateOnStart();

    return services;
}
```

---

### PUB-CFG-03 · Named options for repeated shapes
**Use when** several sections share one POCO — three email templates, four API
clients. One type, many names.
**Needs** `Microsoft.Extensions.Options`

```csharp
// Registration.
services.Configure<TemplatedEmailOptions>("OrderConfirmation", configuration.GetSection("Email:OrderConfirmation"));
services.Configure<TemplatedEmailOptions>("OrderCancellation", configuration.GetSection("Email:OrderCancellation"));

// Consumption — IOptionsSnapshot re-reads per scope, so a reload is picked up.
public class EmailNotificationService(IOptionsSnapshot<TemplatedEmailOptions> options)
{
    public Task SendOrderConfirmationAsync(string recipient, CancellationToken cancellationToken)
    {
        TemplatedEmailOptions settings = options.Get("OrderConfirmation");
        return SendAsync(recipient, settings, cancellationToken);
    }
}
```

Lifetime guide:

| Interface | Reads configuration | Use for |
| --- | --- | --- |
| `IOptions<T>` | once, at first resolve | singletons, values that never change |
| `IOptionsSnapshot<T>` | once per scope | request-scoped and named options |
| `IOptionsMonitor<T>` | on every access, with change callbacks | singletons that must react to reload |

---

### PUB-CFG-04 · Outbound API options
**Use when** defining settings for an HTTP dependency. Retry settings are their own
type so every client reuses the same shape.
**Needs** `System.ComponentModel.DataAnnotations`

```csharp
/// <summary>Settings for an outbound HTTP dependency.</summary>
public sealed class ExternalApiOptions
{
    public const string SectionName = "ExternalApi";

    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    public string? AuthenticationPath { get; init; }

    [Required]
    public RetryOptions Retry { get; init; } = new();
}

/// <summary>Retry and timeout settings shared by all outbound clients.</summary>
public sealed class RetryOptions
{
    [Range(1, 10)]
    public int MaxAttempts { get; init; } = 3;

    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(0, 60_000)]
    public int BaseDelayMilliseconds { get; init; } = 200;
}
```

---

### PUB-CFG-05 · Settings POCO conventions
**Use when** writing any settings class.
**Needs** —

```csharp
/// <summary>Settings for the import process.</summary>
public sealed class ImportOptions
{
    // Section name as a constant: one string, referenced by registration and tests.
    public const string SectionName = "Import";

    // init-only: bound at startup, never mutated afterwards.
    [Required]
    public string SourceDirectory { get; init; } = string.Empty;

    public string FileExtension { get; init; } = ".csv";

    public string Delimiter { get; init; } = ",";

    [Range(1, 10_000)]
    public int BatchSize { get; init; } = 100;

    // Duration as a scalar plus unit in the name — no ambiguity at the call site.
    [Range(1, 168)]
    public int TemplateCacheHours { get; init; } = 24;
}
```

---

### PUB-CFG-06 · appsettings.json shape
**Use when** creating configuration for a new service. Secrets are supplied by the
environment — user secrets locally, a secret store in deployed environments.
**Needs** —

```jsonc
{
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" }
  },
  "Database": {
    "ConnectionString": "<supplied-per-environment>",
    "CommandTimeoutSeconds": 60,
    "MaxRetryCount": 3,
    "MaxRetryDelaySeconds": 10,
    "EnableSensitiveDataLogging": false,
    "EnableDetailedErrors": false
  },
  "ExternalApi": {
    "BaseUrl": "<supplied-per-environment>",
    "AuthenticationPath": "/security/login",
    "Retry": { "MaxAttempts": 3, "RequestTimeoutSeconds": 30, "BaseDelayMilliseconds": 200 }
  },
  "Import": {
    "SourceDirectory": "<supplied-per-environment>",
    "FileExtension": ".csv",
    "Delimiter": ";",
    "BatchSize": 100
  },
  "BackgroundWork": {
    "Enabled": true,
    "Jobs": [
      { "Name": "ImportFiles", "CronExpression": "0 0 10 * * ?", "Enabled": true }
    ]
  },
  "Email": {
    "Smtp": { "Host": "<supplied-per-environment>", "Port": 25, "UseTls": true },
    "OrderConfirmation": {
      "FromAddress": "<supplied-per-environment>",
      "Subject": "Order confirmation",
      "TemplatePath": "templates/order-confirmation.html"
    }
  },
  "Telemetry": { "ServiceName": "<service name>", "OtlpEndpoint": "<supplied-per-environment>" }
}
```

Precedence — later sources override earlier ones:
`appsettings.json` → `appsettings.{Environment}.json` → user secrets (development)
→ environment variables → command line.

Nested keys as environment variables use `__`:
`Database__ConnectionString`, `ExternalApi__Retry__MaxAttempts`.

---

### PUB-CFG-07 · Array and dictionary sections
**Use when** a section is a list or an open key/value map.
**Needs** —

```csharp
// Array section bound to a typed array, then wrapped so consumers inject one class.
public sealed class ScheduledJobOptions
{
    public const string SectionName = "BackgroundWork:Jobs";

    public string Name { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;

    public bool Enabled { get; init; }
}

public sealed class ScheduledJobList
{
    public IReadOnlyList<ScheduledJobOptions> Jobs { get; init; } = [];
}

ScheduledJobOptions[] jobs = configuration
    .GetRequiredSection(ScheduledJobOptions.SectionName)
    .Get<ScheduledJobOptions[]>() ?? [];

services.AddSingleton(new ScheduledJobList { Jobs = jobs });

// Open map: bind straight to a dictionary.
Dictionary<string, string> featureFlags = configuration
    .GetSection("FeatureFlags")
    .Get<Dictionary<string, string>>() ?? [];
```
