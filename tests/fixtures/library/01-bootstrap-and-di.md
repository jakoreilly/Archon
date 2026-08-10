# Bootstrap and Dependency Injection — Public

### PUB-BOOT-01 · Host entry point
**Use when** creating `Program.cs`. Every concern is one extension call, so the file
stays readable as the service grows.
**Needs** `Microsoft.NET.Sdk.Web`

```csharp
using Service.Api.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApiLayer()
    .AddApplicationSettings(builder.Configuration)
    .AddPersistence(builder.Configuration)
    .AddDomainServices()
    .AddOutboundClients(builder.Configuration)
    .AddBackgroundWork(builder.Configuration, builder.Environment)
    .AddObservability(builder.Configuration);

WebApplication app = builder.Build();

app.UseRequestPipeline();

await app.RunAsync();
```

---

### PUB-BOOT-02 · API layer registration
**Use when** registering MVC. `InvalidModelStateResponseFactory` is overridden so
validation failures use the same envelope as every other error.
**Needs** —

```csharp
/// <summary>Registers controllers, serialisation and API behaviour.</summary>
public static IServiceCollection AddApiLayer(this IServiceCollection services)
{
    services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    services.AddEndpointsApiExplorer();
    services.AddSwaggerGen();

    // One error envelope for validation failures and for exceptions alike.
    services.Configure<ApiBehaviorOptions>(options =>
        options.InvalidModelStateResponseFactory = context => new ValidationErrorResult(context.ModelState));

    services.AddProblemDetails();
    services.AddHttpContextAccessor();

    return services;
}
```

---

### PUB-BOOT-03 · Request pipeline order
**Use when** composing middleware. Exception handling outermost so it catches
everything downstream; authorization after routing; endpoints last.
**Needs** —

```csharp
/// <summary>Composes the HTTP request pipeline.</summary>
public static WebApplication UseRequestPipeline(this WebApplication app)
{
    app.UseMiddleware<ExceptionHandlingMiddleware>();  // outermost: catches all below
    app.UseMiddleware<CorrelationIdMiddleware>();      // before logging, so logs carry the id
    app.UseSerilogRequestLogging();

    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready");

    return app;
}
```

---

### PUB-BOOT-04 · Domain service registration
**Use when** registering the service layer. Lifetime is a decision, not a default —
comment it where it is not obvious.
**Needs** —

```csharp
/// <summary>Registers the domain services.</summary>
public static IServiceCollection AddDomainServices(this IServiceCollection services)
{
    // Scoped: per-request state (unit of work, rollback stack, current user).
    services.AddScoped<IRollbackService, RollbackService>();
    services.AddScoped<IWidgetOrderService, WidgetOrderService>();
    services.AddScoped<ITransactionService, TransactionService>();

    // Singleton: stateless or holds only counters read by the meter.
    services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
    services.AddSingleton<ServiceMetrics>();

    return services;
}
```

---

### PUB-BOOT-05 · Persistence registration
**Use when** wiring EF Core. Resilience and timeouts come from bound options, not
literals, so each environment can differ without a code change.
**Needs** `Microsoft.EntityFrameworkCore.SqlServer`

```csharp
/// <summary>Registers the database context and unit of work.</summary>
public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
{
    DatabaseOptions databaseOptions = configuration.GetRequiredSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
        ?? throw new InvalidOperationException($"Missing configuration section '{DatabaseOptions.SectionName}'.");

    services.AddDbContext<AppDbContext>(options =>
    {
        options.UseSqlServer(databaseOptions.ConnectionString, sql =>
        {
            sql.CommandTimeout(databaseOptions.CommandTimeoutSeconds);
            sql.EnableRetryOnFailure(databaseOptions.MaxRetryCount, TimeSpan.FromSeconds(databaseOptions.MaxRetryDelaySeconds), null);
        });

        // Off by default: these log parameter values.
        options.EnableSensitiveDataLogging(databaseOptions.EnableSensitiveDataLogging);
        options.EnableDetailedErrors(databaseOptions.EnableDetailedErrors);
    });

    services.AddScoped<IUnitOfWork, UnitOfWork>();

    return services;
}
```

---

### PUB-BOOT-06 · Environment-conditional registration
**Use when** a hosted service would interfere with integration tests. Registering it
as scoped keeps the type resolvable without the host starting it.
**Needs** —

```csharp
/// <summary>Registers background work, excluding hosted execution under test.</summary>
public static IServiceCollection AddBackgroundWork(
    this IServiceCollection services,
    IConfiguration configuration,
    IHostEnvironment environment)
{
    services.AddScoped<IFileImportService, FileImportService>();

    if (!configuration.GetValue<bool>("BackgroundWork:Enabled"))
        return services;

    if (environment.IsEnvironment("Testing"))
        services.AddScoped<ScheduleSyncService>();
    else
        services.AddHostedService<ScheduleSyncService>();

    return services;
}
```

---

### PUB-BOOT-07 · Assembly-scanned registration
**Use when** a family of types follows one naming convention and manual registration
would drift. Explicit lists are preferable below roughly ten types.
**Needs** —

```csharp
/// <summary>Registers every I{Name}Handler / {Name}Handler pair in the assembly.</summary>
public static IServiceCollection AddHandlers(this IServiceCollection services)
{
    var handlerTypes = typeof(IWidgetOrderService).Assembly
        .GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Handler", StringComparison.Ordinal));

    foreach (Type implementation in handlerTypes)
    {
        Type? contract = implementation.GetInterfaces()
            .FirstOrDefault(i => i.Name == $"I{implementation.Name}");

        if (contract is not null)
            services.AddScoped(contract, implementation);
    }

    return services;
}
```

---

### PUB-BOOT-08 · Web API project file
**Use when** creating the API project. Warnings-as-errors keeps analyzer findings
from accumulating.
**Needs** —

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FluentValidation.AspNetCore" Version="11.3.1" />
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="9.0.10" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="9.0.10" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.10.0" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
    <PackageReference Include="Serilog.AspNetCore" Version="9.0.0" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="9.0.6" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Service.Core\Service.Core.csproj" />
  </ItemGroup>
</Project>
```
