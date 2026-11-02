var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new LiveResponse("live")));
app.MapGet("/health/ready", () => Results.Ok(MonitoringContract.Ready()));
app.MapGet("/api/v1/monitoring/contract", () => Results.Ok(MonitoringContract.Current()));
app.MapGet("/api/v1/monitoring/capabilities", () => Results.Ok(MonitoringContract.Current()));

app.Run();

public partial class Program;

public sealed record LiveResponse(string Status);

public static class MonitoringContract
{
    public const string SchemaVersion = "1.0";
    public const string ModuleId = "monitoring";
    public const string ContractVersion = "1.0";
    public const string ExecutionMode = "contract-only";

    private static readonly string[] CapabilityValues =
        ["camera.status", "health", "media.contract"];

    public static MonitoringModuleResponse Current() => new(SchemaVersion, ModuleId, ContractVersion,
        ExecutionMode, CapabilityValues, DateTimeOffset.UtcNow);

    public static MonitoringModuleResponse Ready() => Current() with { ExecutionMode = "contract-ready" };
}

public sealed record MonitoringModuleResponse(string SchemaVersion, string ModuleId, string ContractVersion,
    string ExecutionMode, IReadOnlyList<string> Capabilities, DateTimeOffset GeneratedAtUtc);
