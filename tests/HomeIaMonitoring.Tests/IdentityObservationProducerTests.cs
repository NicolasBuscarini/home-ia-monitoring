using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class IdentityObservationProducerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SyntheticSourceIsDeterministicAndRespectsTheContract()
    {
        var source = new SyntheticFaceObservationSource();
        var clip = new ReadyClip("CLP000000000001", "garage-01", Now);
        var first = await source.TryCreateAsync(clip, CancellationToken.None);
        var second = await source.TryCreateAsync(clip, CancellationToken.None);
        var other = await source.TryCreateAsync(clip with { PublicId = "CLP000000000002" },
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("face-observation.v1", first.SchemaVersion);
        Assert.Equal(SyntheticFaceObservationSource.ModelId, first.ModelId);
        Assert.Equal("face-embedding.v1", first.EmbeddingSchemaVersion);
        Assert.Equal(128, first.Dimensions);
        Assert.Equal(128, first.Values.Count);
        Assert.All(first.Values, value => Assert.True(float.IsFinite(value)));
        Assert.Equal(first.Values, second!.Values);
        Assert.NotEqual(first.Values, other!.Values);
        Assert.Equal(clip.PublicId, first.ClipPublicId);
        Assert.Equal(clip.EndedAtUtc, first.ObservedAtUtc);
    }

    [Fact]
    public async Task DeliversOneSignedObservationPerReadyClip()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var producer = Producer(handler, Clip("CLP000000000001"));

        var processed = await producer.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        var status = producer.GetStatus();
        Assert.Equal(1, status.Observed);
        Assert.Equal(1, status.Delivered);
        Assert.Equal(0, status.Failed);
        var call = Assert.Single(handler.Calls);
        Assert.StartsWith("Bearer ", call.Authorization, StringComparison.Ordinal);
        using var token = JsonDocument.Parse(DecodePayload(call.Authorization!));
        Assert.Equal("monitoring", token.RootElement.GetProperty("sub").GetString());
        Assert.Equal("home-ia-portal", token.RootElement.GetProperty("aud").GetString());
        Assert.Equal("identity.observations.write", token.RootElement.GetProperty("scope").GetString());
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("face-observation.v1", body.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("synthetic-face", body.RootElement.GetProperty("modelId").GetString());
        Assert.Equal(128, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal(128, body.RootElement.GetProperty("values").GetArrayLength());
        Assert.Equal("CLP000000000001", body.RootElement.GetProperty("clipPublicId").GetString());
    }

    [Fact]
    public async Task RetriesTransientFailuresWithoutDuplicatingTheClip()
    {
        var handler = new StubHandler(attempt => attempt < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.Created));
        var producer = Producer(handler, Clip("CLP000000000001"));

        await producer.RunOnceAsync(CancellationToken.None);
        await producer.RunOnceAsync(CancellationToken.None);

        var status = producer.GetStatus();
        Assert.Equal(1, status.Observed);
        Assert.Equal(1, status.Delivered);
        Assert.Equal(2, status.Retried);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task DropsPermanentRejectionWithoutRetrying()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var producer = Producer(handler, Clip("CLP000000000001"));

        await producer.RunOnceAsync(CancellationToken.None);

        var status = producer.GetStatus();
        Assert.Equal(1, status.Failed);
        Assert.Equal(0, status.Retried);
        Assert.Single(handler.Calls);
        Assert.Equal("http_400", status.LastError);
    }

    [Fact]
    public async Task ThrottlesPerCameraAndKeepsTheQueueBounded()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var producer = Producer(handler, Clip("CLP000000000001"), Clip("CLP000000000002"));

        await producer.RunOnceAsync(CancellationToken.None);

        var status = producer.GetStatus();
        Assert.Equal(1, status.Delivered);
        Assert.Equal(1, status.Throttled);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task QueueCapacityDoesNotDiscardClipsThatWereNotYetProcessed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var clips = Enumerable.Range(1, 5).Select(index => Clip($"CLP{index:D12}")).ToArray();
        var producer = new IdentityObservationProducer(
            Options(queueCapacity: 2, minimumIntervalSeconds: 0), new FakeClips(clips),
            new SyntheticFaceObservationSource(), new ManualTimeProvider(),
            NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler));

        for (var cycle = 0; cycle < 3; cycle++)
            await producer.RunOnceAsync(CancellationToken.None);

        var status = producer.GetStatus();
        Assert.Equal(5, status.Observed);
        Assert.Equal(5, status.Delivered);
        Assert.Equal(0, status.Dropped);
        Assert.Equal(0, status.Pending);
    }

    [Fact]
    public async Task ThrottledClipIsRetriedAfterTheIntervalInsteadOfBeingLost()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var time = new ManualTimeProvider();
        var producer = new IdentityObservationProducer(Options(),
            new FakeClips([Clip("CLP000000000001"), Clip("CLP000000000002")]),
            new SyntheticFaceObservationSource(), time,
            NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler));

        await producer.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, producer.GetStatus().Delivered);
        Assert.Equal(1, producer.GetStatus().Throttled);

        time.Now = Now.AddSeconds(61);
        await producer.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, producer.GetStatus().Delivered);
    }

    [Fact]
    public async Task ScansBeyondTheFormerPageLimitWithoutLosingOlderClips()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var clips = Enumerable.Range(1, 300).Select(index => Clip($"CLP{index:D12}")).ToArray();
        var producer = new IdentityObservationProducer(
            Options(queueCapacity: 8, minimumIntervalSeconds: 0), new FakeClips(clips),
            new SyntheticFaceObservationSource(), new ManualTimeProvider(),
            NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler));

        for (var cycle = 0; cycle < 40; cycle++)
            await producer.RunOnceAsync(CancellationToken.None);

        var status = producer.GetStatus();
        Assert.Equal(300, status.Observed);
        Assert.Equal(300, status.Delivered);
        Assert.Equal(300, handler.Calls.Count);
        Assert.True(status.Sweeps >= 1);
    }

    [Fact]
    public async Task RetriesAFailedClipOnALaterSweep()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var source = new FlakyFaceSource(new SyntheticFaceObservationSource(), failures: 1);
        var time = new ManualTimeProvider();
        var producer = new IdentityObservationProducer(Options(minimumIntervalSeconds: 0),
            new FakeClips([Clip("CLP000000000001")]), source, time,
            NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler));

        await producer.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, producer.GetStatus().Failed);
        Assert.Equal(1, producer.GetStatus().Observed);

        await producer.RunOnceAsync(CancellationToken.None);

        var status = producer.GetStatus();
        Assert.Equal(1, status.Delivered);
        Assert.Equal(2, status.Observed);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task StopsRetryingAClipAfterTheConfiguredAttempts()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var source = new FlakyFaceSource(new SyntheticFaceObservationSource(), failures: int.MaxValue);
        var producer = new IdentityObservationProducer(Options(minimumIntervalSeconds: 0),
            new FakeClips([Clip("CLP000000000001")]), source, new ManualTimeProvider(),
            NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler));

        for (var cycle = 0; cycle < 5; cycle++)
            await producer.RunOnceAsync(CancellationToken.None);

        var status = producer.GetStatus();
        Assert.Equal(3, status.Failed);
        Assert.Equal(3, status.Observed);
        Assert.Equal(0, status.Delivered);
    }

    [Fact]
    public async Task SeenRetentionExpiresAndAllowsADeliveryAfterTheRetentionWindow()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var time = new ManualTimeProvider();
        var producer = new IdentityObservationProducer(Options(minimumIntervalSeconds: 0),
            new FakeClips([Clip("CLP000000000001")]), new SyntheticFaceObservationSource(), time,
            NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler));

        await producer.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, producer.GetStatus().Delivered);

        time.Now = Now.AddDays(8);
        await producer.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, producer.GetStatus().Delivered);
        Assert.Equal(2, producer.GetStatus().Observed);
    }

    [Fact]
    public void RejectsUnsupportedOrIncompleteConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => Configuration(new()
        {
            [IdentityObservationOptions.EnabledKey] = "true",
            [IdentityObservationOptions.SourceKey] = "disabled"
        }));
        Assert.Throws<InvalidOperationException>(() => Configuration(new()
        {
            [IdentityObservationOptions.EnabledKey] = "true",
            [IdentityObservationOptions.SourceKey] = "synthetic"
        }));
        Assert.Throws<InvalidOperationException>(() => Configuration(new()
        {
            [IdentityObservationOptions.SourceKey] = "face-model"
        }));
        var options = Configuration(new()
        {
            [IdentityObservationOptions.EnabledKey] = "true",
            [IdentityObservationOptions.SourceKey] = "synthetic",
            [IdentityObservationOptions.PortalUrlKey] = "http://portal.test",
            [IdentityObservationOptions.SecretKey] = "0123456789012345678901234567890123456789"
        });
        Assert.True(options.Enabled);
        Assert.Equal(60, options.MinimumIntervalSeconds);
        Assert.Equal(64, options.QueueCapacity);
    }

    [Fact]
    public async Task PersistsTheScanCursorAcrossRestarts()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "home-ia-observation-state-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(directory, "scan-state.json");
        try
        {
            var store = new IdentityObservationScanStateStore(statePath);
            var clips = new FakeClips([
                Clip("CLP000000000001"), Clip("CLP000000000002"), Clip("CLP000000000003")
            ]);
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));

            var first = new IdentityObservationProducer(
                Options(queueCapacity: 1, minimumIntervalSeconds: 0), clips,
                new SyntheticFaceObservationSource(), new ManualTimeProvider(),
                NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler), store);
            await first.RunOnceAsync(CancellationToken.None);
            first.Dispose();

            var second = new IdentityObservationProducer(
                Options(queueCapacity: 1, minimumIntervalSeconds: 0), clips,
                new SyntheticFaceObservationSource(), new ManualTimeProvider(),
                NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler), store);
            await second.RunOnceAsync(CancellationToken.None);
            second.Dispose();

            Assert.Equal(0, clips.Requests[0].Offset);
            Assert.Equal(1, clips.Requests[1].Offset);
            Assert.Equal(2, clips.Requests.Count);
            Assert.Equal(1, second.GetStatus().Observed);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsSeenClipsAcrossRestarts()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "home-ia-observation-state-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(directory, "scan-state.json");
        try
        {
            var store = new IdentityObservationScanStateStore(statePath);
            var clips = new FakeClips([Clip("CLP000000000001")]);
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));

            var first = new IdentityObservationProducer(
                Options(queueCapacity: 1, minimumIntervalSeconds: 0), clips,
                new SyntheticFaceObservationSource(), new ManualTimeProvider(),
                NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler), store);
            await first.RunOnceAsync(CancellationToken.None);
            first.Dispose();
            Assert.Single(handler.Calls);

            var second = new IdentityObservationProducer(
                Options(queueCapacity: 1, minimumIntervalSeconds: 0), clips,
                new SyntheticFaceObservationSource(), new ManualTimeProvider(),
                NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler), store);
            await second.RunOnceAsync(CancellationToken.None);
            second.Dispose();

            Assert.Equal(0, second.GetStatus().Observed);
            Assert.Single(handler.Calls);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static IdentityObservationOptions Configuration(Dictionary<string, string?> values) =>
        IdentityObservationOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values).Build());

    private static IdentityObservationOptions Options(int queueCapacity = 8,
        int minimumIntervalSeconds = 60) => new(true, "synthetic", "http://portal.test",
        "0123456789012345678901234567890123456789", "home-ia-portal", "home",
        minimumIntervalSeconds, queueCapacity, 3, TimeSpan.FromSeconds(2), 30);

    private static IdentityObservationProducer Producer(StubHandler handler, params ClipLibraryItem[] clips) =>
        new(Options(), new FakeClips(clips), new SyntheticFaceObservationSource(), new ManualTimeProvider(),
            NullLogger<IdentityObservationProducer>.Instance, new HttpClient(handler));

    private static ClipLibraryItem Clip(string publicId) => new(publicId, publicId, "garage-01",
        Now.AddSeconds(-20), Now, "ready", "not_requested", "motion", "1", new string('a', 64),
        null, 0, Now, 1_024);

    private static string DecodePayload(string authorization)
    {
        var segment = authorization["Bearer ".Length..].Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/')
            .PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private sealed class FlakyFaceSource(IFaceObservationSource inner, int failures) : IFaceObservationSource
    {
        public int Calls { get; private set; }

        public string Name => inner.Name;

        public Task<FaceObservation?> TryCreateAsync(ReadyClip clip, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= failures) throw new InvalidOperationException("transient_face_source_failure");
            return inner.TryCreateAsync(clip, cancellationToken);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = IdentityObservationProducerTests.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeClips(ClipLibraryItem[] items) : IClipPlaybackSource
    {
        public List<(int Take, int Offset)> Requests { get; } = [];

        public Task<ClipScanPage> ListReadyPageAsync(int take, int offset, CancellationToken cancellationToken)
        {
            Requests.Add((take, offset));
            var page = items.Skip(offset).Take(take).ToArray();
            return Task.FromResult(new ClipScanPage(page, offset + page.Length,
                offset + page.Length < items.Length));
        }

        public Task<ClipLibraryItem?> GetAsync(string publicId, CancellationToken cancellationToken) =>
            Task.FromResult<ClipLibraryItem?>(items.FirstOrDefault(item => item.PublicId == publicId));

        public Task<string?> GetThumbnailPathAsync(string publicId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> GetPlaybackPathAsync(string publicId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private sealed class StubHandler(Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string? Authorization, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Headers.Authorization?.ToString(), body));
            return responder(Calls.Count);
        }
    }
}
