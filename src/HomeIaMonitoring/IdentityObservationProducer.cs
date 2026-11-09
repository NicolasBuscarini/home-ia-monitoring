using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

/// <summary>
/// Producer de observação facial (T09). A extração real ainda não existe: a fonte
/// <c>synthetic</c> permite exercitar contrato, fila, retry e entrega sem modelo ou mídia real.
/// Embeddings nunca são gravados em disco; perda na reinicialização é aceitável e observável.
/// </summary>
public sealed record IdentityObservationOptions(
    bool Enabled, string Source, string? PortalUrl, string? Secret, string Audience, string ResidenceId,
    int MinimumIntervalSeconds, int QueueCapacity, int MaximumAttempts, TimeSpan HttpTimeout, int ScanSeconds)
{
    public const string EnabledKey = "IDENTITY_OBSERVATION_PRODUCER_ENABLED";
    public const string SourceKey = "IDENTITY_OBSERVATION_PRODUCER_SOURCE";
    public const string PortalUrlKey = "IDENTITY_OBSERVATION_PRODUCER_PORTAL_URL";
    public const string SecretKey = "MONITORING_SERVICE_TOKEN_SECRET";
    public const string AudienceKey = "MONITORING_IDENTITY_TOKEN_AUDIENCE";
    public const string ResidenceKey = "PORTAL_RESIDENCE_ID";
    public const string MinimumIntervalKey = "IDENTITY_OBSERVATION_PRODUCER_MIN_INTERVAL_SECONDS";
    public const string QueueCapacityKey = "IDENTITY_OBSERVATION_PRODUCER_QUEUE_CAPACITY";
    public const string MaximumAttemptsKey = "IDENTITY_OBSERVATION_PRODUCER_MAX_ATTEMPTS";
    public const string TimeoutSecondsKey = "IDENTITY_OBSERVATION_PRODUCER_TIMEOUT_SECONDS";
    public const string ScanSecondsKey = "IDENTITY_OBSERVATION_PRODUCER_SCAN_SECONDS";
    public const string DisabledSource = "disabled";
    public const string SyntheticSource = "synthetic";
    public const string OnnxSource = "onnx-yunet-sface";
    public const string Scope = "identity.observations.write";
    public const string TokenSubject = "monitoring";
    public const string Issuer = "home-ia-monitoring";

    public static IdentityObservationOptions FromConfiguration(IConfiguration configuration)
    {
        var enabled = false;
        if (configuration[EnabledKey] is { } rawEnabled && !bool.TryParse(rawEnabled, out enabled))
            throw new InvalidOperationException($"{EnabledKey} deve ser true ou false.");

        var source = (configuration[SourceKey] ?? DisabledSource).Trim().ToLowerInvariant();
        if (source is not (DisabledSource or SyntheticSource or OnnxSource))
            throw new InvalidOperationException(
                $"{SourceKey} deve ser {DisabledSource}, {SyntheticSource} ou {OnnxSource}.");
        if (enabled && source == DisabledSource)
            throw new InvalidOperationException($"{SourceKey} não pode ser {DisabledSource} com o producer ativo.");

        var portalUrl = configuration[PortalUrlKey]?.Trim();
        if (enabled)
        {
            if (portalUrl is null || !Uri.TryCreate(portalUrl, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException(
                    $"{PortalUrlKey} deve ser uma URL http(s) absoluta quando o producer está ativo.");
            portalUrl = parsed.ToString().TrimEnd('/');
        }

        var secret = configuration[SecretKey]?.Trim();
        if (enabled && (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32))
            throw new InvalidOperationException(
                $"{SecretKey} deve ter ao menos 32 bytes quando o producer está ativo.");

        var minimumInterval = ReadInt(configuration, MinimumIntervalKey, 60, 5, 3_600);
        var queueCapacity = ReadInt(configuration, QueueCapacityKey, 64, 1, 1_024);
        var maximumAttempts = ReadInt(configuration, MaximumAttemptsKey, 3, 1, 10);
        var timeoutSeconds = ReadInt(configuration, TimeoutSecondsKey, 5, 1, 30);
        var scanSeconds = ReadInt(configuration, ScanSecondsKey, 30, 5, 3_600);

        return new IdentityObservationOptions(enabled, source, portalUrl, secret,
            (configuration[AudienceKey] ?? "home-ia-portal").Trim(),
            (configuration[ResidenceKey] ?? configuration["MONITORING_RESIDENCE_ID"] ?? "home").Trim(),
            minimumInterval, queueCapacity, maximumAttempts, TimeSpan.FromSeconds(timeoutSeconds), scanSeconds);
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback, int minimum, int maximum)
    {
        if (configuration[key] is not { } raw) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{key} deve estar entre {minimum} e {maximum}.");
        return value;
    }
}

public sealed record ReadyClip(string PublicId, string CameraId, DateTimeOffset EndedAtUtc, TimeSpan? Duration = null);

public sealed record FaceObservation(
    string SchemaVersion, string ModelId, string ModelVersion, string EmbeddingSchemaVersion, int Dimensions,
    IReadOnlyList<float> Values, string? CameraPublicId, string? ClipPublicId, DateTimeOffset? ObservedAtUtc);

public interface IFaceObservationSource
{
    string Name { get; }
    Task<FaceObservation?> TryCreateAsync(ReadyClip clip, CancellationToken cancellationToken);
}

public sealed class DisabledFaceObservationSource : IFaceObservationSource
{
    public string Name => IdentityObservationOptions.DisabledSource;

    public Task<FaceObservation?> TryCreateAsync(ReadyClip clip, CancellationToken cancellationToken) =>
        Task.FromResult<FaceObservation?>(null);
}

/// <summary>
/// Fonte determinística para desenvolvimento e testes: mesmo clipe gera o mesmo embedding.
/// Não representa qualidade, precisão ou comportamento de um modelo facial real.
/// </summary>
public sealed class SyntheticFaceObservationSource : IFaceObservationSource
{
    public const string ModelId = "synthetic-face";
    private const int Dimensions = 128;

    public string Name => IdentityObservationOptions.SyntheticSource;

    public Task<FaceObservation?> TryCreateAsync(ReadyClip clip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var values = new float[Dimensions];
        for (var block = 0; block < Dimensions / 8; block++)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{clip.PublicId}|{block}"));
            for (var index = 0; index < 8; index++)
            {
                var raw = BinaryPrimitives.ReadInt32LittleEndian(hash.AsSpan(index * 4, 4));
                values[block * 8 + index] = MathF.Round(raw / (float)int.MaxValue, 6);
            }
        }

        return Task.FromResult<FaceObservation?>(new FaceObservation("face-observation.v1", ModelId, "1",
            "face-embedding.v1", Dimensions, values, null, clip.PublicId, clip.EndedAtUtc));
    }
}

public sealed record IdentityObservationStatus(bool Enabled, string Source, int Pending, int Capacity,
    long Observed, long Enqueued, long Dropped, long Throttled, long Skipped, long Delivered, long Failed,
    long Retried, int Seen, int ScanOffset, long Sweeps, DateTimeOffset? LastSuccessUtc, string? LastError);

public static class IdentityObservationMetrics
{
    public static string Render(IdentityObservationStatus status) => string.Join('\n',
        "# HELP home_ia_identity_observations_up Producer de observação facial ativo (1) ou desativado (0).",
        "# TYPE home_ia_identity_observations_up gauge",
        $"home_ia_identity_observations_up {(status.Enabled ? 1 : 0)}",
        "# TYPE home_ia_identity_observations_pending gauge",
        $"home_ia_identity_observations_pending {status.Pending}",
        "# TYPE home_ia_identity_observations_observed_total counter",
        $"home_ia_identity_observations_observed_total {status.Observed}",
        "# TYPE home_ia_identity_observations_dropped_total counter",
        $"home_ia_identity_observations_dropped_total {status.Dropped}",
        "# TYPE home_ia_identity_observations_throttled_total counter",
        $"home_ia_identity_observations_throttled_total {status.Throttled}",
        "# TYPE home_ia_identity_observations_skipped_total counter",
        $"home_ia_identity_observations_skipped_total {status.Skipped}",
        "# TYPE home_ia_identity_observations_delivered_total counter",
        $"home_ia_identity_observations_delivered_total {status.Delivered}",
        "# TYPE home_ia_identity_observations_failed_total counter",
        $"home_ia_identity_observations_failed_total {status.Failed}",
        "# TYPE home_ia_identity_observations_retried_total counter",
        $"home_ia_identity_observations_retried_total {status.Retried}",
        "# TYPE home_ia_identity_observations_seen gauge",
        $"home_ia_identity_observations_seen {status.Seen}",
        "# TYPE home_ia_identity_observations_scan_offset gauge",
        $"home_ia_identity_observations_scan_offset {status.ScanOffset}",
        "# TYPE home_ia_identity_observations_sweeps_total counter",
        $"home_ia_identity_observations_sweeps_total {status.Sweeps}",
        string.Empty);
}

/// <summary>
/// Fila limitada em memória com deduplicação por clipe e limite de taxa por câmera. O envio
/// usa o token serviço-a-serviço com o escopo estreito <c>identity.observations.write</c> e
/// nunca bloqueia captura, spool ou HLS.
/// </summary>
public sealed class IdentityObservationProducer : IDisposable
{
    private const int TokenLifetimeSeconds = 60;
    private static readonly TimeSpan SeenRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan SeenPruneInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IdentityObservationOptions options;
    private readonly IClipPlaybackSource clips;
    private readonly IFaceObservationSource source;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<IdentityObservationProducer> logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly Channel<ReadyClip> queue;
    private readonly IdentityObservationScanStateStore? stateStore;
    private readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> failedAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastByCamera = new(StringComparer.Ordinal);
    private int scanOffset;
    private long sweeps;
    private DateTimeOffset? lastSeenPruneUtc;
    private long observed;
    private long enqueued;
    private long dropped;
    private long throttled;
    private long skipped;
    private long delivered;
    private long failed;
    private long retried;
    private DateTimeOffset? lastSuccessUtc;
    private string? lastError;

    public IdentityObservationProducer(IdentityObservationOptions options, IClipPlaybackSource clips,
        IFaceObservationSource source, TimeProvider timeProvider, ILogger<IdentityObservationProducer> logger,
        HttpClient? httpClient = null, IdentityObservationScanStateStore? stateStore = null)
    {
        this.options = options;
        this.clips = clips;
        this.source = source;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.stateStore = stateStore;
        ownsHttpClient = httpClient is null;
        this.httpClient = httpClient ?? new HttpClient();
        this.httpClient.Timeout = options.HttpTimeout;
        queue = Channel.CreateBounded<ReadyClip>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var persisted = stateStore?.Load();
        if (persisted is not null)
        {
            scanOffset = persisted.ScanOffset;
            sweeps = persisted.Sweeps;
            foreach (var pair in persisted.Seen) seen[pair.Key] = pair.Value;
            foreach (var pair in persisted.FailedAttempts) failedAttempts[pair.Key] = pair.Value;
            logger.LogInformation(
                "Estado de varredura de observação facial retomado: offset={Offset}, ciclos={Sweeps}, vistos={Seen}.",
                scanOffset, sweeps, seen.Count);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return 0;
        var now = timeProvider.GetUtcNow();
        PruneSeen(now);
        var free = Math.Max(0, options.QueueCapacity - queue.Reader.Count);
        if (free > 0)
        {
            var page = await clips.ListReadyPageAsync(free, scanOffset, cancellationToken);
            if (page.HasMore)
            {
                scanOffset = page.NextOffset;
            }
            else
            {
                scanOffset = 0;
                Interlocked.Increment(ref sweeps);
            }

            var accepted = 0;
            foreach (var clip in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (accepted >= free) break;
                if (seen.TryGetValue(clip.PublicId, out var seenAt))
                {
                    if (now - seenAt < SeenRetention) continue;
                    seen.Remove(clip.PublicId);
                    failedAttempts.Remove(clip.PublicId);
                }

                if (!queue.Writer.TryWrite(new ReadyClip(clip.PublicId, clip.CameraId, clip.EndedAtUtc,
                        clip.EndedAtUtc - clip.StartedAtUtc)))
                {
                    Interlocked.Increment(ref dropped);
                    logger.LogWarning("Fila de observação de identidade cheia; clipe {ClipPublicId} descartado.",
                        clip.PublicId);
                    continue;
                }

                MarkSeen(clip.PublicId, now);
                Interlocked.Increment(ref observed);
                Interlocked.Increment(ref enqueued);
                accepted++;
            }
        }

        // Persiste cursor/seen logo após a leitura da página: a extração de frames pode levar
        // minutos por ciclo e um restart no meio não deve perder a página já enfileirada.
        stateStore?.Save(CaptureState());

        var processed = 0;
        List<ReadyClip>? deferred = null;
        while (processed < options.QueueCapacity && queue.Reader.TryRead(out var clip))
        {
            processed++;
            var outcome = await DeliverAsync(clip, cancellationToken);
            if (outcome == DeliveryOutcome.Throttled)
                (deferred ??= []).Add(clip);
            else if (outcome == DeliveryOutcome.Failed)
                NoteFailure(clip);
            else
                failedAttempts.Remove(clip.PublicId);
        }

        if (deferred is not null)
        {
            foreach (var clip in deferred)
            {
                if (!queue.Writer.TryWrite(clip))
                {
                    Interlocked.Increment(ref dropped);
                    logger.LogWarning(
                        "Fila cheia ao reenfileirar clipe {ClipPublicId}; nova tentativa no próximo ciclo.",
                        clip.PublicId);
                }
            }
        }

        stateStore?.Save(CaptureState());
        return processed;
    }

    private IdentityObservationScanState CaptureState() =>
        new(scanOffset, Interlocked.Read(ref sweeps),
            new Dictionary<string, DateTimeOffset>(seen, StringComparer.Ordinal),
            new Dictionary<string, int>(failedAttempts, StringComparer.Ordinal));

    public IdentityObservationStatus GetStatus() => new(options.Enabled, options.Source,
        queue.Reader.Count, options.QueueCapacity, Interlocked.Read(ref observed),
        Interlocked.Read(ref enqueued), Interlocked.Read(ref dropped), Interlocked.Read(ref throttled),
        Interlocked.Read(ref skipped), Interlocked.Read(ref delivered), Interlocked.Read(ref failed),
        Interlocked.Read(ref retried), seen.Count, scanOffset, Interlocked.Read(ref sweeps),
        lastSuccessUtc, lastError);

    public void Dispose()
    {
        stateStore?.Save(CaptureState());
        if (ownsHttpClient) httpClient.Dispose();
    }

    private async Task<DeliveryOutcome> DeliverAsync(ReadyClip clip, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (lastByCamera.TryGetValue(clip.CameraId, out var last) &&
            now - last < TimeSpan.FromSeconds(options.MinimumIntervalSeconds))
        {
            Interlocked.Increment(ref throttled);
            return DeliveryOutcome.Throttled;
        }

        FaceObservation? observation;
        try
        {
            observation = await source.TryCreateAsync(clip, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            lastError = exception.GetType().Name;
            Interlocked.Increment(ref failed);
            return DeliveryOutcome.Failed;
        }

        if (observation is null)
        {
            Interlocked.Increment(ref skipped);
            return DeliveryOutcome.Skipped;
        }

        lastByCamera[clip.CameraId] = now;
        for (var attempt = 1; ; attempt++)
        {
            var token = IssueToken();
            if (token is null)
            {
                lastError = "identity_token_unavailable";
                Interlocked.Increment(ref failed);
                return DeliveryOutcome.Failed;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{options.PortalUrl}/api/v1/monitoring/identity-observations")
                {
                    Content = new StringContent(JsonSerializer.Serialize(observation, JsonOptions),
                        Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref delivered);
                    lastSuccessUtc = timeProvider.GetUtcNow();
                    lastError = null;
                    return DeliveryOutcome.Delivered;
                }

                lastError = $"http_{(int)response.StatusCode}";
                if (IsRetryable(response.StatusCode) && attempt < options.MaximumAttempts)
                {
                    Interlocked.Increment(ref retried);
                    await Task.Delay(Backoff(attempt), cancellationToken);
                    continue;
                }

                Interlocked.Increment(ref failed);
                logger.LogWarning("Portal recusou observação de identidade do clipe {ClipPublicId}: {Status}.",
                    clip.PublicId, (int)response.StatusCode);
                return DeliveryOutcome.Rejected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name;
                if (attempt < options.MaximumAttempts)
                {
                    Interlocked.Increment(ref retried);
                    await Task.Delay(Backoff(attempt), cancellationToken);
                    continue;
                }

                Interlocked.Increment(ref failed);
                logger.LogWarning("Falha ao enviar observação de identidade do clipe {ClipPublicId}: {Error}.",
                    clip.PublicId, lastError);
                return DeliveryOutcome.Failed;
            }
        }
    }

    private void MarkSeen(string publicId, DateTimeOffset now) => seen[publicId] = now;

    /// <summary>
    /// Um clipe que falhou pode voltar numa varredura futura, mas no máximo
    /// <see cref="IdentityObservationOptions.MaximumAttempts"/> vezes. Ao esgotar, permanece em
    /// <c>seen</c> até a retenção expirar para não gerar laço quente.
    /// </summary>
    private void NoteFailure(ReadyClip clip)
    {
        var attempts = failedAttempts.TryGetValue(clip.PublicId, out var current) ? current + 1 : 1;
        failedAttempts[clip.PublicId] = attempts;
        if (attempts < options.MaximumAttempts) seen.Remove(clip.PublicId);
    }

    private void PruneSeen(DateTimeOffset now)
    {
        if (seen.Count == 0) return;
        if (seen.Count < 1_024 && lastSeenPruneUtc is { } last && now - last < SeenPruneInterval) return;
        lastSeenPruneUtc = now;
        foreach (var pair in seen.Where(item => now - item.Value >= SeenRetention).ToArray())
        {
            seen.Remove(pair.Key);
            failedAttempts.Remove(pair.Key);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private enum DeliveryOutcome
    {
        Delivered,
        Skipped,
        Throttled,
        Failed,
        Rejected
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(2_000, 200 * (1 << Math.Min(attempt - 1, 4))));

    private string? IssueToken()
    {
        if (string.IsNullOrWhiteSpace(options.Secret) || Encoding.UTF8.GetByteCount(options.Secret) < 32)
            return null;
        var now = timeProvider.GetUtcNow();
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = IdentityObservationOptions.Issuer,
            aud = options.Audience,
            sub = IdentityObservationOptions.TokenSubject,
            residence_id = options.ResidenceId,
            scope = IdentityObservationOptions.Scope,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddSeconds(TokenLifetimeSeconds).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        })));
        var signature = Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Secret),
            Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }
}

public sealed class IdentityObservationWorker(IdentityObservationProducer producer,
    IdentityObservationOptions options, ILogger<IdentityObservationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Producer de observação de identidade desativado por configuração.");
            return;
        }

        logger.LogInformation("Producer de observação de identidade ativo com fonte {Source}.", options.Source);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await producer.RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(options.ScanSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Falha no ciclo do producer de observação de identidade.");
                await Task.Delay(TimeSpan.FromSeconds(options.ScanSeconds), stoppingToken);
            }
        }
    }
}
