using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class RosterStoreTests
{
    [Fact]
    public async Task Create_lists_and_persists_a_known_person_across_restarts()
    {
        using var spool = new TempSpool();
        await using (var factory = Factory(spool.Path))
        {
            using var client = factory.CreateClient();
            using var created = await client.PostAsJsonAsync("/api/v1/monitoring/roster/people",
                new RosterPersonRequest("  Ana Silva  ", "Familiar autorizado", true));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var person = await created.Content.ReadFromJsonAsync<RosterPersonSummary>();
            Assert.NotNull(person);
            Assert.StartsWith("PER", person.PublicId);
            Assert.Equal(15, person.PublicId.Length);
            Assert.Equal("Ana Silva", person.DisplayName);
            Assert.Equal("Familiar autorizado", person.Purpose);
            Assert.Equal(1, person.Version);
            Assert.True(File.Exists(Path.Combine(spool.Path, "roster", "people", person.PublicId + ".json")));

            using var listed = await client.GetAsync("/api/v1/monitoring/roster/people?page=1&pageSize=25");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            var page = await listed.Content.ReadFromJsonAsync<RosterPersonPage>();
            Assert.NotNull(page);
            Assert.Equal(1, page.Total);
            Assert.Single(page.Items);
            Assert.Equal(person.PublicId, page.Items[0].PublicId);
        }

        await using (var restarted = Factory(spool.Path))
        {
            using var client = restarted.CreateClient();
            var page = await client.GetFromJsonAsync<RosterPersonPage>("/api/v1/monitoring/roster/people");
            Assert.NotNull(page);
            Assert.Single(page.Items);
            Assert.Equal("Ana Silva", page.Items[0].DisplayName);
        }
    }

    [Fact]
    public async Task Create_rejects_missing_confirmation_blank_fields_and_oversized_values()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        foreach (var request in new[]
                 {
                     new RosterPersonRequest("Ana", "Familiar", false),
                     new RosterPersonRequest("   ", "Familiar", true),
                     new RosterPersonRequest("Ana", "   ", true),
                     new RosterPersonRequest(new string('a', 129), "Familiar", true),
                     new RosterPersonRequest("Ana", new string('p', 257), true)
                 })
        {
            using var response = await client.PostAsJsonAsync("/api/v1/monitoring/roster/people", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var directory = Path.Combine(spool.Path, "roster", "people");
        Assert.True(!Directory.Exists(directory) || !Directory.EnumerateFiles(directory).Any());
    }

    [Fact]
    public async Task Remove_tombstones_the_record_idempotently_and_hides_it_from_the_list()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/v1/monitoring/roster/people",
            new RosterPersonRequest("Bruno", "Prestador", true))).Content.ReadFromJsonAsync<RosterPersonSummary>();
        Assert.NotNull(created);

        using var removed = await client.DeleteAsync($"/api/v1/monitoring/roster/people/{created.PublicId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var again = await client.DeleteAsync($"/api/v1/monitoring/roster/people/{created.PublicId}");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        var page = await client.GetFromJsonAsync<RosterPersonPage>("/api/v1/monitoring/roster/people");
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);

        var tombstonePath = Path.Combine(spool.Path, "roster", "people", created.PublicId + ".json");
        using var tombstone = JsonDocument.Parse(await File.ReadAllBytesAsync(tombstonePath));
        Assert.Equal(JsonValueKind.Null, tombstone.RootElement.GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, tombstone.RootElement.GetProperty("purpose").ValueKind);
        Assert.Equal(2, tombstone.RootElement.GetProperty("version").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, tombstone.RootElement.GetProperty("deletedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Remove_rejects_invalid_ids_and_reports_unknown_people()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        using var invalid = await client.DeleteAsync("/api/v1/monitoring/roster/people/PER123");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var traversal = await client.DeleteAsync("/api/v1/monitoring/roster/people/PER000000000000");
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
    }

    [Fact]
    public async Task List_paginates_in_display_name_order_and_rejects_invalid_paging()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        foreach (var name in new[] { "Carla", "Ana", "Beto" })
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync("/api/v1/monitoring/roster/people",
                    new RosterPersonRequest(name, "Familiar", true))).StatusCode);

        var first = await client.GetFromJsonAsync<RosterPersonPage>(
            "/api/v1/monitoring/roster/people?page=1&pageSize=2");
        Assert.NotNull(first);
        Assert.Equal(3, first.Total);
        Assert.Equal(new[] { "Ana", "Beto" }, first.Items.Select(item => item.DisplayName).ToArray());
        var second = await client.GetFromJsonAsync<RosterPersonPage>(
            "/api/v1/monitoring/roster/people?page=2&pageSize=2");
        Assert.NotNull(second);
        Assert.Single(second.Items);
        Assert.Equal("Carla", second.Items[0].DisplayName);

        using var invalidPage = await client.GetAsync("/api/v1/monitoring/roster/people?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        using var invalidSize = await client.GetAsync("/api/v1/monitoring/roster/people?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidSize.StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(string spool) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool
                })));

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-roster",
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
