# Background Work and Messaging — Public

### PUB-JOB-01 · Startup task hosted service
**Use when** work must run once at start — schema check, schedule registration,
cache warm-up. `StartAsync` blocks startup, so keep it bounded.
**Needs** —

```csharp
/// <summary>Registers this service's schedules with the scheduler on start.</summary>
public sealed class ScheduleSyncService(
    ILogger<ScheduleSyncService> logger,
    IServiceProvider serviceProvider,
    ScheduledJobList jobList) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Synchronising {JobCount} scheduled job(s).", jobList.Jobs.Count);

        // A hosted service is a singleton, so scoped dependencies need an explicit scope.
        using IServiceScope scope = serviceProvider.CreateScope();
        ISchedulerClient scheduler = scope.ServiceProvider.GetRequiredService<ISchedulerClient>();

        foreach (ScheduledJobOptions job in jobList.Jobs.Where(j => j.Enabled))
        {
            try
            {
                await scheduler.UpsertJobAsync(job.Name, job.CronExpression, cancellationToken);
                logger.LogInformation("Synchronised job {JobName}.", job.Name);
            }
            catch (Exception ex)
            {
                // A scheduler outage must not prevent the service from serving requests.
                logger.LogError(ex, "Failed to synchronise job {JobName}.", job.Name);
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

---

### PUB-JOB-02 · Periodic background service
**Use when** work repeats on an interval and no external scheduler is involved.
`PeriodicTimer` does not overlap: the next tick waits for the current iteration.
**Needs** —

```csharp
/// <summary>Runs the import on a fixed interval.</summary>
public sealed class ImportBackgroundService(
    ILogger<ImportBackgroundService> logger,
    IServiceScopeFactory scopeFactory,
    IOptions<ImportOptions> options) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));

        // Iterations never overlap; a slow run delays the next tick rather than doubling up.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // A fresh scope per iteration: a leaked DbContext would grow forever.
                using IServiceScope scope = scopeFactory.CreateScope();
                IFileImportService importService = scope.ServiceProvider.GetRequiredService<IFileImportService>();

                await importService.ImportAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
                break;
            }
            catch (Exception ex)
            {
                // Never rethrow: an unhandled exception here stops the host.
                logger.LogError(ex, "The scheduled import failed. Retrying on the next interval.");
            }
        }
    }
}
```

---

### PUB-JOB-03 · Cron-scheduled job
**Use when** the schedule is a cron expression. `DisallowConcurrentExecution`
prevents overlap when a run exceeds its interval.
**Needs** `Quartz`

```csharp
/// <summary>Removes abandoned draft records.</summary>
[DisallowConcurrentExecution]
public sealed class CleanupJob(
    ILogger<CleanupJob> logger,
    IDraftCleanupService cleanupService) : IJob
{
    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("Removing abandoned draft records.");

        try
        {
            int removed = await cleanupService.RemoveAbandonedDraftsAsync(context.CancellationToken);
            logger.LogInformation("Removed {RemovedCount} abandoned draft record(s).", removed);
        }
        catch (Exception ex)
        {
            // Never rethrow: an unhandled job exception can stop the scheduler.
            logger.LogError(ex, "Failed to remove abandoned draft records.");
        }
    }
}
```

---

### PUB-JOB-04 · Cron job registration
**Use when** enabling scheduled jobs. The feature flag keeps them off in tests and
in instances that must not run them.
**Needs** `Quartz.Extensions.Hosting`

```csharp
/// <summary>Registers the cron-scheduled jobs.</summary>
public static IServiceCollection AddScheduledJobs(this IServiceCollection services, IConfiguration configuration)
{
    if (!configuration.GetValue<bool>("BackgroundWork:Enabled"))
        return services;

    services.AddQuartz(quartz =>
    {
        var jobKey = new JobKey(nameof(CleanupJob));

        quartz.AddJob<CleanupJob>(job => job.WithIdentity(jobKey));

        quartz.AddTrigger(trigger => trigger
            .ForJob(jobKey)
            .WithIdentity($"{nameof(CleanupJob)}-trigger")
            .WithCronSchedule(configuration.GetValue<string>("BackgroundWork:CleanupCron")!));
    });

    // Lets in-flight jobs finish during shutdown.
    services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

    return services;
}
```

---

### PUB-JOB-05 · Scoped services inside long-running work
**Use when** a singleton or long loop needs scoped dependencies. Create the scope
once per unit of work, resolve up front, keep it alive for the whole operation.
**Needs** —

```csharp
/// <summary>Runs the import inside its own dependency scope.</summary>
public sealed class FileImportService(IServiceScopeFactory scopeFactory) : IFileImportService
{
    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();

        // Resolved once: passing them down keeps the dependency list visible.
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IDateTimeProvider dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        ILogger<FileImportService> logger = scope.ServiceProvider.GetRequiredService<ILogger<FileImportService>>();
        IOptions<AlertOptions> alertOptions = scope.ServiceProvider.GetRequiredService<IOptions<AlertOptions>>();

        try
        {
            await RunAsync(unitOfWork, dateTimeProvider, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during the import.");

            // Alert, then rethrow so the caller records a failed run.
            await emailService.SendAsync(
                alertOptions.Value.Recipients,
                alertOptions.Value.Subject,
                $"The import failed: {ex.Message}",
                cancellationToken);

            throw;
        }
    }
}
```

---

### PUB-JOB-06 · Single-flight execution
**Use when** an endpoint or trigger starts work that must not run twice
concurrently. Returns immediately if a run is already in progress.
**Needs** —

```csharp
/// <summary>Runs an operation at most once at a time per key.</summary>
public interface ISingleFlightRunner
{
    Task<bool> TryRunAsync(string key, Func<CancellationToken, Task> operation, CancellationToken cancellationToken);
}

/// <summary>In-process single-flight guard. Use a distributed lock across instances.</summary>
public sealed class SingleFlightRunner(ILogger<SingleFlightRunner> logger) : ISingleFlightRunner
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public async Task<bool> TryRunAsync(
        string key,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        // Zero timeout: report "already running" instead of queueing behind it.
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            logger.LogInformation("Operation {OperationKey} is already running; skipping this trigger.", key);
            return false;
        }

        try
        {
            await operation(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }
}

// Usage from a controller: 202 when started, 409 when a run is in flight.
[HttpPost("process")]
public async Task<IActionResult> Process(CancellationToken cancellationToken)
{
    bool started = await singleFlightRunner.TryRunAsync(
        "process-transactions",
        transactionService.ProcessAllAsync,
        cancellationToken);

    return started ? Accepted() : Conflict();
}
```

---

### PUB-MSG-01 · Message consumer
**Use when** subscribing to a queue or topic. The consumer stays thin — hand off to a
service. Idempotency is the consumer's responsibility: at-least-once delivery means
the same message will arrive twice.
**Needs** `MassTransit` (or the equivalent for your broker)

```csharp
/// <summary>Applies balance top-up status messages.</summary>
public sealed class TopUpStatusConsumer(
    ILogger<TopUpStatusConsumer> logger,
    ITopUpService topUpService) : IConsumer<TopUpStatusMessage>
{
    /// <inheritdoc />
    public async Task Consume(ConsumeContext<TopUpStatusMessage> context)
    {
        logger.LogInformation(
            "Received top-up status {Status} for reference {Reference}. MessageId {MessageId}",
            context.Message.Status,
            context.Message.Reference,
            context.MessageId);

        // Keyed on the message reference, so a redelivery is a no-op.
        await topUpService.ApplyStatusAsync(context.Message, context.CancellationToken);
    }
}
```

---

### PUB-MSG-02 · Bus and endpoint registration
**Use when** wiring consumers. Retry handles transient faults; the error queue
catches everything else instead of dropping it.
**Needs** `MassTransit.RabbitMQ`

```csharp
/// <summary>Registers the message bus and its consumers.</summary>
public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
{
    BrokerOptions brokerOptions = configuration.GetRequiredOptions<BrokerOptions>(BrokerOptions.SectionName);

    services.AddMassTransit(busConfigurator =>
    {
        busConfigurator.AddConsumer<TopUpStatusConsumer>();

        busConfigurator.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(brokerOptions.Host, brokerOptions.VirtualHost, host =>
            {
                host.Username(brokerOptions.Username);
                host.Password(brokerOptions.Password);
            });

            cfg.ReceiveEndpoint(brokerOptions.TopUpStatusQueue, endpoint =>
            {
                // Durable across broker restarts.
                endpoint.SetQuorumQueue();

                // Bounded in-flight work; without it a burst can exhaust the pool.
                endpoint.PrefetchCount = brokerOptions.PrefetchCount;
                endpoint.ConcurrentMessageLimit = brokerOptions.ConcurrentMessageLimit;

                // Transient faults retried in place; anything else goes to the error queue.
                endpoint.UseMessageRetry(retry => retry.Exponential(
                    brokerOptions.RetryCount,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(2)));

                endpoint.ConfigureConsumer<TopUpStatusConsumer>(context);
            });
        });
    });

    return services;
}
```
