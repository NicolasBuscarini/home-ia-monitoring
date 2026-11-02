using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

public sealed record MonitoringOptions(string CameraId, string SpoolDirectory, int StatusStaleSeconds)
{
    public const string CameraIdKey = "MONITORING_CAMERA_ID";
    public const string SpoolDirectoryKey = "MONITORING_SPOOL_DIRECTORY";
    public const string StatusStaleSecondsKey = "MONITORING_STATUS_STALE_SECONDS";

    public static MonitoringOptions FromConfiguration(IConfiguration configuration)
    {
        var cameraId = (configuration[CameraIdKey] ?? "garage-01").Trim();
        if (cameraId.Length is < 1 or > 64 ||
            cameraId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException("MONITORING_CAMERA_ID inválido.");
        var spool = configuration[SpoolDirectoryKey] ?? "/spool";
        if (!Path.IsPathFullyQualified(spool))
            throw new InvalidOperationException("MONITORING_SPOOL_DIRECTORY deve ser absoluto.");
        var stale = 20;
        if (configuration[StatusStaleSecondsKey] is { } raw &&
            (!int.TryParse(raw, out stale) || stale is < 5 or > 300))
            throw new InvalidOperationException("MONITORING_STATUS_STALE_SECONDS deve estar entre 5 e 300.");
        return new MonitoringOptions(cameraId, spool, stale);
    }
}
