# Email and Templates — Public

### PUB-MAIL-01 · Email abstraction and registration
**Use when** the service sends mail. The interface keeps the transport swappable —
SMTP now, a hosted provider later — without touching callers.
**Needs** `MailKit`

```csharp
/// <summary>Sends email.</summary>
public interface IEmailService
{
    Task SendAsync(
        IReadOnlyCollection<string> recipients,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>An outbound email.</summary>
public sealed class EmailMessage
{
    public required IReadOnlyCollection<string> To { get; init; }

    public IReadOnlyCollection<string> Cc { get; init; } = [];

    public required string Subject { get; init; }

    public required string HtmlBody { get; init; }

    /// <summary>Overrides the configured default sender.</summary>
    public string? FromAddress { get; init; }

    public string? FromDisplayName { get; init; }
}

/// <summary>SMTP settings.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    [Required]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 25;

    public bool UseTls { get; init; } = true;

    public string? Username { get; init; }

    public string? Password { get; init; }

    [Required]
    [EmailAddress]
    public string DefaultFromAddress { get; init; } = string.Empty;

    public string DefaultFromDisplayName { get; init; } = string.Empty;
}

/// <summary>Registers the email service.</summary>
public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<SmtpOptions>()
        .Bind(configuration.GetSection(SmtpOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddScoped<IEmailService, SmtpEmailService>();

    return services;
}
```

---

### PUB-MAIL-02 · SMTP implementation
**Use when** sending over SMTP. The client is created per send: MailKit's
`SmtpClient` is not thread-safe and a pooled connection can go stale.
**Needs** `MailKit`

```csharp
/// <summary>Sends email over SMTP.</summary>
public sealed class SmtpEmailService(
    ILogger<SmtpEmailService> logger,
    IOptions<SmtpOptions> options) : IEmailService
{
    private readonly SmtpOptions _options = options.Value;

    public Task SendAsync(
        IReadOnlyCollection<string> recipients,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default) =>
        SendAsync(new EmailMessage { To = recipients, Subject = subject, HtmlBody = htmlBody }, cancellationToken);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        // Nothing to do, and an empty recipient list would throw inside MailKit.
        if (message.To.Count == 0)
        {
            logger.LogWarning("Send skipped: no recipients for subject {Subject}.", message.Subject);
            return;
        }

        var mimeMessage = new MimeMessage
        {
            Subject = message.Subject,
            Body = new BodyBuilder { HtmlBody = message.HtmlBody }.ToMessageBody()
        };

        mimeMessage.From.Add(new MailboxAddress(
            message.FromDisplayName ?? _options.DefaultFromDisplayName,
            message.FromAddress ?? _options.DefaultFromAddress));

        foreach (string recipient in message.To)
            mimeMessage.To.Add(MailboxAddress.Parse(recipient));

        foreach (string recipient in message.Cc)
            mimeMessage.Cc.Add(MailboxAddress.Parse(recipient));

        using var client = new SmtpClient();

        await client.ConnectAsync(
            _options.Host,
            _options.Port,
            _options.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(_options.Username))
            await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);

        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        // Subject only: recipient addresses are personal data.
        logger.LogInformation("Sent email with subject {Subject} to {RecipientCount} recipient(s).", message.Subject, message.To.Count);
    }
}
```

---

### PUB-MAIL-03 · Templated email with caching
**Use when** bodies come from HTML templates. Templates are cached for a configured
period; per-message tokens are applied through the optional action.
**Needs** `Microsoft.Extensions.Caching.Memory`

```csharp
/// <summary>Settings for one templated message type.</summary>
public sealed class TemplatedEmailOptions
{
    [Required]
    public string Subject { get; init; } = string.Empty;

    [Required]
    public string TemplatePath { get; init; } = string.Empty;

    public string? FromAddress { get; init; }
}

/// <summary>Loads a template from the configured store.</summary>
public interface ITemplateProvider
{
    Task<string> GetTemplateAsync(string templatePath, CancellationToken cancellationToken);
}

/// <summary>Sends templated notification emails.</summary>
public sealed class EmailNotificationService(
    IEmailService emailService,
    ITemplateProvider templateProvider,
    IMemoryCache cache,
    IOptionsSnapshot<TemplatedEmailOptions> templateOptions,
    IOptions<TemplateCacheOptions> cacheOptions) : IEmailNotificationService
{
    private const string TokenFullName = "{{FullName}}";
    private const string TokenReference = "{{Reference}}";

    public Task SendOrderConfirmationAsync(CustomerModel customer, string reference, CancellationToken cancellationToken)
    {
        TemplatedEmailOptions settings = templateOptions.Get("OrderConfirmation");

        return SendTemplatedAsync(
            customer,
            settings,
            body => body.Replace(TokenReference, reference),
            cancellationToken);
    }

    public Task SendOrderCancellationAsync(CustomerModel customer, CancellationToken cancellationToken)
    {
        TemplatedEmailOptions settings = templateOptions.Get("OrderCancellation");
        return SendTemplatedAsync(customer, settings, applyTokens: null, cancellationToken);
    }

    private async Task SendTemplatedAsync(
        CustomerModel customer,
        TemplatedEmailOptions settings,
        Action<StringBuilder>? applyTokens,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder(await GetCachedTemplateAsync(settings.TemplatePath, cancellationToken));

        // Tokens common to every template first, then message-specific ones.
        body.Replace(TokenFullName, $"{customer.FirstName} {customer.LastName}");
        applyTokens?.Invoke(body);

        await emailService.SendAsync(
            new EmailMessage
            {
                To = [customer.EmailAddress],
                Subject = settings.Subject,
                HtmlBody = body.ToString(),
                FromAddress = settings.FromAddress
            },
            cancellationToken);
    }

    private async Task<string> GetCachedTemplateAsync(string templatePath, CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync($"template:{templatePath}", async entry =>
        {
            // Expiry from configuration, so it can be shortened during a template rollout.
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(cacheOptions.Value.ExpiryHours);
            return await templateProvider.GetTemplateAsync(templatePath, cancellationToken);
        }))!;
}
```

---

### PUB-MAIL-04 · Operational alert email
**Use when** a batch job reports failures to a distribution list. Recipients come
from configuration; rows are built from the failures.
**Needs** —

```csharp
/// <summary>Settings for operational alerts.</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    [Required]
    [MinLength(1)]
    public IReadOnlyList<string> Recipients { get; init; } = [];

    [Required]
    public string Subject { get; init; } = string.Empty;
}

private const string TokenRows = "{{Rows}}";

private static string BuildRows(IEnumerable<InvalidRecord> records)
{
    var rows = new StringBuilder();

    foreach (InvalidRecord record in records)
    {
        // Encoded: reason text can originate from an external file.
        rows.Append("<tr>");
        rows.Append($"<td>{HtmlEncoder.Default.Encode(record.FileName)}</td>");
        rows.Append($"<td>{record.RecordId}</td>");
        rows.Append($"<td>{record.OccurredAt:u}</td>");
        rows.Append($"<td>{HtmlEncoder.Default.Encode(record.SerialNumber ?? string.Empty)}</td>");
        rows.Append($"<td>{HtmlEncoder.Default.Encode(record.Reason)}</td>");
        rows.Append("</tr>");
    }

    return rows.ToString();
}

private static async Task SendInvalidRecordsAlertAsync(
    IReadOnlyCollection<InvalidRecord> invalidRecords,
    string bodyTemplate,
    IEmailService emailService,
    AlertOptions alertOptions,
    ILogger logger,
    CancellationToken cancellationToken)
{
    // No failures, no mail — a nightly empty alert trains people to ignore it.
    if (invalidRecords.Count == 0)
    {
        logger.LogInformation("No invalid records to report.");
        return;
    }

    string body = bodyTemplate.Replace(TokenRows, BuildRows(invalidRecords));

    await emailService.SendAsync(alertOptions.Recipients, alertOptions.Subject, body, cancellationToken);

    logger.LogInformation("Sent an invalid records alert covering {InvalidRecordCount} row(s).", invalidRecords.Count);
}
```
