using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class StoreEventEmitterTests
{
    [Fact]
    public async Task Roster_create_and_remove_enqueue_typed_outbox_events()
    {
        using var temp = new TempDirectory();
        using var outbox = new PlatformEventOutbox(Options(temp.Path), TimeProvider.System);
        var store = new RosterStore(new MonitoringOptions("garage-01", temp.Path, 20), TimeProvider.System,
            outbox);

        var person = await store.CreateAsync(new RosterPersonRequest("Ana", "household", true),
            CancellationToken.None);
        await store.RemoveAsync(person.PublicId, CancellationToken.None);

        var events = ReadPending(temp.Path);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, value => value.GetProperty("event").GetProperty("eventType").GetString() ==
            "roster.person.created" &&
            value.GetProperty("event").GetProperty("resourcePublicId").GetString() == person.PublicId);
        Assert.Contains(events, value => value.GetProperty("event").GetProperty("eventType").GetString() ==
            "roster.person.removed");
    }

    [Fact]
    public async Task Capture_settings_update_enqueues_a_typed_outbox_event()
    {
        using var temp = new TempDirectory();
        using var outbox = new PlatformEventOutbox(Options(temp.Path), TimeProvider.System);
        var store = new CaptureSettingsStore(new MonitoringOptions("garage-01", temp.Path, 20),
            new CaptureWriterOptions(false, 5, 120, 67_108_864),
            new ConfigurationBuilder().Build(), outbox);

        await store.UpdateAsync(true, 6, 180, 134_217_728);

        var events = ReadPending(temp.Path);
        var value = Assert.Single(events);
        Assert.Equal("monitoring.capture.settings.updated",
            value.GetProperty("event").GetProperty("eventType").GetString());
        Assert.Equal("monitoring.capture",
            value.GetProperty("event").GetProperty("resourceType").GetString());
        Assert.Equal("settings",
            value.GetProperty("event").GetProperty("resourcePublicId").GetString());
    }

    private static List<JsonElement> ReadPending(string directory)
    {
        var pending = Path.Combine(directory, "pending");
        if (!Directory.Exists(pending)) return [];
        return Directory.EnumerateFiles(pending, "*.json")
            .Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone())
            .ToList();
    }

    private static PlatformEventOutboxOptions Options(string directory) => new(
        true, directory, "http://portal:8080", new string('k', 32), "home-ia-portal", "home", 100, 1_048_576,
        TimeSpan.FromDays(1), 10, TimeSpan.FromSeconds(1), 3, TimeSpan.FromSeconds(2));

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-emitter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
