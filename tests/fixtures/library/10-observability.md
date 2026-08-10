# Observability — Public

### PUB-OBS-01 · Service metrics
**Use when** a batch or queue process needs a health signal. Observable gauges are
read by the meter on collection, so setters are cheap and never block.
**Needs** `System.Diagnostics.Metrics`

```csharp
/// <summary>Publishes the service's operational metrics.</summary>
public sealed class ServiceMetrics
{
    /// <summary>Meter name. Must be registered with the OpenTelemetry provider.</summary>
    public const string MeterName = "Service.Widgets";

    private readonly Counter<long> _ordersCreated;
    private readonly Histogram<double> _importDuration;

    private int InvalidRecordsBeforeImport { get; set; }

    private int InvalidRecordsAfterImport { get; set; }

    private int UnprocessedTransactions { get; set; }

    public ServiceMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(MeterName);

        // Gauges: current state, sampled on collection.
        meter.CreateObservableGauge(
            "widget.import.invalid_records_before",
            () => InvalidRecordsBeforeImport,
            "{record}",
            "Invalid records present before the import started.");

        meter.CreateObservableGauge(
            "widget.import.invalid_records_after",
            () => InvalidRecordsAfterImport,
            "{record}",
            "Invalid records present after the import completed.");

        meter.CreateObservableGauge(
            "widget.transactions.unprocessed",
            () => UnprocessedTransactions,
            "{transaction}",
            "Transactions not yet sent downstream.");

        // Counter: monotonic total. Tags keep cardinality low — status, not identifiers.
        _ordersCreated = meter.CreateCounter<long>("widget.orders.created", "{order}", "Widget orders created.");

        _importDuration = meter.CreateHistogram<double>("widget.import.duration", "s", "Duration of an import run.");
    }

    public void SetInvalidRecordsBeforeImport(int count) => InvalidRecordsBeforeImport = count;

    public void SetInvalidRecordsAfterImport(int count) => InvalidRecordsAfterImport = count;

    public void SetUnprocessedTransactions(int count) => UnprocessedTransactions = count;

    public void RecordOrderCreated(string status) =>
        _ordersCreated.Add(1, new KeyValuePair<string, object?>("status", status));

    public void RecordImportDuration(TimeSpan duration) => _importDuration.Record(duration.TotalSeconds);
}
```

Recording — measure before and after a run so a regression is visible:

```csharp
int invalidBefore = await unitOfWork.Query<ImportedRecord>().CountAsync(x => x.Status == ImportStatus.Invalid, cancellationToken);
metrics.SetInvalidRecordsBeforeImport(invalidBefore);

long startTimestamp = Stopwatch.GetTimestamp();

await RunImportAsync(cancellationToken);

metrics.RecordImportDuration(Stopwatch.GetElapsedTime(startTimestamp));

int invalidAfter = await unitOfWork.Query<ImportedRecord>().CountAsync(x => x.Status == ImportStatus.Invalid, cancellationToken);
metrics.SetInvalidRecordsAfterImport(invalidAfter);
```

---

### PUB-OBS-02 · OpenTelemetry registration
**Use when** creating a service. `AddMeter` must name the same meter the metrics class
creates, or those metrics are silently dropped.
**Needs** `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`

```csharp
/// <summary>Registers tracing, metrics and health checks.</summary>
public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
{
    TelemetryOptions telemetry = configuration.GetRequiredOptions<TelemetryOptions>(TelemetryOptions.SectionName);

    services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(telemetry.ServiceName, serviceVersion: telemetry.ServiceVersion))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation(o =>
                // Health probes would otherwise dominate the trace volume.
                o.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation(o => o.SetDbStatementForText = false)
            .AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint)))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()

            // Must match ServiceMetrics.MeterName.
            .AddMeter(ServiceMetrics.MeterName)
            .AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint)));

    services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("database");

    return services;
}
```

---

### PUB-OBS-03 · Structured logging
**Use when** writing any log call. Named placeholders only — an interpolated string
produces one unique message per value and cannot be queried or aggregated.
**Needs** `Microsoft.Extensions.Logging`

```csharp
// Correct: values become searchable fields.
logger.LogInformation("Created order with reference {Reference}.", order.Reference);

logger.LogInformation(
    "Order accepted. Local reference {Reference}, external reference {ExternalReference}",
    order.Reference,
    externalReference);

// Correct: exception first, message as a template.
logger.LogError(ex, "Failed to submit order {Reference} to the fulfilment provider.", order.Reference);

// Correct: expected failure at Warning, exception retained for the stack.
logger.LogWarning(ex, "{ErrorMessage}", ex.Message);

// Correct: level chosen at runtime, template still constant per branch.
logger.Log(
    hasErrors ? LogLevel.Warning : LogLevel.Information,
    hasErrors ? "File {FileName} has errors and was not imported." : "File {FileName} is empty.",
    fileName);

// Wrong: no queryable fields, unbounded distinct message count.
logger.LogInformation($"Created {records.Count} records.");

// Wrong: personal data in the log.
logger.LogInformation("Sending confirmation to {EmailAddress}.", customer.EmailAddress);
```

Level guide:

| Level | Use for |
| --- | --- |
| `Debug` | Developer detail; off in deployed environments |
| `Information` | Step boundaries, counts, identifiers of what was processed |
| `Warning` | Expected failure the caller can act on; skipped or duplicate input |
| `Error` | Unexpected failure, dependency failure, swallowed side-effect failure |
| `Critical` | The service cannot continue |

---

### PUB-OBS-04 · High-frequency log call
**Use when** a log statement sits on a hot path. Source-generated logging avoids
boxing and the format-string parse on every call.
**Needs** `Microsoft.Extensions.Logging.Abstractions`

```csharp
/// <summary>Source-generated log messages for the transaction processor.</summary>
internal static partial class TransactionLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Claimed transaction {TransactionId} for processing.")]
    public static partial void TransactionClaimed(ILogger logger, int transactionId);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Transaction {TransactionId} failed and will be retried. Attempt {Attempt}.")]
    public static partial void TransactionRetrying(ILogger logger, int transactionId, int attempt);
}

// Usage.
TransactionLog.TransactionClaimed(logger, transaction.Id);
```

---

### PUB-OBS-05 · Personal data in logs
**Use when** a model that gets logged or serialised holds personal or payment data.
Redact at the model, so no call site has to remember.
**Needs** `System.Text.Json`

```csharp
/// <summary>Marks a property whose value must never reach a log or an outbound payload.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveDataAttribute : Attribute;

/// <summary>Serialises an object with sensitive properties replaced.</summary>
public static class SensitiveDataSerializer
{
    private const string RedactedValue = "***";

    public static string SerializeRedacted<T>(T value)
        where T : class
    {
        Dictionary<string, object?> redacted = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(
                property => property.Name,
                property => property.GetCustomAttribute<SensitiveDataAttribute>() is not null
                    ? RedactedValue
                    : property.GetValue(value));

        return JsonSerializer.Serialize(redacted);
    }
}

// Usage on the model.
public sealed class CreateAccountCommand
{
    public string EmailAddress { get; init; } = string.Empty;

    [SensitiveData]
    public string Password { get; init; } = string.Empty;

    [SensitiveData]
    public string SecurityAnswer { get; init; } = string.Empty;
}

// Also excluded from a ToString-style override, so an accidental log is safe.
public override string ToString() => SensitiveDataSerializer.SerializeRedacted(this);
```
