using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

public sealed record CaptureRuntimeSettings(
    bool Enabled,
    int SegmentSeconds,
    int MaximumSegments,
    long MaximumSegmentBytes,
    long Revision);

public sealed class CaptureSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private CaptureRuntimeSettings _current;
    private readonly string _path;

    public CaptureSettingsStore(MonitoringOptions monitoring, CaptureWriterOptions bootstrap,
        IConfiguration configuration)
    {
        _path = configuration["MONITORING_CAPTURE_SETTINGS_FILE"] ??
                Path.Combine(monitoring.SpoolDirectory, "capture-settings.json");
        _current = Load(bootstrap);
    }

    public CaptureRuntimeSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public string SettingsPath => _path;

    public event Action? Changed;

    public async Task<CaptureRuntimeSettings> UpdateAsync(bool enabled, int segmentSeconds,
        int maximumSegments, long maximumSegmentBytes, CancellationToken cancellationToken = default)
    {
        Validate(segmentSeconds, maximumSegments, maximumSegmentBytes);
        CaptureRuntimeSettings updated;
        lock (_gate)
        {
            updated = new CaptureRuntimeSettings(enabled, segmentSeconds, maximumSegments,
                maximumSegmentBytes, _current.Revision + 1);
        }
        var directory = Path.GetDirectoryName(_path) ??
                        throw new InvalidOperationException("capture_settings_path_invalid");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, JsonOptions),
                cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        lock (_gate) _current = updated;
        if (_changed.CurrentCount == 0) _changed.Release();
        Changed?.Invoke();
        return updated;
    }

    public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _changed.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public static void Validate(int segmentSeconds, int maximumSegments, long maximumSegmentBytes)
    {
        if (segmentSeconds is < 1 or > 60)
            throw new InvalidOperationException("segmentSeconds deve estar entre 1 e 60.");
        if (maximumSegments is < 1 or > 10_000)
            throw new InvalidOperationException("maxSegments deve estar entre 1 e 10000.");
        if (maximumSegmentBytes is < 1_048_576 or > 1_073_741_824)
            throw new InvalidOperationException("maxSegmentBytes deve estar entre 1048576 e 1073741824.");
    }

    private CaptureRuntimeSettings Load(CaptureWriterOptions bootstrap)
    {
        if (!File.Exists(_path))
            return new CaptureRuntimeSettings(bootstrap.Enabled, bootstrap.SegmentSeconds,
                bootstrap.MaximumSegments, bootstrap.MaximumSegmentBytes, 0);
        try
        {
            var settings = JsonSerializer.Deserialize<CaptureRuntimeSettings>(
                File.ReadAllText(_path), JsonOptions);
            if (settings is null) return Disabled(bootstrap);
            Validate(settings.SegmentSeconds, settings.MaximumSegments, settings.MaximumSegmentBytes);
            return settings with { Revision = Math.Max(1, settings.Revision) };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            return Disabled(bootstrap) with { Revision = 1 };
        }
    }

    private static CaptureRuntimeSettings Disabled(CaptureWriterOptions bootstrap) =>
        new(false, bootstrap.SegmentSeconds, bootstrap.MaximumSegments, bootstrap.MaximumSegmentBytes, 0);
}
