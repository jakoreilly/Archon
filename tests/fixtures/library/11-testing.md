# Testing — Public

Integration-first: tests drive the real HTTP pipeline against an in-memory host and
a real database, with only outbound dependencies mocked. Four cooperating pieces:
**options** (what the world looks like), **builder** (fluent setup), **fixture**
(host plus mocks plus seeded data), **generators** (Bogus data).

### PUB-TEST-01 · Scenario options
**Use when** creating a test project. Everything a test can vary about the world
lives here.
**Needs** —

```csharp
/// <summary>Describes the world a single test runs against.</summary>
public sealed class ScenarioOptions
{
    public string Environment { get; init; } = "Testing";

    public int NumberOfOrders { get; set; }

    public int LinesPerOrder { get; set; }

    // Per-entity mutators let a test bend one field of generated data.
    public Action<WidgetOrder>[]? OrderMutators { get; set; }

    public Action<Location>? LocationMutator { get; set; }

    /// <summary>Extra registrations applied after the host's own.</summary>
    public List<Action<IServiceCollection>> ServiceOverrides { get; } = [];

    public Dictionary<string, string?> ConfigurationOverrides { get; } = [];

    public Dictionary<string, string> RequestHeaders { get; } = [];

    public MockHttpMessageHandler? FulfilmentHandler { get; set; }

    public DateTime? FrozenTime { get; set; }
}
```

---

### PUB-TEST-02 · Scenario builder
**Use when** adding a new setup dimension. Every method returns `this`; `Build`
returns the options.
**Needs** `Bogus`

```csharp
/// <summary>Fluent setup for a test scenario.</summary>
public sealed class ScenarioBuilder
{
    private readonly ScenarioOptions _options = new();

    public ScenarioOptions Build() => _options;

    public ScenarioBuilder WithOrders(int numberOfOrders, int linesPerOrder, params Action<WidgetOrder>[] mutators)
    {
        _options.NumberOfOrders = numberOfOrders;
        _options.LinesPerOrder = linesPerOrder;
        _options.OrderMutators = mutators;
        return this;
    }

    public ScenarioBuilder WithOrder(int linesPerOrder, Action<WidgetOrder>? mutator = null) =>
        WithOrders(1, linesPerOrder, mutator ?? (_ => { }));

    public ScenarioBuilder WithFulfilmentHandler(MockHttpMessageHandler handler)
    {
        _options.FulfilmentHandler = handler;
        return this;
    }

    public ScenarioBuilder WithService(Action<IServiceCollection> configure)
    {
        _options.ServiceOverrides.Add(configure);
        return this;
    }

    public ScenarioBuilder WithConfiguration(string key, string? value)
    {
        _options.ConfigurationOverrides[key] = value;
        return this;
    }

    // Freezing the clock makes any assertion on a timestamp deterministic.
    public ScenarioBuilder WithFrozenTime(DateTime utcNow)
    {
        _options.FrozenTime = utcNow;
        return this;
    }

    public ScenarioBuilder WithAuthenticatedUser(int customerId)
    {
        _options.RequestHeaders[HeaderNames.Authorization] = $"Bearer {TestTokenFactory.Create(customerId)}";
        return this;
    }
}
```

---

### PUB-TEST-03 · Scenario fixture
**Use when** creating a test project. Mock defaults live here so an individual test
only overrides what it is actually asserting.
**Needs** `Microsoft.AspNetCore.Mvc.Testing`, `Moq`, `RichardSzalay.MockHttp`

```csharp
/// <summary>Hosts the API in memory with a per-test database and mocked dependencies.</summary>
public sealed class Scenario : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ScenarioOptions _options;

    public HttpClient Client { get; }

    public IServiceScope Scope { get; }

    public IList<WidgetOrder> Orders { get; } = [];

    public WidgetOrder Order => Orders.First();

    public Mock<ICustomerClient> CustomerClientMock { get; } = new();

    public Mock<IEmailService> EmailServiceMock { get; } = new();

    public Mock<ITemplateProvider> TemplateProviderMock { get; } = new();

    public AppDbContext DbContext => Scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public IUnitOfWork UnitOfWork => Scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

    private Scenario(ScenarioOptions options)
    {
        _options = options;

        // Unique database name per scenario: tests can run in parallel without interference.
        string databaseName = $"test-{Guid.NewGuid():N}";

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(options.Environment);

            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(options.ConfigurationOverrides));

            builder.ConfigureServices(services =>
            {
                ReplaceDatabase(services, databaseName);
                RegisterMocks(services);

                // Test-specific registrations last, so they win.
                foreach (Action<IServiceCollection> configure in options.ServiceOverrides)
                    configure(services);
            });
        });

        Client = _factory.CreateClient();

        foreach ((string key, string value) in options.RequestHeaders)
            Client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);

        Scope = _factory.Services.CreateScope();
    }

    public static async Task<Scenario> StartAsync(ScenarioOptions options)
    {
        var scenario = new Scenario(options);
        await scenario.SeedAsync();
        return scenario;
    }

    public Task<HttpResponseMessage> PostAsync<T>(string uri, T body) => Client.PostAsJsonAsync(uri, body);

    public Task<HttpResponseMessage> PutAsync<T>(string uri, T body) => Client.PutAsJsonAsync(uri, body);

    public async ValueTask DisposeAsync()
    {
        Scope.Dispose();
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    private static void ReplaceDatabase(IServiceCollection services, string databaseName)
    {
        // Remove the real registration before adding the test one.
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<AppDbContext>();

        // Substitute your provider of choice: SQLite in-memory, Testcontainers, LocalDB.
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"DataSource={databaseName};Mode=Memory;Cache=Shared"));
    }

    private void RegisterMocks(IServiceCollection services)
    {
        // Default happy-path handler unless the test supplied its own.
        MockHttpMessageHandler handler = _options.FulfilmentHandler ?? CreateDefaultFulfilmentHandler();

        services.RemoveAll<IFulfilmentClient>();
        services.AddHttpClient<IFulfilmentClient, FulfilmentClient>(client => client.BaseAddress = new Uri("http://fulfilment.test"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        // Template stub containing the tokens the email service replaces.
        TemplateProviderMock
            .Setup(x => x.GetTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{{FullName}} {{Reference}}");

        services.AddScoped(_ => CustomerClientMock.Object);
        services.AddScoped(_ => EmailServiceMock.Object);
        services.AddScoped(_ => TemplateProviderMock.Object);

        if (_options.FrozenTime.HasValue)
            services.AddSingleton<IDateTimeProvider>(new FrozenDateTimeProvider(_options.FrozenTime.Value));
    }

    private static MockHttpMessageHandler CreateDefaultFulfilmentHandler()
    {
        var handler = new MockHttpMessageHandler();

        handler.When(HttpMethod.Post, "*/orders")
            .Respond(HttpStatusCode.OK, JsonContent.Create(new ExternalOrderResponse
            {
                ResultCode = ExternalResultCode.Success,
                OrderReference = Guid.NewGuid().ToString()
            }));

        return handler;
    }

    private async Task SeedAsync()
    {
        AppDbContext dbContext = DbContext;
        await dbContext.Database.EnsureCreatedAsync();

        var faker = new Faker();

        for (int i = 0; i < _options.NumberOfOrders; i++)
        {
            Action<WidgetOrder>? mutator = _options.OrderMutators?.Length > i ? _options.OrderMutators[i] : null;

            WidgetOrder order = faker.GenerateWidgetOrder(_options.LinesPerOrder, mutator);
            dbContext.WidgetOrders.Add(order);
            Orders.Add(order);
        }

        dbContext.Add(faker.GenerateLocation(_options.LocationMutator));

        await dbContext.SaveChangesAsync();

        // Detach everything: assertions must read from the database, not the seed graph.
        dbContext.ChangeTracker.Clear();
    }
}
```

---

### PUB-TEST-04 · Shared assertions
**Use when** creating a test project. One helper per response shape keeps tests to
arrange, act, one assert line.
**Needs** `NUnit`

```csharp
/// <summary>Assertions shared by every test class.</summary>
public abstract class TestBase
{
    protected static Faker Faker => new();

    protected static async Task<T> AssertOkAsync<T>(HttpResponseMessage response) =>
        await AssertStatusAsync<T>(response, HttpStatusCode.OK);

    protected static async Task<T> AssertCreatedAsync<T>(HttpResponseMessage response) =>
        await AssertStatusAsync<T>(response, HttpStatusCode.Created);

    protected static void AssertNoContent(HttpResponseMessage response) =>
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

    // Domain failure: single message plus error code.
    protected static async Task AssertBadRequestAsync(HttpResponseMessage response, int errorCode, string message)
    {
        ErrorResponse body = await AssertStatusAsync<ErrorResponse>(response, HttpStatusCode.BadRequest);
        Assert.That(body.ErrorCode, Is.EqualTo(errorCode));
        Assert.That(body.Message, Is.EqualTo(message));
    }

    // Model validation failure: per-field messages.
    protected static async Task AssertValidationErrorAsync(HttpResponseMessage response, string field, string message)
    {
        ValidationErrorResponse body = await AssertStatusAsync<ValidationErrorResponse>(response, HttpStatusCode.BadRequest);
        Assert.That(body.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput));

        bool exists = body.Errors.Any(e => e.Field == field && e.Message == message);
        Assert.That(exists, Is.True, $"Field '{field}' with message '{message}' not found in: {JsonSerializer.Serialize(body.Errors)}");
    }

    protected static async Task AssertConflictAsync(HttpResponseMessage response, string message)
    {
        ErrorResponse body = await AssertStatusAsync<ErrorResponse>(response, HttpStatusCode.Conflict);
        Assert.That(body.ErrorCode, Is.EqualTo(ErrorCodes.Conflict));
        Assert.That(body.Message, Is.EqualTo(message));
    }

    protected static async Task AssertNotFoundAsync(HttpResponseMessage response, string message)
    {
        ErrorResponse body = await AssertStatusAsync<ErrorResponse>(response, HttpStatusCode.NotFound);
        Assert.That(body.Message, Is.EqualTo(message));
    }

    protected static async Task<T> AssertStatusAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        string content = await response.Content.ReadAsStringAsync();

        // Body in the failure message: a bare status mismatch is not diagnosable.
        Assert.That(response.StatusCode, Is.EqualTo(expected), $"Unexpected status. Body: {content}");

        return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
```

---

### PUB-TEST-05 · Bogus generator
**Use when** a test needs an entity or request. Extension on `Faker`, an optional
mutator as the last parameter, valid data by default.
**Needs** `Bogus`

```csharp
/// <summary>Generates widget order test data.</summary>
public static class WidgetOrderGenerator
{
    public static WidgetOrder GenerateWidgetOrder(this Faker faker, int linesPerOrder, Action<WidgetOrder>? mutator = null)
    {
        DateTime createdAt = faker.Date.RecentOffset().UtcDateTime;

        var order = new WidgetOrder
        {
            CustomerId = faker.Random.Int(1, 999_999),
            Reference = faker.Random.Guid().ToString(),
            ExternalReference = faker.Random.Guid().ToString(),
            Status = OrderStatus.Fulfilled,
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(faker.Random.Int(1, 120)),
            Lines = []
        };

        for (int i = 0; i < linesPerOrder; i++)
            order.Lines.Add(faker.GenerateWidgetOrderLine(order));

        // Mutator runs before dependent fields are reconciled.
        mutator?.Invoke(order);

        // Keep the graph internally consistent after the mutator.
        if (order.Status != OrderStatus.Fulfilled)
            order.ExternalReference = null;

        order.TotalAmount = order.Lines.Sum(l => l.Amount);

        return order;
    }

    public static CreateWidgetOrderRequest GenerateCreateOrderRequest(
        this Faker faker,
        int lineCount = 1,
        Action<CreateWidgetOrderRequest>? mutator = null)
    {
        var request = new CreateWidgetOrderRequest
        {
            Lines = Enumerable.Range(0, lineCount)
                .Select(_ => new WidgetLineRequest
                {
                    WidgetCode = faker.Random.AlphaNumeric(8).ToUpperInvariant(),
                    Quantity = faker.Random.Int(1, 10),
                    Label = faker.Commerce.ProductName()
                })
                .ToList(),
            DeliveryAddress = new DeliveryAddressRequest
            {
                Line1 = faker.Address.StreetAddress(),
                Line2 = faker.Address.SecondaryAddress(),
                PostalCode = faker.Address.ZipCode(),
                CountryCode = faker.Address.CountryCode()
            }
        };

        mutator?.Invoke(request);
        return request;
    }
}

/// <summary>Returns a fixed time, so timestamp assertions are deterministic.</summary>
public sealed class FrozenDateTimeProvider(DateTime utcNow) : IDateTimeProvider
{
    public DateTime UtcNow { get; } = utcNow;
}
```

---

### PUB-TEST-06 · Test class and method shape
**Use when** writing tests. Name is `Method_WhenCondition_ExpectedResult`.
`ParallelScope.All` requires that no test touches shared static state.
**Needs** `NUnit`

```csharp
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class WidgetOrdersControllerTests : TestBase
{
    private const string OrdersUri = "/api/v1/widget-orders";

    [Test]
    public async Task Post_WhenLinesAreEmpty_ReturnsBadRequest()
    {
        await using Scenario scenario = await Scenario.StartAsync(
            new ScenarioBuilder().WithAuthenticatedUser(customerId: 1).Build());

        CreateWidgetOrderRequest request = Faker.GenerateCreateOrderRequest(0, r => r.Lines = []);

        HttpResponseMessage response = await scenario.PostAsync(OrdersUri, request);

        await AssertValidationErrorAsync(response, "Lines", "The Lines list must contain at least 1 items.");
    }

    [Test]
    public async Task Post_WhenInputIsValid_CreatesOrderAndReturnsReference()
    {
        await using Scenario scenario = await Scenario.StartAsync(
            new ScenarioBuilder().WithAuthenticatedUser(customerId: 42).Build());

        CreateWidgetOrderRequest request = Faker.GenerateCreateOrderRequest(3);

        HttpResponseMessage response = await scenario.PostAsync(OrdersUri, request);

        CreateWidgetOrderResponse body = await AssertCreatedAsync<CreateWidgetOrderResponse>(response);

        // Read back from the database, not from the response, to prove it persisted.
        WidgetOrder? persisted = await scenario.UnitOfWork.Query<WidgetOrder>(x => x.Reference == body.Reference)
            .AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefaultAsync();

        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.Lines, Has.Count.EqualTo(3));
        Assert.That(persisted.CustomerId, Is.EqualTo(42));
        scenario.EmailServiceMock.VerifyOrderConfirmationSent(body.Reference);
    }

    // TestCase for input variants of the same behaviour.
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task Post_WhenFulfilmentProviderFails_ReturnsBadGatewayAndDeclinesOrder(HttpStatusCode providerStatus)
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, "*/orders").Respond(providerStatus);

        await using Scenario scenario = await Scenario.StartAsync(
            new ScenarioBuilder()
                .WithAuthenticatedUser(customerId: 1)
                .WithFulfilmentHandler(handler)
                .Build());

        HttpResponseMessage response = await scenario.PostAsync(OrdersUri, Faker.GenerateCreateOrderRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));

        // Assert the rollback actually ran, not just the status code.
        List<WidgetOrder> declined = await scenario.UnitOfWork.Query<WidgetOrder>(x => x.Status == OrderStatus.Declined)
            .AsNoTracking()
            .ToListAsync();

        Assert.That(declined, Has.Count.EqualTo(1));
        Assert.That(declined[0].ExternalReference, Is.Null);
        scenario.EmailServiceMock.VerifyNothingSent();
    }

    // Replacing a client outright, when asserting the exact outbound request.
    [Test]
    public async Task Post_WhenInputIsValid_SendsExpectedRequestToProvider()
    {
        var fulfilmentClient = new Mock<IFulfilmentClient>();
        CreateWidgetOrderCommand? captured = null;

        fulfilmentClient
            .Setup(x => x.SubmitOrderAsync(It.IsAny<CreateWidgetOrderCommand>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<CreateWidgetOrderCommand, string, CancellationToken>((command, _, _) => captured = command)
            .ReturnsAsync(Guid.NewGuid().ToString());

        await using Scenario scenario = await Scenario.StartAsync(
            new ScenarioBuilder()
                .WithAuthenticatedUser(customerId: 1)
                .WithService(s => s.AddScoped(_ => fulfilmentClient.Object))
                .Build());

        CreateWidgetOrderRequest request = Faker.GenerateCreateOrderRequest(1, r => r.DeliveryAddress!.Line2 = null);

        HttpResponseMessage response = await scenario.PostAsync(OrdersUri, request);

        await AssertCreatedAsync<CreateWidgetOrderResponse>(response);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DeliveryAddress.Line1, Is.EqualTo(request.DeliveryAddress!.Line1));

        // Optional inputs are sent as empty strings, not nulls.
        Assert.That(captured.DeliveryAddress.Line2, Is.EqualTo(string.Empty));

        // Codes are normalised at the boundary.
        Assert.That(captured.Lines[0].WidgetCode, Is.EqualTo(request.Lines[0].WidgetCode.ToUpperInvariant()));
    }
}
```

---

### PUB-TEST-07 · Mock verification extension
**Use when** the same verification appears in more than two tests. Keeps the argument
matcher in one place.
**Needs** `Moq`

```csharp
/// <summary>Verification helpers for the email service mock.</summary>
public static class EmailServiceMockExtensions
{
    public static void VerifyOrderConfirmationSent(this Mock<IEmailService> mock, string reference, int times = 1) =>
        mock.Verify(
            x => x.SendAsync(
                It.Is<EmailMessage>(m => m.HtmlBody.Contains(reference, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Exactly(times));

    public static void VerifyNothingSent(this Mock<IEmailService> mock) =>
        mock.Verify(
            x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

    public static void SetupSendFailure(this Mock<IEmailService> mock) =>
        mock.Setup(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP unavailable."));
}
```

---

### PUB-TEST-08 · Malformed input file fixture
**Use when** testing an import path. Writes real files so the parser is exercised end
to end.
**Needs** `Bogus`

```csharp
/// <summary>Writes deliberately malformed CSV files into a scratch directory.</summary>
public static class MalformedCsvFixture
{
    private const string Header = "SourceId;ExternalRef;OccurredAt;SiteId;GateId;Direction;Name;NetAmount;TaxAmount;TaxRate;GrossAmount;ClassId;Code;SerialNumber";

    /// <summary>Row generators, each producing one kind of defect.</summary>
    public static readonly IReadOnlyList<Func<Faker, string>> ErrorGenerators =
    [
        // Wrong delimiter.
        faker => $"{faker.Random.Long(1, 9999)},REF,{faker.Date.Recent():s},1,1,N,Name,1.00,0.20,20,1.20,1,CODE,SERIAL",

        // Non-numeric amount.
        faker => $"{faker.Random.Long(1, 9999)};REF;{faker.Date.Recent():s};1;1;N;Name;not-a-number;0.20;20;1.20;1;CODE;SERIAL",

        // Too few columns.
        faker => $"{faker.Random.Long(1, 9999)};REF;{faker.Date.Recent():s}",

        // Unterminated quote.
        faker => $"{faker.Random.Long(1, 9999)};\"REF;{faker.Date.Recent():s};1;1;N;Name;1.00;0.20;20;1.20;1;CODE;SERIAL"
    ];

    public static List<string> Write(string directory, int numberOfFiles)
    {
        // Clean slate so a previous run cannot influence this one.
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        Directory.CreateDirectory(directory);

        var faker = new Faker();
        var fileNames = new List<string>();

        for (int i = 0; i < numberOfFiles; i++)
        {
            string fileName = $"malformed_{i + 1}.csv";
            string content = $"{Header}{Environment.NewLine}{faker.PickRandom(ErrorGenerators)(faker)}";

            File.WriteAllText(Path.Combine(directory, fileName), content);
            fileNames.Add(fileName);
        }

        return fileNames;
    }
}
```

---

### PUB-TEST-09 · Test project file
**Use when** creating the test project. `InternalsVisibleTo` is unnecessary if the
test targets the public surface, which it should.
**Needs** —

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Bogus" Version="35.6.1" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="9.0.10" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.10" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="Moq" Version="4.20.72" />
    <PackageReference Include="NUnit" Version="4.6.1" />
    <PackageReference Include="NUnit3TestAdapter" Version="5.2.0" />
    <PackageReference Include="RichardSzalay.MockHttp" Version="7.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Service.Api\Service.Api.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.Testing.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```
