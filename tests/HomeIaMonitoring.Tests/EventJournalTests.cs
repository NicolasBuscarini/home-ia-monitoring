using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class EventJournalTests
{
    private const string EventJson = """
        {"schemaVersion":"1.0","eventId":"0123456789abcdef0123456789abcdef","contextId":"garage","cameraId":"garage-01","streamEpoch":"epoch-1","trackIds":["track-1"],"type":"person.in_zone","alertLevel":"attention","ruleConfigVersion":"config-1","startedAt":"2026-11-02T12:00:00+00:00","endedAt":"2026-11-02T12:00:31+00:00","ruleId":"dwell","ruleVersion":1,"evidence":{"reasonCode":"zone.dwell.threshold_reached","observedDurationSeconds":31,"position":{"x":0.5,"y":0.5},"boundingBox":{"x1":0.4,"y1":0.4,"x2":0.6,"y2":0.6},"detectionScore":0.9,"crossingDirection":null},"createdAt":"2026-11-02T12:00:31+00:00"}
        """;

    [Fact]
    public async Task Append_is_idempotent_checksum_validated_and_preserves_the_hashed_payload()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        var hash = Hash(EventJson);
        using var created = await client.PostAsJsonAsync("/api/v1/monitoring/events", Envelope(hash, EventJson));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("created", body.GetProperty("status").GetString());
        Assert.Equal(hash, body.GetProperty("eventSha256").GetString());

        using var duplicate = await client.PostAsJsonAsync("/api/v1/monitoring/events", Envelope(hash, EventJson));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal("duplicate",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var stored = Directory.EnumerateFiles(Path.Combine(spool.Path, "events"), "*.json",
            SearchOption.AllDirectories).Single();
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(stored));
        var storedRaw = document.RootElement.GetProperty("event").GetRawText();
        Assert.Equal(hash, Hash(storedRaw));
        Assert.Equal(hash, document.RootElement.GetProperty("eventSha256").GetString());

        using var tampered = await client.PostAsJsonAsync("/api/v1/monitoring/events",
            Envelope(new string('0', 64), EventJson));
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);

        var changed = EventJson.Replace("person.in_zone", "person.line_crossed");
        using var conflict = await client.PostAsJsonAsync("/api/v1/monitoring/events",
            Envelope(Hash(changed), changed));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Events_are_listed_by_authorized_camera_scope_time_and_limit()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        await PostAsync(client, EventJson);
        var older = EventJson
            .Replace("0123456789abcdef0123456789abcdef", "1123456789abcdef0123456789abcdef")
            .Replace("2026-11-02T12:00", "2026-11-02T11:00");
        await PostAsync(client, older);
        var otherCamera = EventJson
            .Replace("0123456789abcdef0123456789abcdef", "2123456789abcdef0123456789abcdef")
            .Replace("garage-01", "side-entry");
        await PostAsync(client, otherCamera);

        using var scoped = await client.GetAsync(
            "/api/v1/monitoring/events?cameraIds=garage-01&limit=1");
        Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
        var page = await scoped.Content.ReadFromJsonAsync<EventJournalPage>();
        Assert.NotNull(page);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal("person.in_zone",
            page.Items[0].Event.GetProperty("type").GetString());

        using var empty = await client.GetAsync("/api/v1/monitoring/events");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var invalidLimit = await client.GetAsync(
            "/api/v1/monitoring/events?cameraIds=garage-01&limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);

        using var unsorted = await client.GetAsync(
            "/api/v1/monitoring/events?cameraIds=garage-01&fromUtc=2026-11-02T11:30:00Z");
        var filtered = await unsorted.Content.ReadFromJsonAsync<EventJournalPage>();
        Assert.Equal(1, filtered!.Total);
    }

    [Fact]
    public async Task Recovery_reconciles_existing_files_and_capacity_is_bounded()
    {
        using var spool = new TempSpool();
        await using (var first = Factory(spool.Path, maximumEvents: 1))
        using (var client = first.CreateClient())
        {
            await PostAsync(client, EventJson);
            var second = EventJson.Replace("0123456789abcdef0123456789abcdef",
                "3123456789abcdef0123456789abcdef");
            using var rejected = await client.PostAsJsonAsync("/api/v1/monitoring/events",
                Envelope(Hash(second), second));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        await using var factory = Factory(spool.Path);
        using var recoveredClient = factory.CreateClient();
        using var recovery = await recoveredClient.GetAsync("/api/v1/monitoring/events/recovery");
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        var result = await recovery.Content.ReadFromJsonAsync<EventJournalRecoveryResult>();
        Assert.Equal(1, result!.RecoveredEvents);
        Assert.Equal(0, result.InvalidEvents);
        Assert.True(result.JournalBytes > 0);

        using var duplicate = await recoveredClient.PostAsJsonAsync("/api/v1/monitoring/events",
            Envelope(Hash(EventJson), EventJson));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
    }

    [Fact]
    public async Task Contract_advertises_event_read_and_write_capabilities()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/v1/monitoring/contract"));
        var capabilities = json.RootElement.GetProperty("capabilities")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("events.read", capabilities);
        Assert.Contains("events.write", capabilities);
    }

    private static async Task PostAsync(HttpClient client, string eventJson)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/monitoring/events",
            Envelope(Hash(eventJson), eventJson));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static EventJournalEnvelopeV1 Envelope(string hash, string eventJson) =>
        new("1.0", hash, JsonDocument.Parse(Canonical(eventJson)).RootElement.Clone());

    private static string Canonical(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(json)))).ToLowerInvariant();

    private static WebApplicationFactory<Program> Factory(string spool, int maximumEvents = 10_000) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool,
                    ["MONITORING_EVENT_JOURNAL_MAX_EVENTS"] = maximumEvents.ToString()
                })));

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-events",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "events"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
