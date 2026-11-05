using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class PlatformEventOutboxTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Enqueue_limits_depth_and_drops_the_oldest_item_with_metric()
    {
        using var directory = new TempOutbox();
        var options = Options(directory.Path) with { MaximumEvents = 2 };
        using var outbox = new PlatformEventOutbox(options, TimeProvider.System);
        var start = DateTimeOffset.UtcNow;

        var first = await outbox.EnqueueAsync(Event(start), CancellationToken.None);
        await outbox.EnqueueAsync(Event(start.AddMilliseconds(1)), CancellationToken.None);
        await outbox.EnqueueAsync(Event(start.AddMilliseconds(2)), CancellationToken.None);

        var status = outbox.GetStatus();
        Assert.Equal(2, status.Pending);
        Assert.Equal(1, status.Dropped);
        Assert.False(File.Exists(Path.Combine(directory.Path, "pending",
            $"{start.ToUnixTimeMilliseconds():D13}-{first.EventId}.json")));
    }

    [Fact]
    public async Task RunOnce_sends_service_token_and_envelope_then_removes_the_pending_item()
    {
        using var directory = new TempOutbox();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        using var outbox = new PlatformEventOutbox(Options(directory.Path), TimeProvider.System, client);

        var enqueued = await outbox.EnqueueAsync(
            new PlatformEventOutboxEvent("clip.retention.expired", "media", "info", "success", "clip", "CLP0001"),
            CancellationToken.None);
        var processed = await outbox.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/events", request.Uri?.AbsolutePath);
        Assert.Equal(enqueued.CorrelationId, request.CorrelationId);
        Assert.StartsWith("Bearer ", request.Authorization);
        var claims = ReadClaims(request.Authorization!["Bearer ".Length..]);
        Assert.Equal("monitoring", claims.GetProperty("sub").GetString());
        Assert.Equal("home-ia-portal", claims.GetProperty("aud").GetString());
        Assert.Equal("home", claims.GetProperty("residence_id").GetString());
        Assert.Contains("events.write", claims.GetProperty("scope").GetString()!.Split(' '));
        Assert.True(claims.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("1.0", body.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("clip.retention.expired", body.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("media", body.RootElement.GetProperty("category").GetString());
        Assert.Equal("monitoring", body.RootElement.GetProperty("source").GetString());
        Assert.Equal("service", body.RootElement.GetProperty("emitter").GetString());
        Assert.Equal("1.0", body.RootElement.GetProperty("sourceVersion").GetString());
        Assert.Equal("clip", body.RootElement.GetProperty("resource").GetProperty("type").GetString());
        Assert.Equal("CLP0001", body.RootElement.GetProperty("resource").GetProperty("publicId").GetString());
        Assert.Equal(enqueued.CorrelationId, body.RootElement.GetProperty("correlationId").GetString());

        var status = outbox.GetStatus();
        Assert.Equal(1, status.Sent);
        Assert.Equal(0, status.Pending);
        Assert.NotNull(status.LastSuccessUtc);
    }

    [Fact]
    public async Task RunOnce_keeps_the_item_when_the_portal_is_unreachable_and_succeeds_later()
    {
        using var directory = new TempOutbox();
        var reachable = false;
        var handler = new RecordingHandler(_ => reachable
            ? new HttpResponseMessage(HttpStatusCode.Created)
            : throw new HttpRequestException("portal offline"));
        using var client = new HttpClient(handler);
        using var outbox = new PlatformEventOutbox(Options(directory.Path), TimeProvider.System, client);
        await outbox.EnqueueAsync(Event(DateTimeOffset.UtcNow), CancellationToken.None);

        await outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, outbox.GetStatus().Pending);
        Assert.Equal(1, outbox.GetStatus().Failed);

        reachable = true;
        await outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, outbox.GetStatus().Pending);
        Assert.Equal(1, outbox.GetStatus().Sent);
    }

    [Fact]
    public async Task RunOnce_dead_letters_a_permanent_rejection()
    {
        using var directory = new TempOutbox();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        using var outbox = new PlatformEventOutbox(Options(directory.Path), TimeProvider.System, client);
        await outbox.EnqueueAsync(Event(DateTimeOffset.UtcNow), CancellationToken.None);

        await outbox.RunOnceAsync(CancellationToken.None);

        var status = outbox.GetStatus();
        Assert.Equal(0, status.Pending);
        Assert.Equal(1, status.DeadLettered);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "dead"), "*.json"));
    }

    [Fact]
    public async Task RunOnce_dead_letters_after_the_maximum_attempts()
    {
        using var directory = new TempOutbox();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var outbox = new PlatformEventOutbox(Options(directory.Path) with { MaximumAttempts = 2 },
            TimeProvider.System, client);
        await outbox.EnqueueAsync(Event(DateTimeOffset.UtcNow), CancellationToken.None);

        await outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, outbox.GetStatus().Pending);

        await outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, outbox.GetStatus().Pending);
        Assert.Equal(1, outbox.GetStatus().DeadLettered);
    }

    [Fact]
    public async Task Disabled_outbox_neither_writes_nor_sends()
    {
        using var directory = new TempOutbox();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        using var outbox = new PlatformEventOutbox(Options(directory.Path) with { Enabled = false },
            TimeProvider.System, client);

        var enqueued = await outbox.EnqueueAsync(Event(DateTimeOffset.UtcNow), CancellationToken.None);
        await outbox.RunOnceAsync(CancellationToken.None);

        Assert.False(enqueued.Accepted);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "pending")));
    }

    [Fact]
    public async Task Expired_pending_item_is_dropped_instead_of_blocking_the_queue()
    {
        using var directory = new TempOutbox();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        using var outbox = new PlatformEventOutbox(Options(directory.Path) with { MaximumAge = TimeSpan.FromDays(1) },
            TimeProvider.System, client);
        await outbox.EnqueueAsync(Event(DateTimeOffset.UtcNow.AddDays(-2)), CancellationToken.None);

        await outbox.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, outbox.GetStatus().Pending);
        Assert.Equal(1, outbox.GetStatus().Dropped);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Options_fail_fast_when_enabled_without_url_or_secret()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MONITORING_EVENT_OUTBOX_ENABLED"] = "true"
        }).Build();

        var missingUrl = Assert.Throws<InvalidOperationException>(() => PlatformEventOutboxOptions
            .FromConfiguration(configuration, new MonitoringOptions("garage-01", "/spool", 20)));
        Assert.Contains("MONITORING_EVENT_OUTBOX_PORTAL_URL", missingUrl.Message);

        configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MONITORING_EVENT_OUTBOX_ENABLED"] = "true",
            ["MONITORING_EVENT_OUTBOX_PORTAL_URL"] = "http://portal:8080"
        }).Build();
        var missingSecret = Assert.Throws<InvalidOperationException>(() => PlatformEventOutboxOptions
            .FromConfiguration(configuration, new MonitoringOptions("garage-01", "/spool", 20)));
        Assert.Contains("MONITORING_SERVICE_TOKEN_SECRET", missingSecret.Message);
    }

    private static PlatformEventOutboxEvent Event(DateTimeOffset occurredAt) =>
        new("clip.retention.expired", "media", "info", "success", "clip", "CLP0001", OccurredAtUtc: occurredAt);

    private static PlatformEventOutboxOptions Options(string directory) => new(
        true, directory, "http://portal:8080", Secret, "home-ia-portal", "home", 100, 1_048_576,
        TimeSpan.FromDays(1), 10, TimeSpan.FromSeconds(1), 3, TimeSpan.FromSeconds(2));

    private static JsonElement ReadClaims(string token)
    {
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = Base64Url.DecodeFromChars(parts[1]);
        using var document = JsonDocument.Parse(payload);
        var signature = Base64Url.DecodeFromChars(parts[2]);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.True(CryptographicOperations.FixedTimeEquals(expected, signature));
        return document.RootElement.Clone();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<(Uri? Uri, string? Authorization, string? CorrelationId, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Correlation-Id", out var values) ? values.Single() : null, body));
            return respond(request);
        }
    }

    private sealed class TempOutbox : IDisposable
    {
        public TempOutbox()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "outbox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Diretório temporário permanece apenas no host de teste.
            }
        }
    }
}
