using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class BiometricStoreTests
{
    private const string User = "USR000000000001";

    [Fact]
    public async Task Enroll_lists_and_persists_ciphertext_across_restarts()
    {
        using var spool = new TempSpool();
        await using (var factory = Factory(spool.Path))
        {
            using var client = factory.CreateClient();
            using var created = await client.PostAsJsonAsync("/api/v1/monitoring/biometrics/enrollments",
                Request());
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var result = await created.Content.ReadFromJsonAsync<BiometricEnrollmentResult>();
            Assert.NotNull(result);
            Assert.StartsWith("BIO", result.Enrollment.PublicId);
            Assert.Equal(15, result.Enrollment.PublicId.Length);
            Assert.Equal("active", result.Enrollment.Status);
            Assert.Equal("2021dec", result.Enrollment.ModelVersion);
            Assert.Equal(365, (result.Enrollment.ExpiresAtUtc - result.Enrollment.EnrolledAtUtc).Days);
            Assert.Empty(result.ReplacedPublicIds);

            var path = Path.Combine(spool.Path, "biometrics", "enrollments",
                result.Enrollment.PublicId + ".json");
            Assert.True(File.Exists(path));
            using var stored = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            Assert.Equal(128, stored.RootElement.GetProperty("embeddingCiphertext").GetBytesFromBase64().Length);

            using var listed = await client.GetAsync(
                $"/api/v1/monitoring/biometrics/enrollments?userPublicId={User}");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            var items = await listed.Content.ReadFromJsonAsync<BiometricEnrollmentSummary[]>();
            Assert.NotNull(items);
            Assert.Single(items);
            Assert.Equal(result.Enrollment.PublicId, items[0].PublicId);
        }

        await using (var restarted = Factory(spool.Path))
        {
            using var client = restarted.CreateClient();
            var items = await client.GetFromJsonAsync<BiometricEnrollmentSummary[]>(
                $"/api/v1/monitoring/biometrics/enrollments?userPublicId={User}");
            Assert.NotNull(items);
            Assert.Single(items);
            Assert.Equal("active", items[0].Status);
        }
    }

    [Fact]
    public async Task New_enrollment_revokes_the_previous_active_payload()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        var first = await Enroll(client);
        var second = await Enroll(client);

        Assert.Single(second.ReplacedPublicIds);
        Assert.Equal(first.Enrollment.PublicId, second.ReplacedPublicIds[0]);
        var items = await client.GetFromJsonAsync<BiometricEnrollmentSummary[]>(
            $"/api/v1/monitoring/biometrics/enrollments?userPublicId={User}");
        Assert.NotNull(items);
        Assert.Equal(2, items.Length);
        var active = Assert.Single(items, item => item.Status == "active");
        Assert.Equal(second.Enrollment.PublicId, active.PublicId);
        var revoked = Assert.Single(items, item => item.Status == "revoked");
        Assert.Equal("replaced", revoked.RevokedReason);

        using var stored = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(spool.Path, "biometrics", "enrollments", first.Enrollment.PublicId + ".json")));
        Assert.Equal(JsonValueKind.Null, stored.RootElement.GetProperty("embeddingCiphertext").ValueKind);
        Assert.Equal(JsonValueKind.Null, stored.RootElement.GetProperty("embeddingNonce").ValueKind);
        Assert.Equal(JsonValueKind.Null, stored.RootElement.GetProperty("embeddingTag").ValueKind);
    }

    [Fact]
    public async Task Enrollment_validation_rejects_consent_payload_and_identity_errors()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        var valid = Request();

        foreach (var request in new[]
                 {
                     valid with { ConsentVersion = "outdated" },
                     valid with { EmbeddingCiphertext = [] },
                     valid with { EmbeddingNonce = new byte[8] },
                     valid with { EmbeddingTag = new byte[8] },
                     valid with { Dimensions = 0 },
                     valid with { UserPublicId = "invalid" },
                     valid with { KeyVersion = " " }
                 })
        {
            using var rejected = await client.PostAsJsonAsync(
                "/api/v1/monitoring/biometrics/enrollments", request);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using var missingQuery = await client.GetAsync("/api/v1/monitoring/biometrics/enrollments");
        Assert.Equal(HttpStatusCode.BadRequest, missingQuery.StatusCode);
        using var invalidQuery = await client.GetAsync(
            "/api/v1/monitoring/biometrics/enrollments?userPublicId=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuery.StatusCode);
    }

    [Fact]
    public async Task Revoke_is_idempotent_erases_payload_and_rejects_unknown_or_invalid_ids()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        var created = await Enroll(client);

        using var revoked = await client.DeleteAsync(
            $"/api/v1/monitoring/biometrics/enrollments/{created.Enrollment.PublicId}?reason=owner_request");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var repeated = await client.DeleteAsync(
            $"/api/v1/monitoring/biometrics/enrollments/{created.Enrollment.PublicId}?reason=owner_request");
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);

        var items = await client.GetFromJsonAsync<BiometricEnrollmentSummary[]>(
            $"/api/v1/monitoring/biometrics/enrollments?userPublicId={User}");
        Assert.NotNull(items);
        var item = Assert.Single(items);
        Assert.Equal("revoked", item.Status);
        Assert.Equal("owner_request", item.RevokedReason);
        using var stored = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(spool.Path, "biometrics", "enrollments", created.Enrollment.PublicId + ".json")));
        Assert.Equal(JsonValueKind.Null, stored.RootElement.GetProperty("embeddingCiphertext").ValueKind);

        using var unknown = await client.DeleteAsync(
            "/api/v1/monitoring/biometrics/enrollments/BIO000000000099?reason=owner_request");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var invalid = await client.DeleteAsync(
            $"/api/v1/monitoring/biometrics/enrollments/{created.Enrollment.PublicId}?reason=other");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Expiry_erases_the_payload_and_is_visible_without_a_restart()
    {
        using var spool = new TempSpool();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        await using var factory = Factory(spool.Path, clock);
        using var client = factory.CreateClient();
        var created = await Enroll(client);

        clock.Advance(TimeSpan.FromDays(366));
        var items = await client.GetFromJsonAsync<BiometricEnrollmentSummary[]>(
            $"/api/v1/monitoring/biometrics/enrollments?userPublicId={User}");
        Assert.NotNull(items);
        var expired = Assert.Single(items);
        Assert.Equal("expired", expired.Status);
        Assert.Equal("expired", expired.RevokedReason);
        using var stored = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(spool.Path, "biometrics", "enrollments", created.Enrollment.PublicId + ".json")));
        Assert.Equal(JsonValueKind.Null, stored.RootElement.GetProperty("embeddingCiphertext").ValueKind);
    }

    private static BiometricEnrollmentRequest Request() => new(
        User,
        Enumerable.Range(0, 128).Select(value => (byte)value).ToArray(),
        new byte[12],
        new byte[16],
        "v1",
        128,
        "opencv-zoo-sface",
        "2021dec",
        "1.0",
        "onnxruntime-cpu",
        BiometricStore.ConsentVersion);

    private static async Task<BiometricEnrollmentResult> Enroll(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/monitoring/biometrics/enrollments", Request());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BiometricEnrollmentResult>())!;
    }

    private static WebApplicationFactory<Program> Factory(string spool, TimeProvider? clock = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (clock is not null)
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                });
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool
                }));
        });

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-biometrics",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
