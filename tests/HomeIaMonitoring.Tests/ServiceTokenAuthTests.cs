using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class ServiceTokenAuthTests
{
    private const string Secret = "test-monitoring-service-secret-0123456789abcdef";

    [Fact]
    public async Task Health_endpoints_stay_public()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Metrics_endpoint_stays_public()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Missing_token_is_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/monitoring/contract");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Valid_read_token_allows_read()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("monitoring.read"));

        var response = await client.GetAsync("/api/v1/monitoring/contract");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("monitoring.read", expired: true));

        var response = await client.GetAsync("/api/v1/monitoring/contract");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_audience_and_residence_are_rejected()
    {
        await using var factory = Factory();

        foreach (var token in new[]
                 {
                     Token("monitoring.read", audience: "other-module"),
                     Token("monitoring.read", residence: "other-home")
                 })
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var response = await client.GetAsync("/api/v1/monitoring/contract");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Read_scope_cannot_execute_a_write()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("monitoring.read"));

        var response = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Write_token_reaches_the_write_endpoint()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("monitoring.read monitoring.write"));

        var response = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", new { });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = TempSpool(),
                    ["MONITORING_SERVICE_AUTH_MODE"] = "required",
                    ["MONITORING_SERVICE_TOKEN_SECRET"] = Secret
                })));

    private static string TempSpool()
    {
        var path = Path.Combine(Path.GetTempPath(), "home-ia-monitoring-auth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "manifests"));
        Directory.CreateDirectory(Path.Combine(path, "gaps"));
        return path;
    }

    private static string Token(string scope, bool expired = false, string audience = "monitoring",
        string residence = "home")
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = "home-ia-portal",
            aud = audience,
            sub = "portal",
            residence_id = residence,
            scope,
            iat = now.AddMinutes(expired ? -10 : -1).ToUnixTimeSeconds(),
            exp = now.AddMinutes(expired ? -5 : 1).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        })));
        var signature = Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),
            Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }
}
