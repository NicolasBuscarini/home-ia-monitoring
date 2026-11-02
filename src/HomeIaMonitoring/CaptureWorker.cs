using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeIaMonitoring;

public sealed record CaptureStatus(string Adapter, string AdapterVersion,
    IReadOnlyList<string> Capabilities, string State, string? StreamEpoch, long Gaps, long Segments,
    string? LastErrorCode, long Revision);

public interface ICaptureAdapterFactory
{
    ISegmentCaptureAdapter Create(CaptureRuntimeSettings settings);
    ISegmentCaptureAdapter Describe();
}

public sealed class CaptureAdapterFactory(CaptureOptions options) : ICaptureAdapterFactory
{
    public ISegmentCaptureAdapter Create(CaptureRuntimeSettings settings)
    {
        var effective = options with
        {
            SegmentSeconds = settings.SegmentSeconds,
            MaximumSegments = settings.MaximumSegments,
            MaximumSegmentBytes = settings.MaximumSegmentBytes
        };
        return effective.Profile == "synthetic"
            ? new SyntheticSegmentCaptureAdapter(effective)
            : new FfmpegRtspCaptureAdapter(effective);
    }

    public ISegmentCaptureAdapter Describe() => Create(new CaptureRuntimeSettings(false, options.SegmentSeconds,
        options.MaximumSegments, options.MaximumSegmentBytes, 0));
}

public sealed class CaptureWorker(
    CaptureOptions options,
    ICaptureAdapterFactory adapterFactory,
    CaptureSettingsStore settingsStore,
    SpoolWriter writer,
    ILogger<CaptureWorker> logger) : BackgroundService
{
    private readonly object _gate = new();
    private CaptureStatus _status = InitialStatus(adapterFactory, options);

    public CaptureStatus Status { get { lock (_gate) return _status; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        var recovered = false;
        string? lastSuccessfulEpoch = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = settingsStore.Current;
            if (!settings.Enabled)
            {
                Update(status => status with { State = "disabled", StreamEpoch = null, LastErrorCode = null });
                recovered = false;
                try
                {
                    await settingsStore.WaitForChangeAsync(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                failures = 0;
                continue;
            }

            if (!recovered)
            {
                try
                {
                    var count = await writer.RecoverAsync(stoppingToken);
                    recovered = true;
                    Update(status => status with { Segments = count, Gaps = writer.CountGaps() });
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    Update(status => status with
                    {
                        State = "spool-unavailable",
                        LastErrorCode = "local_spool_unavailable"
                    });
                    logger.LogWarning("Spool indisponível; recuperação será repetida, motivo={FailureType}",
                        exception.GetType().Name);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    continue;
                }
            }

            var adapter = adapterFactory.Create(settings);
            var epoch = Guid.NewGuid().ToString();
            var workDirectory = Path.Combine(options.SpoolDirectory, "work", epoch);
            if (failures == 0)
                Update(status => status with { State = "connecting", LastErrorCode = null, Revision = settings.Revision });
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            void OnSettingsChanged()
            {
                if (settingsStore.Current.Revision != settings.Revision) attemptCancellation.Cancel();
            }
            settingsStore.Changed += OnSettingsChanged;
            try
            {
                await ConsumeAttemptAsync(adapter, settings, workDirectory, attemptCancellation, stoppingToken,
                    async segment =>
                    {
                        await using var source = File.OpenRead(segment.SourcePath);
                        var manifest = await writer.StoreAsync(source, segment.StartedAt, segment.EndedAt, epoch,
                            stoppingToken);
                        File.Delete(segment.SourcePath);
                        failures = 0;
                        lastSuccessfulEpoch = epoch;
                        Update(status => status with
                        {
                            State = "capturing",
                            StreamEpoch = epoch,
                            LastErrorCode = null,
                            Segments = ManifestCount(),
                            Revision = settings.Revision
                        });
                        logger.LogInformation("Segmento confirmado: {SegmentId}, {SizeBytes} bytes",
                            manifest.SegmentId, manifest.SizeBytes);
                        Update(status => status with { Segments = writer.EnforceRetention() });
                    });
                if (!stoppingToken.IsCancellationRequested && !attemptCancellation.IsCancellationRequested)
                    throw new InvalidOperationException("capture_stream_ended");
            }
            catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested &&
                                                     !stoppingToken.IsCancellationRequested)
            {
                // Configuration changed; loop re-reads the new settings.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                failures = Math.Min(failures + 1, 6);
                try
                {
                    await writer.RecordGapAsync(lastSuccessfulEpoch, failures, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception spoolException)
                {
                    Update(status => status with
                    {
                        State = "spool-unavailable",
                        LastErrorCode = "local_spool_unavailable"
                    });
                    logger.LogWarning("Gap não confirmado no spool; nova tentativa limitada, motivo={FailureType}",
                        spoolException.GetType().Name);
                }
                var errorCode = ErrorCode(exception);
                Update(status => status with
                {
                    State = "reconnecting",
                    StreamEpoch = null,
                    LastErrorCode = errorCode,
                    Gaps = writer.CountGaps(),
                    Revision = settings.Revision
                });
                logger.LogWarning("Fonte de captura indisponível; nova tentativa limitada, motivo={FailureType}",
                    exception.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << (failures - 1))), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
            finally
            {
                settingsStore.Changed -= OnSettingsChanged;
                TryDeleteDirectory(workDirectory);
            }
        }
        Update(status => status with { State = "stopped", StreamEpoch = null });
    }

    private async Task ConsumeAttemptAsync(ISegmentCaptureAdapter adapter,
        CaptureRuntimeSettings settings, string workDirectory, CancellationTokenSource attemptCancellation,
        CancellationToken stoppingToken, Func<CapturedSegment, Task> onSegment)
    {
        var timeoutSeconds = options.SegmentTimeoutSeconds > 0
            ? Math.Max(options.SegmentTimeoutSeconds, settings.SegmentSeconds + 5)
            : Math.Max(15, settings.SegmentSeconds * 3 + 5);
        await using var enumerator = adapter.CaptureAsync(workDirectory, attemptCancellation.Token)
            .GetAsyncEnumerator(attemptCancellation.Token);
        while (true)
        {
            var moveNext = enumerator.MoveNextAsync().AsTask();
            bool hasNext;
            try
            {
                hasNext = await moveNext.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds),
                    stoppingToken);
            }
            catch (TimeoutException)
            {
                attemptCancellation.Cancel();
                try { await moveNext; }
                catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested) { }
                throw new TimeoutException("capture_segment_timeout");
            }
            if (!hasNext) return;
            await onSegment(enumerator.Current);
        }
    }

    private int ManifestCount() => writer.ReadManifests().Count;

    private static string ErrorCode(Exception exception) => exception switch
    {
        TimeoutException { Message: "capture_segment_timeout" } => "capture_segment_timeout",
        InvalidDataException { Message: "capture_codec_unsupported" } => "capture_codec_unsupported",
        InvalidDataException { Message: "capture_audio_unsupported" } => "capture_audio_unsupported",
        InvalidDataException { Message: "capture_segment_size_limit" } => "capture_segment_size_limit",
        _ => "capture_source_unavailable"
    };

    private void Update(Func<CaptureStatus, CaptureStatus> change)
    {
        lock (_gate) _status = change(_status);
    }

    private static CaptureStatus InitialStatus(ICaptureAdapterFactory factory, CaptureOptions options)
    {
        var adapter = factory.Describe();
        return new CaptureStatus(adapter.Name, adapter.Version, adapter.Capabilities, "starting", null, 0, 0,
            null, 0);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
    }
}
