using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

/// <summary>
/// Outbox durável de eventos de auditoria do Monitoramento (T17-D). A gravação é local e
/// independente do portal: captura, mídia e HLS nunca esperam o EventStore. O envio usa o
/// mesmo segredo do token serviço-a-serviço e o escopo estreito <c>events.write</c>.
/// </summary>
public sealed record PlatformEventOutboxOptions(
    bool Enabled, string Directory, string? PortalUrl, string? Secret, string Audience, string ResidenceId,
    int MaximumEvents, long MaximumBytes, TimeSpan MaximumAge, int BatchSize, TimeSpan PollInterval,
    int MaximumAttempts, TimeSpan HttpTimeout)
{
    public const string EnabledKey = "MONITORING_EVENT_OUTBOX_ENABLED";
    public const string DirectoryKey = "MONITORING_EVENT_OUTBOX_DIRECTORY";
    public const string PortalUrlKey = "MONITORING_EVENT_OUTBOX_PORTAL_URL";
    public const string SecretKey = "MONITORING_SERVICE_TOKEN_SECRET";
    public const string AudienceKey = "MONITORING_EVENT_TOKEN_AUDIENCE";
    public const string ResidenceKey = "PORTAL_RESIDENCE_ID";
    public const string MaximumEventsKey = "MONITORING_EVENT_OUTBOX_MAX_EVENTS";
    public const string MaximumBytesKey = "MONITORING_EVENT_OUTBOX_MAX_BYTES";
    public const string MaximumAgeDaysKey = "MONITORING_EVENT_OUTBOX_MAX_AGE_DAYS";
    public const string BatchSizeKey = "MONITORING_EVENT_OUTBOX_BATCH_SIZE";
    public const string PollSecondsKey = "MONITORING_EVENT_OUTBOX_POLL_SECONDS";
    public const string MaximumAttemptsKey = "MONITORING_EVENT_OUTBOX_MAX_ATTEMPTS";
    public const string TimeoutSecondsKey = "MONITORING_EVENT_OUTBOX_TIMEOUT_SECONDS";
    public const string Scope = "events.write";
    public const string Source = "monitoring";
    public const string Issuer = "home-ia-monitoring";

    public static PlatformEventOutboxOptions FromConfiguration(IConfiguration configuration,
        MonitoringOptions monitoring)
    {
        var enabled = false;
        if (configuration[EnabledKey] is { } rawEnabled && !bool.TryParse(rawEnabled, out enabled))
            throw new InvalidOperationException($"{EnabledKey} deve ser true ou false.");

        var directory = configuration[DirectoryKey]?.Trim() is { Length: > 0 } configuredDirectory
            ? configuredDirectory
            : Path.Combine(monitoring.SpoolDirectory, "events", "outbox");
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException($"{DirectoryKey} deve ser absoluto.");

        var portalUrl = configuration[PortalUrlKey]?.Trim();
        if (enabled)
        {
            if (portalUrl is null || !Uri.TryCreate(portalUrl, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException(
                    $"{PortalUrlKey} deve ser uma URL http(s) absoluta quando a outbox está ativa.");
            portalUrl = parsed.ToString().TrimEnd('/');
        }

        var secret = configuration[SecretKey]?.Trim();
        if (enabled && (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32))
            throw new InvalidOperationException(
                $"{SecretKey} deve ter ao menos 32 bytes quando a outbox está ativa.");

        var maximumEvents = 1_000;
        if (configuration[MaximumEventsKey] is { } rawEvents &&
            (!int.TryParse(rawEvents, out maximumEvents) || maximumEvents is < 1 or > 10_000))
            throw new InvalidOperationException($"{MaximumEventsKey} deve estar entre 1 e 10000.");

        var maximumBytes = 16_777_216L;
        if (configuration[MaximumBytesKey] is { } rawBytes &&
            (!long.TryParse(rawBytes, out maximumBytes) || maximumBytes is < 1_048_576 or > 1_073_741_824))
            throw new InvalidOperationException($"{MaximumBytesKey} deve estar entre 1048576 e 1073741824.");

        var maximumAgeDays = 7;
        if (configuration[MaximumAgeDaysKey] is { } rawAge &&
            (!int.TryParse(rawAge, out maximumAgeDays) || maximumAgeDays is < 1 or > 30))
            throw new InvalidOperationException($"{MaximumAgeDaysKey} deve estar entre 1 e 30.");

        var batchSize = 10;
        if (configuration[BatchSizeKey] is { } rawBatch &&
            (!int.TryParse(rawBatch, out batchSize) || batchSize is < 1 or > 50))
            throw new InvalidOperationException($"{BatchSizeKey} deve estar entre 1 e 50.");

        var pollSeconds = 5;
        if (configuration[PollSecondsKey] is { } rawPoll &&
            (!int.TryParse(rawPoll, out pollSeconds) || pollSeconds is < 1 or > 300))
            throw new InvalidOperationException($"{PollSecondsKey} deve estar entre 1 e 300.");

        var maximumAttempts = 5;
        if (configuration[MaximumAttemptsKey] is { } rawAttempts &&
            (!int.TryParse(rawAttempts, out maximumAttempts) || maximumAttempts is < 1 or > 20))
            throw new InvalidOperationException($"{MaximumAttemptsKey} deve estar entre 1 e 20.");

        var timeoutSeconds = 5;
        if (configuration[TimeoutSecondsKey] is { } rawTimeout &&
            (!int.TryParse(rawTimeout, out timeoutSeconds) || timeoutSeconds is < 1 or > 30))
            throw new InvalidOperationException($"{TimeoutSecondsKey} deve estar entre 1 e 30.");

        return new PlatformEventOutboxOptions(enabled, directory, portalUrl, secret,
            (configuration[AudienceKey] ?? "home-ia-portal").Trim(),
            (configuration[ResidenceKey] ?? configuration["MONITORING_RESIDENCE_ID"] ?? "home").Trim(),
            maximumEvents, maximumBytes, TimeSpan.FromDays(maximumAgeDays), batchSize,
            TimeSpan.FromSeconds(pollSeconds), maximumAttempts, TimeSpan.FromSeconds(timeoutSeconds));
    }
}

public sealed record PlatformEventOutboxEvent(
    string EventType, string Category, string Severity, string Outcome,
    string? ResourceType = null, string? ResourcePublicId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null, string? CorrelationId = null,
    string? CausationId = null, DateTimeOffset? OccurredAtUtc = null, string SourceVersion = "1.0");

public sealed record PlatformEventOutboxEnqueueResult(bool Accepted, bool Dropped, string EventId,
    string CorrelationId);

public sealed record PlatformEventOutboxStatus(bool Enabled, int Pending, long PendingBytes, long Sent,
    long Failed, long Dropped, long DeadLettered, DateTimeOffset? LastSuccessUtc, string? LastError);

internal sealed record PlatformEventOutboxItem(string EventId, DateTimeOffset EnqueuedAtUtc, int Attempts,
    PlatformEventOutboxEvent Event);

internal sealed record PlatformEventOutboxResource(string Type, string PublicId);

internal sealed record PlatformEventOutboxPayload(string SchemaVersion, string EventId, string EventType,
    string Category, string Severity, string Outcome, string OccurredAtUtc, string Source, string SourceVersion,
    string Emitter, PlatformEventOutboxResource? Resource, string CorrelationId, string? CausationId,
    IReadOnlyDictionary<string, object?>? Metadata);

internal enum PlatformEventOutboxSendResult
{
    Sent,
    Retry,
    DeadLettered
}

public sealed class PlatformEventOutbox : IDisposable
{
    private const int TokenLifetimeSeconds = 60;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlatformEventOutboxOptions options;
    private readonly TimeProvider timeProvider;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly SemaphoreSlim fileGate = new(1, 1);
    private readonly SemaphoreSlim runGate = new(1, 1);
    private long sent;
    private long failed;
    private long dropped;
    private long deadLettered;
    private DateTimeOffset? lastSuccessUtc;
    private string? lastError;

    public PlatformEventOutbox(PlatformEventOutboxOptions options, TimeProvider timeProvider,
        HttpClient? httpClient = null)
    {
        this.options = options;
        this.timeProvider = timeProvider;
        ownsHttpClient = httpClient is null;
        this.httpClient = httpClient ?? new HttpClient();
        this.httpClient.Timeout = options.HttpTimeout;
    }

    private string PendingDirectory => Path.Combine(options.Directory, "pending");
    private string DeadDirectory => Path.Combine(options.Directory, "dead");

    public async Task<PlatformEventOutboxEnqueueResult> EnqueueAsync(PlatformEventOutboxEvent value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var eventId = Guid.NewGuid().ToString("N");
        var correlationId = string.IsNullOrWhiteSpace(value.CorrelationId)
            ? Guid.NewGuid().ToString("N")
            : value.CorrelationId;
        if (!options.Enabled)
            return new PlatformEventOutboxEnqueueResult(false, false, eventId, correlationId);

        await fileGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(PendingDirectory);
            var occurredAt = value.OccurredAtUtc ?? timeProvider.GetUtcNow();
            var item = new PlatformEventOutboxItem(eventId, occurredAt, 0,
                value with { CorrelationId = correlationId, OccurredAtUtc = occurredAt });
            var name = $"{occurredAt.ToUnixTimeMilliseconds():D13}-{eventId}.json";
            var destination = Path.Combine(PendingDirectory, name);
            var temporary = destination + ".tmp";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions),
                cancellationToken);
            File.Move(temporary, destination);
            var removed = TrimUnderLock();
            return new PlatformEventOutboxEnqueueResult(true, removed > 0, eventId, correlationId);
        }
        finally
        {
            fileGate.Release();
        }
    }

    /// <summary>
    /// Envia um lote limitado, mais antigo primeiro. Falha de rede, 5xx, 408 e 429 mantêm o
    /// item pendente com tentativa contabilizada; rejeição definitiva ou excesso de tentativas
    /// move para <c>dead/</c> em vez de bloquear a captura. O envio acontece fora do lock de
    /// arquivos para que uma nova emissão não espere o portal.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return 0;
        await runGate.WaitAsync(cancellationToken);
        try
        {
            List<string> paths;
            await fileGate.WaitAsync(cancellationToken);
            try
            {
                if (!Directory.Exists(PendingDirectory)) return 0;
                paths = Directory.EnumerateFiles(PendingDirectory, "*.json")
                    .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                    .Take(options.BatchSize)
                    .ToList();
            }
            finally
            {
                fileGate.Release();
            }

            var now = timeProvider.GetUtcNow();
            var processed = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlatformEventOutboxItem? item;
                await fileGate.WaitAsync(cancellationToken);
                try
                {
                    item = await TryReadAsync(path, cancellationToken);
                }
                finally
                {
                    fileGate.Release();
                }

                if (item is null || now - item.EnqueuedAtUtc > options.MaximumAge)
                {
                    await MutateAsync(() => Delete(path), cancellationToken);
                    Interlocked.Increment(ref dropped);
                    continue;
                }

                processed++;
                var result = await SendAsync(item, path, cancellationToken);
                if (result == PlatformEventOutboxSendResult.Sent)
                {
                    await MutateAsync(() => Delete(path), cancellationToken);
                    Interlocked.Increment(ref sent);
                    lastSuccessUtc = timeProvider.GetUtcNow();
                    lastError = null;
                }
            }

            return processed;
        }
        finally
        {
            runGate.Release();
        }
    }

    public PlatformEventOutboxStatus GetStatus()
    {
        fileGate.Wait();
        try
        {
            var (pending, bytes) = CountPending();
            return new PlatformEventOutboxStatus(options.Enabled, pending, bytes, Interlocked.Read(ref sent),
                Interlocked.Read(ref failed), Interlocked.Read(ref dropped),
                Interlocked.Read(ref deadLettered), lastSuccessUtc, lastError);
        }
        finally
        {
            fileGate.Release();
        }
    }

    public void Dispose()
    {
        fileGate.Dispose();
        runGate.Dispose();
        if (ownsHttpClient) httpClient.Dispose();
    }

    private async Task<PlatformEventOutboxSendResult> SendAsync(PlatformEventOutboxItem item, string path,
        CancellationToken cancellationToken)
    {
        var token = IssueToken();
        if (token is null)
        {
            lastError = "outbox_secret_missing";
            Interlocked.Increment(ref failed);
            return PlatformEventOutboxSendResult.Retry;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"{options.PortalUrl}/api/v1/events"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", item.Event.CorrelationId);
        request.Content = new StringContent(JsonSerializer.Serialize(BuildPayload(item), JsonOptions),
            Encoding.UTF8, "application/json");
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return PlatformEventOutboxSendResult.Sent;
            var status = (int)response.StatusCode;
            if (status is not (408 or 429) && status < 500)
            {
                await DeadLetterAsync(path, $"http_{status}", cancellationToken);
                return PlatformEventOutboxSendResult.DeadLettered;
            }

            return await RetryAsync(path, item, $"http_{status}", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RetryAsync(path, item, exception.GetType().Name, cancellationToken);
        }
    }

    private async Task<PlatformEventOutboxSendResult> RetryAsync(string path, PlatformEventOutboxItem item,
        string reason, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref failed);
        lastError = reason;
        var attempts = item.Attempts + 1;
        if (attempts >= options.MaximumAttempts)
        {
            await DeadLetterAsync(path, reason, cancellationToken);
            return PlatformEventOutboxSendResult.DeadLettered;
        }

        var retried = item with { Attempts = attempts };
        var temporary = path + ".tmp";
        await fileGate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(retried, JsonOptions),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            fileGate.Release();
        }

        return PlatformEventOutboxSendResult.Retry;
    }

    private async Task DeadLetterAsync(string path, string reason, CancellationToken cancellationToken)
    {
        await fileGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DeadDirectory);
            File.Move(path, Path.Combine(DeadDirectory, Path.GetFileName(path)), overwrite: true);
        }
        finally
        {
            fileGate.Release();
        }

        Interlocked.Increment(ref deadLettered);
        lastError = reason;
    }

    private PlatformEventOutboxPayload BuildPayload(PlatformEventOutboxItem item) => new(
        "1.0", item.EventId, item.Event.EventType, item.Event.Category, item.Event.Severity, item.Event.Outcome,
        item.Event.OccurredAtUtc!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        PlatformEventOutboxOptions.Source, item.Event.SourceVersion, "service",
        item.Event.ResourceType is not null && item.Event.ResourcePublicId is not null
            ? new PlatformEventOutboxResource(item.Event.ResourceType, item.Event.ResourcePublicId)
            : null,
        item.Event.CorrelationId!, item.Event.CausationId, item.Event.Metadata);

    private string? IssueToken()
    {
        if (string.IsNullOrWhiteSpace(options.Secret) || Encoding.UTF8.GetByteCount(options.Secret) < 32)
            return null;
        var now = timeProvider.GetUtcNow();
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = PlatformEventOutboxOptions.Issuer,
            aud = options.Audience,
            sub = PlatformEventOutboxOptions.Source,
            residence_id = options.ResidenceId,
            scope = PlatformEventOutboxOptions.Scope,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddSeconds(TokenLifetimeSeconds).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        })));
        var signature = Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Secret),
            Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }

    private int TrimUnderLock()
    {
        var paths = Directory.EnumerateFiles(PendingDirectory, "*.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();
        long bytes = 0;
        foreach (var path in paths) bytes += new FileInfo(path).Length;
        var removed = 0;
        foreach (var path in paths)
        {
            if (paths.Count - removed <= options.MaximumEvents && bytes <= options.MaximumBytes) break;
            var length = new FileInfo(path).Length;
            Delete(path);
            bytes -= length;
            removed++;
            Interlocked.Increment(ref dropped);
        }

        return removed;
    }

    private (int Count, long Bytes) CountPending()
    {
        if (!Directory.Exists(PendingDirectory)) return (0, 0);
        var count = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(PendingDirectory, "*.json"))
        {
            count++;
            bytes += new FileInfo(path).Length;
        }

        return (count, bytes);
    }

    private async Task MutateAsync(Action action, CancellationToken cancellationToken)
    {
        await fileGate.WaitAsync(cancellationToken);
        try
        {
            action();
        }
        finally
        {
            fileGate.Release();
        }
    }

    private static async Task<PlatformEventOutboxItem?> TryReadAsync(string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return JsonSerializer.Deserialize<PlatformEventOutboxItem>(
                await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // O próximo ciclo reconcilia; a profundidade permanece observável no status.
        }
    }
}

public sealed class PlatformEventOutboxWorker(PlatformEventOutbox outbox, PlatformEventOutboxOptions options)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await outbox.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // A próxima varredura tenta de novo; profundidade e falhas ficam no status.
            }

            try
            {
                await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
