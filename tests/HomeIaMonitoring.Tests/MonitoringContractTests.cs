using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class MonitoringContractTests
{
    [Fact]
    public async Task Liveness_does_not_depend_on_capture()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("live", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Contract_exposes_version_module_mode_and_capabilities()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/monitoring/contract");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("monitoring", json.RootElement.GetProperty("moduleId").GetString());
        Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
        Assert.Equal("contract-only", json.RootElement.GetProperty("executionMode").GetString());
        Assert.Contains("camera.status", json.RootElement.GetProperty("capabilities")
            .EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Readiness_reports_contract_ready_without_claiming_capture()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("contract-ready", json.RootElement.GetProperty("executionMode").GetString());
        Assert.DoesNotContain("capturing", json.RootElement.GetProperty("executionMode").GetString());
    }
}
