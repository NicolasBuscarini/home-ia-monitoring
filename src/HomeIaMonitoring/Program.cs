using HomeIaMonitoring;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(provider =>
    MonitoringOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new LiveResponse("live")));
app.MapGet("/health/ready", () => Results.Ok(MonitoringContract.Ready()));
app.MapGet("/api/v1/monitoring/contract", () => Results.Ok(MonitoringContract.Current()));
app.MapGet("/api/v1/monitoring/capabilities", () => Results.Ok(MonitoringContract.Current()));
app.MapGet("/api/v1/monitoring/cameras/{cameraId}/status", (string cameraId, MonitoringOptions monitoring,
    TimeProvider timeProvider) =>
{
    var status = CameraStatusReader.Read(monitoring, cameraId, timeProvider.GetUtcNow());
    return Results.Json(status, statusCode: status.Status == "not_found"
        ? StatusCodes.Status404NotFound
        : StatusCodes.Status200OK);
});

app.Run();

public partial class Program;

public sealed record LiveResponse(string Status);

public static class MonitoringContract
{
    public const string SchemaVersion = "1.0";
    public const string ModuleId = "monitoring";
    public const string ContractVersion = "1.0";
    public const string ExecutionMode = "status-read-only";

    private static readonly string[] CapabilityValues =
        ["camera.status.read", "health", "media.contract"];

    public static MonitoringModuleResponse Current() => new(SchemaVersion, ModuleId, ContractVersion,
        ExecutionMode, CapabilityValues, DateTimeOffset.UtcNow);

    public static MonitoringModuleResponse Ready() => Current() with { ExecutionMode = "status-ready" };
}

public sealed record MonitoringModuleResponse(string SchemaVersion, string ModuleId, string ContractVersion,
    string ExecutionMode, IReadOnlyList<string> Capabilities, DateTimeOffset GeneratedAtUtc);
