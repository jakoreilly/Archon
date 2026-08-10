# Data Access — Public

### PUB-DATA-01 · DbContext
**Use when** creating the data project. Configuration lives in `IEntityTypeConfiguration`
classes, discovered by assembly scan.
**Needs** `Microsoft.EntityFrameworkCore`

```csharp
/// <summary>The application database context.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<WidgetOrder> WidgetOrders => Set<WidgetOrder>();

    public DbSet<WidgetOrderLine> WidgetOrderLines => Set<WidgetOrderLine>();

    public DbSet<ImportedRecord> ImportedRecords => Set<ImportedRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
```

---

### PUB-DATA-02 · Entity type configuration
**Use when** mapping a table. One file per entity, `internal`, no attributes on the
entity itself.
**Needs** `Microsoft.EntityFrameworkCore`

```csharp
internal sealed class WidgetOrderConfiguration : IEntityTypeConfiguration<WidgetOrder>
{
    public void Configure(EntityTypeBuilder<WidgetOrder> builder)
    {
        const int ReferenceLength = 64;

        builder.ToTable("WidgetOrders");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Reference)
            .HasMaxLength(ReferenceLength)
            .IsRequired();

        // Natural key: unique index, not just a constraint in code.
        builder.HasIndex(x => x.Reference).IsUnique();

        // Frequent filter, so index the pair rather than the columns separately.
        builder.HasIndex(x => new { x.CustomerId, x.Status });

        // Enum persisted as int; keeps the column narrow and sortable.
        builder.Property(x => x.Status).HasConversion<int>();

        // Money needs explicit precision or EF falls back to a lossy default.
        builder.Property(x => x.TotalAmount).HasPrecision(18, 2);

        // UTC in, UTC out: guards against a DateTime arriving with the wrong Kind.
        builder.Property(x => x.CreatedAt)
            .HasConversion(
                v => v,
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        builder.HasMany(x => x.Lines)
            .WithOne(x => x.Order)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

---

### PUB-DATA-03 · Entity
**Use when** adding a table. No annotations — mapping lives in the configuration.
**Needs** —

```csharp
/// <summary>A widget order.</summary>
public sealed class WidgetOrder
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string? ExternalReference { get; set; }

    public OrderStatus Status { get; set; }

    public decimal TotalAmount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public ICollection<WidgetOrderLine> Lines { get; set; } = [];
}
```

---

### PUB-DATA-04 · Unit of work
**Use when** the service layer needs data access without depending on `DbContext`
directly. Thin wrapper — the queryable is still EF Core, so all LINQ works.
**Needs** `Microsoft.EntityFrameworkCore`

```csharp
/// <summary>Data access boundary for the service layer.</summary>
public interface IUnitOfWork
{
    IQueryable<T> Query<T>()
        where T : class;

    IQueryable<T> Query<T>(Expression<Func<T, bool>> predicate)
        where T : class;

    Task AddAsync<T>(T entity, CancellationToken cancellationToken = default)
        where T : class;

    Task AddRangeAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default)
        where T : class;

    Task<int> SaveAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> FromSqlAsync<T>(string sql, object[] parameters, CancellationToken cancellationToken = default);

    Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default);
}

/// <summary>EF Core implementation of the data access boundary.</summary>
public sealed class UnitOfWork(AppDbContext dbContext) : IUnitOfWork
{
    // AsNoTracking by default: reads are the common case and tracking is opt-in.
    public IQueryable<T> Query<T>()
        where T : class => dbContext.Set<T>();

    public IQueryable<T> Query<T>(Expression<Func<T, bool>> predicate)
        where T : class => dbContext.Set<T>().Where(predicate);

    public async Task AddAsync<T>(T entity, CancellationToken cancellationToken = default)
        where T : class => await dbContext.Set<T>().AddAsync(entity, cancellationToken);

    public async Task AddRangeAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default)
        where T : class => await dbContext.Set<T>().AddRangeAsync(entities, cancellationToken);

    public Task<int> SaveAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<T>> FromSqlAsync<T>(
        string sql,
        object[] parameters,
        CancellationToken cancellationToken = default)
    {
        // FormattableStringFactory keeps SqlQuery parameterised — never interpolate values.
        return await dbContext.Database
            .SqlQuery<T>(FormattableStringFactory.Create(sql, parameters))
            .ToListAsync(cancellationToken);
    }

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        // Execution strategy owns the transaction so a retry replays the whole block.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            T result = await operation();

            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }
}
```

---

### PUB-DATA-05 · Projection query
**Use when** reading data. Project in the query so only needed columns leave the
database and nothing is tracked unnecessarily.
**Needs** `Microsoft.EntityFrameworkCore`

```csharp
public Task<List<WidgetOrderSummary>> GetSummariesAsync(int customerId, CancellationToken cancellationToken) =>
    unitOfWork.Query<WidgetOrder>(x => x.CustomerId == customerId)
        .AsNoTracking()
        .Select(x => new WidgetOrderSummary
        {
            Reference = x.Reference,
            Status = x.Status,
            LineCount = x.Lines.Count,
            CreatedAt = x.CreatedAt
        })
        .ToListAsync(cancellationToken);

// Distinct scalar list, filtered on a client-supplied set.
public Task<List<string>> GetActiveWidgetCodesAsync(IReadOnlyCollection<string> widgetCodes, CancellationToken cancellationToken) =>
    unitOfWork.Query<WidgetOrderLine>(x => widgetCodes.Contains(x.WidgetCode)
                                           && x.Status != LineStatus.Cancelled
                                           && x.Status != LineStatus.Deleted)
        .AsNoTracking()
        .Select(x => x.WidgetCode)
        .Distinct()
        .ToListAsync(cancellationToken);
```

---

### PUB-DATA-06 · Set-based update
**Use when** changing rows without needing them in memory. One statement, no change
tracking, no read-modify-write race.
**Needs** EF Core 7+

```csharp
// Simple field update.
await unitOfWork.Query<WidgetOrder>()
    .Where(x => x.Id == orderId)
    .ExecuteUpdateAsync(set => set
        .SetProperty(o => o.Status, OrderStatus.Cancelled)
        .SetProperty(o => o.UpdatedAt, dateTimeProvider.UtcNow), cancellationToken);

// Update computed from the existing row value.
await unitOfWork.Query<Transaction>()
    .Where(x => x.Id == transactionId)
    .ExecuteUpdateAsync(set => set
        .SetProperty(t => t.Status, newStatus)
        .SetProperty(t => t.RetryCount, t => succeeded ? t.RetryCount : (t.RetryCount ?? 0) + 1)
        .SetProperty(t => t.UpdatedAt, dateTimeProvider.UtcNow), cancellationToken);

// Conditional update evaluated server-side across many rows.
int affectedRows = await unitOfWork.Query<ImportedRecord>()
    .Where(r => r.Status == ImportStatus.Ready)
    .ExecuteUpdateAsync(set => set
        .SetProperty(r => r.Status, r =>
            r.SourceId == null || string.IsNullOrWhiteSpace(r.Code)
                ? ImportStatus.Invalid
                : r.Status)
        .SetProperty(r => r.ErrorDetail, r =>
            r.SourceId == null ? ImportErrors.SourceIdMissing :
            string.IsNullOrWhiteSpace(r.Code) ? ImportErrors.CodeMissing : string.Empty), cancellationToken);
```

---

### PUB-DATA-07 · Set-based delete
**Use when** removing rows by predicate. Delete children before parents unless
cascade is configured.
**Needs** EF Core 7+

```csharp
public async Task DeleteCustomerDataAsync(int customerId, CancellationToken cancellationToken)
{
    // Order matters: foreign keys are enforced.
    await unitOfWork.Query<Transaction>(x => x.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
    await unitOfWork.Query<WidgetOrderLine>(x => x.Order.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
    await unitOfWork.Query<WidgetOrder>(x => x.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
}
```

---

### PUB-DATA-08 · Paged query
**Use when** an endpoint returns a list. Order by a unique column so paging is
stable; count and page in one round trip where the provider supports it.
**Needs** `Microsoft.EntityFrameworkCore`

```csharp
public async Task<PagedResponse<AuditModel>> GetAuditsAsync(GetAuditsRequest request, CancellationToken cancellationToken)
{
    DateTime from = request.StartDate!.Value.ToDateTime(TimeOnly.MinValue);
    DateTime toExclusive = request.EndDate!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);

    IQueryable<Audit> query = unitOfWork.Query<Audit>()
        .AsNoTracking()
        .Where(x => x.CreatedAt >= from && x.CreatedAt < toExclusive);

    // Optional filters compose onto the queryable before it is materialised.
    if (request.UserId.HasValue)
        query = query.Where(x => x.UserId == request.UserId.Value);

    int totalCount = await query.CountAsync(cancellationToken);

    List<AuditModel> items = await query
        // Unique column: without it, rows can repeat or vanish across pages.
        .OrderByDescending(x => x.Id)
        .Skip((request.PageNumber - 1) * request.PageSize)
        .Take(request.PageSize)
        .Select(x => new AuditModel
        {
            Action = x.Action,
            EntityName = x.EntityName,
            Message = x.Message,
            CreatedAt = x.CreatedAt
        })
        .ToListAsync(cancellationToken);

    return new PagedResponse<AuditModel>
    {
        Items = items,
        PageNumber = request.PageNumber,
        PageSize = request.PageSize,
        TotalCount = totalCount
    };
}
```

---

### PUB-DATA-09 · Left join with composite keys
**Use when** finding rows whose related record is missing or invalid. Anonymous
objects give the composite key; `DefaultIfEmpty` makes it a left join.
**Needs** `Microsoft.EntityFrameworkCore`

```csharp
private Task<List<InvalidRecord>> GetUnmatchedRecordsAsync(ImportStatus status, CancellationToken cancellationToken) =>
    (from record in unitOfWork.Query<ImportedRecord>()
     join location in unitOfWork.Query<Location>()
         on new { record.SiteId, record.GateId }
         equals new { SiteId = (int?)location.SiteId, GateId = (int?)location.GateId }
         into locationJoin
     from location in locationJoin.DefaultIfEmpty()
     join asset in unitOfWork.Query<Asset>() on record.SerialNumber equals asset.SerialNumber into assetJoin
     from asset in assetJoin.DefaultIfEmpty()
     where record.Status == status
         && string.IsNullOrWhiteSpace(record.ErrorDetail)
         && (location == null || asset == null || location.ProductId == 0)
     orderby record.Id
     select new InvalidRecord
     {
         RecordId = record.Id,
         FileName = record.ImportFile.FileName,

         // Null on the joined side identifies which lookup failed.
         SiteId = location == null ? record.SiteId : null,
         SerialNumber = record.SerialNumber,
         OccurredAt = record.OccurredAt
     })
    .ToListAsync(cancellationToken);
```

---

### PUB-DATA-10 · Cached lookup repository
**Use when** reading reference data that changes rarely. Expiry comes from
configuration, never a literal.
**Needs** `Microsoft.Extensions.Caching.Memory`

```csharp
/// <summary>Reads reference data through an in-memory cache.</summary>
public sealed class LookupRepository(
    ILogger<LookupRepository> logger,
    IMemoryCache cache,
    IUnitOfWork unitOfWork,
    IOptions<CacheOptions> cacheOptions) : ILookupRepository
{
    private const string AllCategoriesKey = "lookup:categories:all";

    public Task<IReadOnlyList<CategoryModel>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(AllCategoriesKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(cacheOptions.Value.LookupExpiryHours);
            return await LoadCategoriesAsync(cancellationToken);
        })!;

    private async Task<IReadOnlyList<CategoryModel>> LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Cache miss, loading categories from the database.");

        List<CategoryModel> categories = await unitOfWork.Query<Category>()
            .AsNoTracking()
            .Select(c => new CategoryModel { Id = c.Id, Description = c.Description })
            .ToListAsync(cancellationToken);

        logger.LogInformation("Loaded {CategoryCount} categories from the database.", categories.Count);
        return categories;
    }
}
```

---

### PUB-DATA-11 · Claim one row for exclusive processing
**Use when** several workers drain the same queue table. `UPDATE … OUTPUT` claims and
returns the row in a single atomic statement, so no two workers get the same row.
**Needs** SQL Server; `FromSqlAsync` from `PUB-DATA-04`

```csharp
private const string ClaimNextSql = """
    SET ROWCOUNT 1;
    UPDATE Transactions WITH (ROWLOCK, READPAST)
    SET Status = @inProgressStatus,
        UpdatedAt = @processStartedAt
    OUTPUT Inserted.Id           AS TransactionId,
           Inserted.CustomerId,
           Inserted.Amount,
           Inserted.OccurredAt,
           Inserted.Status,
           Inserted.RetryCount
    WHERE Status = @readyStatus
      AND (UpdatedAt IS NULL OR UpdatedAt <= @processStartedAt)
      AND (RetryCount IS NULL OR RetryCount < @maxRetries);
    """;

private async Task<TransactionModel?> ClaimNextTransactionAsync(
    DateTime processStartedAt,
    int maxRetries,
    CancellationToken cancellationToken)
{
    object[] parameters =
    [
        new SqlParameter("@inProgressStatus", (int)TransactionStatus.InProgress),
        new SqlParameter("@readyStatus", (int)TransactionStatus.Ready),
        new SqlParameter("@processStartedAt", processStartedAt),
        new SqlParameter("@maxRetries", maxRetries)
    ];

    IReadOnlyList<TransactionModel> claimed = await unitOfWork
        .FromSqlAsync<TransactionModel>(ClaimNextSql, parameters, cancellationToken);

    return claimed.FirstOrDefault();
}

// Drain loop: processStartedAt is captured once so rows this run touched are not re-selected.
public async Task ProcessAllAsync(CancellationToken cancellationToken)
{
    DateTime processStartedAt = dateTimeProvider.UtcNow;

    while (!cancellationToken.IsCancellationRequested)
    {
        TransactionModel? transaction = await ClaimNextTransactionAsync(processStartedAt, options.MaxRetries, cancellationToken);
        if (transaction is null)
            break;

        await ProcessOneAsync(transaction, cancellationToken);
    }
}
```

---

### PUB-DATA-12 · Bulk insert
**Use when** inserting more than a few hundred rows. Assign foreign keys after the
parent is saved. `AutoDetectChangesEnabled = false` removes the O(n²) tracking cost.
**Needs** `Microsoft.EntityFrameworkCore`; optionally `EFCore.BulkExtensions` or `SqlBulkCopy` above ~10 000 rows

```csharp
public async Task ImportBatchAsync(
    ImportFile importFile,
    List<ImportedRecord> records,
    CancellationToken cancellationToken)
{
    // Parent first, so the generated identity is available.
    await unitOfWork.AddAsync(importFile, cancellationToken);
    await unitOfWork.SaveAsync(cancellationToken);

    foreach (ImportedRecord record in records)
        record.ImportFileId = importFile.Id;

    bool autoDetectWasEnabled = dbContext.ChangeTracker.AutoDetectChangesEnabled;
    dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

    try
    {
        await unitOfWork.AddRangeAsync(records, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
    }
    finally
    {
        dbContext.ChangeTracker.AutoDetectChangesEnabled = autoDetectWasEnabled;
    }
}
```
