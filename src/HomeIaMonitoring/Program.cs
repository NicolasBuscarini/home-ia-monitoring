using HomeIaMonitoring;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(provider =>
    MonitoringOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider =>
    CaptureWriterOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    return CaptureOptions.FromConfiguration(configuration,
        provider.GetRequiredService<MonitoringOptions>(),
        provider.GetRequiredService<CaptureWriterOptions>());
});
builder.Services.AddSingleton<SpoolWriter>();
builder.Services.AddSingleton<CaptureSettingsStore>();
builder.Services.AddSingleton<CaptureAdapterFactory>();
builder.Services.AddSingleton<ICaptureAdapterFactory>(provider =>
    provider.GetRequiredService<CaptureAdapterFactory>());
builder.Services.AddSingleton<CaptureWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CaptureWorker>());
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new LiveResponse("live")));
app.MapGet("/health/ready", (CaptureSettingsStore settings) =>
    Results.Ok(MonitoringContract.Ready(settings.Current.Enabled)));
app.MapGet("/api/v1/monitoring/contract", (CaptureSettingsStore settings) =>
    Results.Ok(MonitoringContract.Current(settings.Current.Enabled)));
app.MapGet("/api/v1/monitoring/capabilities", (CaptureSettingsStore settings) =>
    Results.Ok(MonitoringContract.Current(settings.Current.Enabled)));
app.MapGet("/api/v1/monitoring/cameras/{cameraId}/status", (string cameraId, MonitoringOptions monitoring,
    TimeProvider timeProvider) =>
{
    var status = CameraStatusReader.Read(monitoring, cameraId, timeProvider.GetUtcNow());
    return Results.Json(status, statusCode: status.Status == "not_found"
        ? StatusCodes.Status404NotFound
        : StatusCodes.Status200OK);
});
app.MapGet("/api/v1/monitoring/capture", (MonitoringOptions monitoring, CaptureOptions capture,
    CaptureSettingsStore settings, SpoolWriter writer, CaptureWorker worker) =>
{
    var current = settings.Current;
    return Results.Ok(new CaptureResponse(MonitoringContract.SchemaVersion, capture.Profile, monitoring.CameraId,
        current.Enabled, current.SegmentSeconds, current.MaximumSegments, current.MaximumSegmentBytes,
        current.Revision, worker.Status, writer.CountGaps(), writer.ReadManifests().Count));
});
app.MapPut("/api/v1/monitoring/capture/settings",
    async (CaptureSettingsRequest request, CaptureSettingsStore settings, CancellationToken cancellationToken) =>
    {
        try
        {
            var updated = await settings.UpdateAsync(request.Enabled, request.SegmentSeconds,
                request.MaximumSegments, request.MaximumSegmentBytes, cancellationToken);
            return Results.Ok(new CaptureSettingsResponse(updated.Enabled, updated.SegmentSeconds,
                updated.MaximumSegments, updated.MaximumSegmentBytes, updated.Revision));
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = "capture_settings_invalid", detail = exception.Message });
        }
    });

app.Run();

public partial class Program;

public sealed record LiveResponse(string Status);

public sealed record CaptureSettingsRequest(bool Enabled, int SegmentSeconds, int MaximumSegments,
    long MaximumSegmentBytes);

public sealed record CaptureSettingsResponse(bool Enabled, int SegmentSeconds, int MaximumSegments,
    long MaximumSegmentBytes, long Revision);

public sealed record CaptureResponse(string SchemaVersion, string Profile, string CameraId, bool Enabled,
    int SegmentSeconds, int MaximumSegments, long MaximumSegmentBytes, long Revision, CaptureStatus Status,
    long Gaps, int Segments);

public static class MonitoringContract
{
    public const string SchemaVersion = "1.0";
    public const string ModuleId = "monitoring";
    public const string ContractVersion = "1.0";
    public const string ExecutionMode = "status-read-only";

    private static readonly string[] BaseCapabilities =
        ["camera.status.read", "capture.status.read", "capture.settings", "health", "media.contract"];

    private static readonly string[] CaptureCapabilities = [.. BaseCapabilities, "capture.write"];

    public static MonitoringModuleResponse Current(bool captureEnabled) => new(SchemaVersion, ModuleId,
        ContractVersion, captureEnabled ? "capture-enabled" : ExecutionMode,
        captureEnabled ? CaptureCapabilities : BaseCapabilities, DateTimeOffset.UtcNow);

    public static MonitoringModuleResponse Ready(bool captureEnabled) => Current(captureEnabled) with
    {
        ExecutionMode = captureEnabled ? "capture-ready" : "status-ready"
    };
}

public sealed record MonitoringModuleResponse(string SchemaVersion, string ModuleId, string ContractVersion,
    string ExecutionMode, IReadOnlyList<string> Capabilities, DateTimeOffset GeneratedAtUtc);
