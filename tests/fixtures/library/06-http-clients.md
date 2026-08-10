# HTTP Clients — Public

### PUB-HTTP-01 · Typed client with resilience
**Use when** calling any HTTP dependency. Timeout and retry come from bound options;
the pipeline name appears in telemetry.
**Needs** `Microsoft.Extensions.Http.Resilience`

```csharp
/// <summary>Registers the outbound HTTP clients.</summary>
public static IServiceCollection AddOutboundClients(this IServiceCollection services, IConfiguration configuration)
{
    ExternalApiOptions apiOptions = configuration.GetRequiredOptions<ExternalApiOptions>(ExternalApiOptions.SectionName);

    services.AddHttpClient<IFulfilmentClient, FulfilmentClient>(client =>
        {
            client.BaseAddress = new Uri(apiOptions.BaseUrl);

            // Per-attempt timeout is set on the pipeline; this bounds the whole call.
            client.Timeout = TimeSpan.FromSeconds(apiOptions.Retry.RequestTimeoutSeconds * apiOptions.Retry.MaxAttempts);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add(HeaderNames.SourceApplication, ApplicationNames.Current);
        })
        .AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(apiOptions.Retry.RequestTimeoutSeconds);
            options.Retry.MaxRetryAttempts = apiOptions.Retry.MaxAttempts;
            options.Retry.Delay = TimeSpan.FromMilliseconds(apiOptions.Retry.BaseDelayMilliseconds);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;

            // Circuit breaker window must exceed twice the attempt timeout.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(apiOptions.Retry.RequestTimeoutSeconds * 2);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 10;
        });

    return services;
}
```

---

### PUB-HTTP-02 · Request helper
**Use when** every client needs the same serialise / send / deserialise / error
handling. One helper instead of that logic in each method.
**Needs** `System.Net.Http.Json`

```csharp
/// <summary>Sends JSON requests and maps failures to domain exceptions.</summary>
public static class HttpClientExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Sends a request and deserialises the response body.</summary>
    public static async Task<T> SendJsonAsync<T>(
        this HttpClient httpClient,
        HttpMethod method,
        string uri,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: SerializerOptions);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

        string content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Body may hold secrets or stack detail, so it goes to the exception, not the caller's response.
            throw new DependencyFailureException(
                $"Request to {method} {uri} failed with status {(int)response.StatusCode}. Body: {content}");
        }

        // 204 and empty bodies are success, not a deserialisation error.
        if (string.IsNullOrWhiteSpace(content))
            return default!;

        return JsonSerializer.Deserialize<T>(content, SerializerOptions)!;
    }

    /// <summary>Sends a request that returns no body.</summary>
    public static async Task SendJsonAsync(
        this HttpClient httpClient,
        HttpMethod method,
        string uri,
        object? body = null,
        CancellationToken cancellationToken = default) =>
        await httpClient.SendJsonAsync<object>(method, uri, body, cancellationToken);
}
```

---

### PUB-HTTP-03 · Client implementation
**Use when** writing the client class. It maps between the external contract and the
domain, logs the correlating identifier, and does nothing else.
**Needs** —

```csharp
/// <summary>Calls the fulfilment provider.</summary>
public sealed class FulfilmentClient(
    ILogger<FulfilmentClient> logger,
    HttpClient httpClient,
    IOptions<ExternalApiOptions> options) : IFulfilmentClient
{
    private readonly ExternalApiOptions _options = options.Value;

    public async Task<string> SubmitOrderAsync(
        CreateWidgetOrderCommand command,
        string reference,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Submitting order to the fulfilment provider. Reference {Reference}", reference);

        ExternalOrderResponse response = await httpClient.SendJsonAsync<ExternalOrderResponse>(
            HttpMethod.Post,
            _options.CreateOrderPath,
            ToExternalRequest(command, reference),
            cancellationToken);

        // Provider reports failure in the body, so translate before returning.
        EnsureSuccess(response);

        return response.OrderReference;
    }

    public Task<ExternalOrderDetails> GetOrderAsync(string reference, CancellationToken cancellationToken)
    {
        logger.LogInformation("Getting order details for reference {Reference}.", reference);

        // Path segment is caller-supplied, so encode it.
        return httpClient.SendJsonAsync<ExternalOrderDetails>(
            HttpMethod.Get,
            $"{_options.GetOrderPath}/{Uri.EscapeDataString(reference)}",
            cancellationToken: cancellationToken);
    }

    public Task DeleteOrderAsync(string reference, CancellationToken cancellationToken) =>
        httpClient.SendJsonAsync(
            HttpMethod.Delete,
            $"{_options.GetOrderPath}/{Uri.EscapeDataString(reference)}",
            cancellationToken: cancellationToken);
}
```

---

### PUB-HTTP-04 · Token-caching delegating handler
**Use when** a dependency needs a bearer token. The handler owns acquisition and
invalidation so no client method has to.
**Needs** `Microsoft.Extensions.Caching.Memory`

```csharp
/// <summary>Attaches a cached bearer token and evicts it on 401.</summary>
public sealed class BearerTokenHandler(
    ITokenProvider tokenProvider,
    IMemoryCache cache,
    ILogger<BearerTokenHandler> logger) : DelegatingHandler
{
    private const string TokenCacheKey = "external-api:access-token";

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string token = await GetTokenAsync(cancellationToken);

        // The message may be a retry, so replace rather than append.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        // Evict on 401 so the next attempt fetches a fresh token.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("Received 401 from the dependency; evicting the cached access token.");
            cache.Remove(TokenCacheKey);
        }

        return response;
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(TokenCacheKey, async entry =>
        {
            TokenResult result = await tokenProvider.AcquireAsync(cancellationToken);

            // Expire early so a token is never used in its last moments.
            entry.AbsoluteExpirationRelativeToNow = result.ExpiresIn - TimeSpan.FromMinutes(1);
            return result.AccessToken;
        }))!;
}
```

Registration — the auth handler goes inside the resilience handler so retries carry
a current token:

```csharp
services.AddTransient<BearerTokenHandler>();

services.AddHttpClient<IFulfilmentClient, FulfilmentClient>(ConfigureClient)
    .AddStandardResilienceHandler(ConfigureResilience)
    .AddHttpMessageHandler<BearerTokenHandler>();
```

---

### PUB-HTTP-05 · Forward the caller's identity
**Use when** the downstream service must act as the calling user. Resolve
`IHttpContextAccessor` from the provider overload — never capture it outside the
factory.
**Needs** —

```csharp
services.AddHttpClient<ICustomerClient, CustomerClient>((serviceProvider, client) =>
{
    IHttpContextAccessor accessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();

    client.BaseAddress = new Uri(customerApiOptions.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    // Header is read per resolve, so each scoped client carries its own request's token.
    string? authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();

    if (!string.IsNullOrWhiteSpace(authorization))
        client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderNames.Authorization, authorization);
});
```

For a background job with no inbound request, use a handler instead so the token is
fetched per call rather than captured at registration:

```csharp
/// <summary>Attaches the service's own credentials when no user context exists.</summary>
public sealed class ServiceIdentityHandler(ITokenProvider tokenProvider) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            TokenResult token = await tokenProvider.AcquireServiceTokenAsync(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
```

---

### PUB-HTTP-06 · Header and path constants
**Use when** a client calls more than one endpoint, or a custom header is used in
more than one place.
**Needs** —

```csharp
/// <summary>Custom header names used across services.</summary>
public static class HeaderNames
{
    public const string CorrelationId = "X-Correlation-Id";
    public const string SourceApplication = "X-Source-Application";
    public const string Authorization = "Authorization";
}

/// <summary>Application identifiers sent in outbound requests.</summary>
public static class ApplicationNames
{
    public const string Current = "WidgetService";
}
```

---

### PUB-HTTP-07 · Correlation id propagation
**Use when** requests span services. The id is accepted if present, generated if not,
put on the response, and forwarded outbound.
**Needs** —

```csharp
/// <summary>Ensures every request carries a correlation id.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Invokes the next delegate with a correlation id in scope.</summary>
    public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
    {
        string correlationId = context.Request.Headers[HeaderNames.CorrelationId].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString();

        context.Items[HeaderNames.CorrelationId] = correlationId;

        // Set before the response starts, otherwise the header is dropped.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderNames.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        // Every log entry inside this request carries the id.
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

/// <summary>Forwards the inbound correlation id on outbound calls.</summary>
public sealed class CorrelationIdForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext?.Items[HeaderNames.CorrelationId] is string correlationId)
            request.Headers.TryAddWithoutValidation(HeaderNames.CorrelationId, correlationId);

        return base.SendAsync(request, cancellationToken);
    }
}
```
