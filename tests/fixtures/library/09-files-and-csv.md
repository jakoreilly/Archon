# Files, CSV and SFTP — Public

### PUB-FILE-01 · CSV class map
**Use when** the incoming file has no usable header, or column order is the contract.
Index-based mapping with converters for nullable and derived columns.
**Needs** `CsvHelper`

```csharp
/// <summary>Maps the partner CSV layout onto the import entity.</summary>
private static ClassMap<ImportedRecord> CreateImportedRecordMap(IDateTimeProvider dateTimeProvider)
{
    var map = new DefaultClassMap<ImportedRecord>();

    // Blank numeric cell becomes null rather than a parse failure.
    map.Map(m => m.SourceId).Index(0).Convert(args =>
        string.IsNullOrWhiteSpace(args.Row.GetField(0)) ? null : long.Parse(args.Row.GetField(0)!, CultureInfo.InvariantCulture));

    map.Map(m => m.ExternalReference).Index(1);
    map.Map(m => m.OccurredAt).Index(2);
    map.Map(m => m.SiteId).Index(3);
    map.Map(m => m.GateId).Index(4);
    map.Map(m => m.NetAmount).Index(7);
    map.Map(m => m.GrossAmount).Index(10);
    map.Map(m => m.Code).Index(12);
    map.Map(m => m.SerialNumber).Index(13);

    // Columns not present in the file.
    map.Map(m => m.ImportedAt).Convert(_ => dateTimeProvider.UtcNow);
    map.Map(m => m.Status).Constant(ImportStatus.Ready);

    return map;
}
```

---

### PUB-FILE-02 · Tolerant CSV parse
**Use when** a malformed file must be recorded and skipped rather than abort the run.
`DetectColumnCountChanges` catches a wrong delimiter that would otherwise parse
silently into one column.
**Needs** `CsvHelper`

```csharp
private static List<ImportedRecord> ParseCsvFile(
    string filePath,
    ILogger logger,
    IDateTimeProvider dateTimeProvider,
    List<InvalidRecord> invalidRecords,
    ImportOptions options)
{
    var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        Delimiter = options.Delimiter,
        MissingFieldFound = null,   // trailing columns absent is acceptable
        HeaderValidated = null,     // header names are not the contract, indexes are
        DetectColumnCountChanges = true,
        HasHeaderRecord = true,
        TrimOptions = TrimOptions.Trim
    };

    try
    {
        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, csvConfig);

        csv.Context.RegisterClassMap(CreateImportedRecordMap(dateTimeProvider));

        // Materialised inside the using block: GetRecords streams lazily.
        return csv.GetRecords<ImportedRecord>().ToList();
    }
    catch (BadDataException ex)
    {
        logger.LogError(ex, "Delimiter error found in file {FilePath}.", filePath);

        invalidRecords.Add(new InvalidRecord
        {
            FileName = Path.GetFileName(filePath),
            Reason = $"Delimiter error detected. Expected '{options.Delimiter}' but found a different format.",
            OccurredAt = dateTimeProvider.UtcNow
        });

        return [];
    }
    catch (CsvHelperException ex)
    {
        logger.LogError(ex, "Error parsing file {FilePath}.", filePath);

        invalidRecords.Add(new InvalidRecord
        {
            FileName = Path.GetFileName(filePath),
            Reason = $"Parsing error: {ExtractParsingDetail(ex.Message)}",
            OccurredAt = dateTimeProvider.UtcNow
        });

        return [];
    }
}

/// <summary>Extracts the useful line from a CsvHelper message, dropping the dumped context.</summary>
private static string ExtractParsingDetail(string message)
{
    string firstLine = message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? message;
    const int MaxLength = 300;

    return firstLine.Length <= MaxLength ? firstLine : string.Concat(firstLine.AsSpan(0, MaxLength), "...");
}
```

---

### PUB-FILE-03 · Import directory scan
**Use when** picking up files dropped locally. A missing directory is an empty
result, not an exception.
**Needs** —

```csharp
private static List<string> GetFilesToImport(ImportOptions options) =>
    Directory.Exists(options.SourceDirectory)
        ? Directory.EnumerateFiles(options.SourceDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(file => file.EndsWith(options.FileExtension, StringComparison.OrdinalIgnoreCase))
            // Oldest first, so record ordering is preserved across files.
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToList()
        : [];
```

---

### PUB-FILE-04 · Per-file processing with guaranteed cleanup
**Use when** looping over files. One bad file logs and continues; the local copy is
always removed so a retry does not double-import.
**Needs** —

```csharp
private async Task ImportFilesAsync(
    List<string> files,
    List<InvalidRecord> invalidRecords,
    CancellationToken cancellationToken)
{
    foreach (string file in files)
    {
        string fileName = Path.GetFileName(file);

        try
        {
            // Idempotency: file name is the natural key for an import.
            bool alreadyImported = await unitOfWork.Query<ImportFile>()
                .AnyAsync(x => x.FileName == fileName, cancellationToken);

            if (alreadyImported)
            {
                logger.LogWarning("File {FileName} has already been imported. Skipping.", fileName);

                invalidRecords.Add(new InvalidRecord
                {
                    FileName = fileName,
                    Reason = "File has already been imported.",
                    OccurredAt = dateTimeProvider.UtcNow
                });

                continue;
            }

            List<ImportedRecord> records = ParseCsvFile(file, logger, dateTimeProvider, invalidRecords, options.Value);

            if (records.Count == 0)
            {
                bool hasErrors = invalidRecords.Any(r => r.FileName == fileName);

                logger.Log(
                    hasErrors ? LogLevel.Warning : LogLevel.Information,
                    hasErrors ? "File {FileName} has errors and was not imported." : "File {FileName} is empty.",
                    fileName);

                continue;
            }

            var importFile = new ImportFile
            {
                FileName = fileName,
                Status = ImportFileStatus.Completed,
                ImportedAt = dateTimeProvider.UtcNow
            };

            await ImportBatchAsync(importFile, records, cancellationToken);

            logger.LogInformation("Imported {RecordCount} record(s) from {FileName}.", records.Count, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while processing file {FileName}.", fileName);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                // Cleanup failure must not mask the outcome of the import itself.
                logger.LogError(ex, "An error occurred while deleting file {FileName}.", fileName);
            }
        }
    }
}
```

---

### PUB-FILE-05 · SFTP download and archive
**Use when** collecting files from a partner SFTP server. Files are archived on the
remote side after download so the next run does not see them again.
**Needs** `SSH.NET`

```csharp
/// <summary>Downloads files from SFTP and archives them remotely.</summary>
public sealed class SftpFileTransfer(
    ILogger<SftpFileTransfer> logger,
    IDateTimeProvider dateTimeProvider,
    IOptions<SftpOptions> sftpOptions,
    IOptions<ImportOptions> importOptions) : IFileTransfer
{
    private const string FingerprintPromptMarker = "SSH fingerprint:";

    private readonly SftpOptions _sftp = sftpOptions.Value;
    private readonly ImportOptions _import = importOptions.Value;

    public int Download()
    {
        logger.LogInformation("Starting file download from SFTP.");

        try
        {
            using SftpClient client = CreateClient();

            client.Connect();
            logger.LogInformation("Connected to the SFTP host.");

            List<ISftpFile> remoteFiles = GetRemoteFiles(client);
            logger.LogInformation("Found {FileCount} file(s) in the remote directory.", remoteFiles.Count);

            return DownloadAndArchive(client, remoteFiles);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading files from SFTP.");
            throw;
        }
    }

    private SftpClient CreateClient() =>
        new(new ConnectionInfo(_sftp.Host, _sftp.Port, _sftp.Username, GetAuthenticationMethods()));

    private List<ISftpFile> GetRemoteFiles(SftpClient client)
    {
        string remotePath = _sftp.RemotePath.TrimEnd('/');

        return client.ListDirectory(remotePath)
            .Where(file => !file.IsDirectory
                           && file.Name.EndsWith(_import.FileExtension, StringComparison.OrdinalIgnoreCase))
            // Oldest first, so record ordering is preserved across files.
            .OrderBy(file => file.LastWriteTime)
            .ToList();
    }

    private int DownloadAndArchive(SftpClient client, IEnumerable<ISftpFile> remoteFiles)
    {
        string remotePath = _sftp.RemotePath.TrimEnd('/');
        string archivePath = $"{remotePath}/{_sftp.ArchiveFolder}/{dateTimeProvider.UtcNow:yyyy-MM-dd}";
        int downloaded = 0;

        EnsureDirectories(client, remotePath, archivePath);

        foreach (string remoteFileName in remoteFiles.Select(f => f.Name))
        {
            string localPath = Path.Combine(_import.SourceDirectory, remoteFileName);

            // FileShare.None: fail rather than interleave with a concurrent run.
            using (var localStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                client.DownloadFile($"{remotePath}/{remoteFileName}", localStream);
            }

            string archiveFilePath = $"{archivePath}/{remoteFileName}";

            // Same name twice in one day: suffix rather than overwrite.
            if (client.Exists(archiveFilePath))
            {
                archiveFilePath = $"{archivePath}/{remoteFileName}.{dateTimeProvider.UtcNow:HHmmssfff}";
                logger.LogWarning("File {FileName} is already archived. Using {NewFileName}.", remoteFileName, Path.GetFileName(archiveFilePath));
            }

            // Rename only after the stream is closed, so a failure leaves the file for retry.
            client.RenameFile($"{remotePath}/{remoteFileName}", archiveFilePath);

            downloaded++;
            logger.LogInformation("Downloaded and archived {FileName}.", remoteFileName);
        }

        return downloaded;
    }

    private void EnsureDirectories(SftpClient client, string remotePath, string archivePath)
    {
        string archiveRoot = $"{remotePath}/{_sftp.ArchiveFolder}";

        if (!client.Exists(archiveRoot))
            client.CreateDirectory(archiveRoot);

        if (!client.Exists(archivePath))
            client.CreateDirectory(archivePath);

        if (!Directory.Exists(_import.SourceDirectory))
            Directory.CreateDirectory(_import.SourceDirectory);
    }
}
```

---

### PUB-FILE-06 · SFTP authentication and options
**Use when** building the connection. Private key preferred, password as fallback;
the fingerprint prompt is answered from configuration so the host stays verified.
**Needs** `SSH.NET`

```csharp
private AuthenticationMethod[] GetAuthenticationMethods()
{
    var methods = new List<AuthenticationMethod>();

    if (!string.IsNullOrWhiteSpace(_sftp.PrivateKey))
    {
        byte[] keyBytes = Encoding.ASCII.GetBytes(_sftp.PrivateKey);
        using var keyStream = new MemoryStream(keyBytes);

        var privateKeyFile = string.IsNullOrEmpty(_sftp.PrivateKeyPassphrase)
            ? new PrivateKeyFile(keyStream)
            : new PrivateKeyFile(keyStream, _sftp.PrivateKeyPassphrase);

        methods.Add(new PrivateKeyAuthenticationMethod(_sftp.Username, privateKeyFile));
    }
    else
    {
        methods.Add(new PasswordAuthenticationMethod(_sftp.Username, _sftp.Password));
    }

    // Some servers ask for the host fingerprint interactively.
    var interactive = new KeyboardInteractiveAuthenticationMethod(_sftp.Username);

    interactive.AuthenticationPrompt += (_, e) =>
    {
        foreach (AuthenticationPrompt prompt in e.Prompts.Where(p => p.Request.Contains(FingerprintPromptMarker, StringComparison.OrdinalIgnoreCase)))
            prompt.Response = _sftp.HostKeyFingerprint;
    };

    methods.Add(interactive);

    return [.. methods];
}
```

**Options shape** — every value is supplied per environment, never committed:

```csharp
/// <summary>SFTP connection settings.</summary>
public sealed class SftpOptions
{
    public const string SectionName = "Sftp";

    [Required]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 22;

    [Required]
    public string Username { get; init; } = string.Empty;

    /// <summary>Used only when <see cref="PrivateKey"/> is empty.</summary>
    public string? Password { get; init; }

    /// <summary>PEM-encoded private key, supplied from a secret store.</summary>
    public string? PrivateKey { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Expected host key fingerprint. Never leave this blank in a deployed environment.</summary>
    public string HostKeyFingerprint { get; init; } = string.Empty;

    [Required]
    public string RemotePath { get; init; } = string.Empty;

    public string ArchiveFolder { get; init; } = "Archive";
}
```
