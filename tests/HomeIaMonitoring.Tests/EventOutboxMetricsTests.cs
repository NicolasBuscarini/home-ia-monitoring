using System.Net;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class EventOutboxMetricsTests
{
    [Fact]
    public void Render_exposes_outbox_counters_in_the_prometheus_text_format()
    {
        var status = new PlatformEventOutboxStatus(true, 2, 128, 5, 1, 3, 1,
            new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), "temporary");

        var text = EventOutboxMetrics.Render(status);

        Assert.Contains("# TYPE monitoring_event_outbox_enabled gauge", text);
        Assert.Contains("monitoring_event_outbox_enabled 1", text);
        Assert.Contains("monitoring_event_outbox_pending 2", text);
        Assert.Contains("monitoring_event_outbox_pending_bytes 128", text);
        Assert.Contains("# TYPE monitoring_event_outbox_sent_total counter", text);
        Assert.Contains("monitoring_event_outbox_sent_total 5", text);
        Assert.Contains("monitoring_event_outbox_failed_total 1", text);
        Assert.Contains("monitoring_event_outbox_dropped_total 3", text);
        Assert.Contains("monitoring_event_outbox_dead_letter 1", text);
        Assert.Contains("monitoring_event_outbox_last_success_timestamp_seconds 1793880000", text);
        Assert.DoesNotContain("temporary", text);
    }

    [Fact]
    public void Render_omits_the_last_success_line_until_a_batch_is_accepted()
    {
        var text = EventOutboxMetrics.Render(new PlatformEventOutboxStatus(false, 0, 0, 0, 0, 0, 0, null, null));

        Assert.Contains("monitoring_event_outbox_enabled 0", text);
        Assert.DoesNotContain("last_success", text);
    }

    [Fact]
    public async Task Metrics_endpoint_serves_the_disabled_outbox_snapshot()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("monitoring_event_outbox_enabled 0", body);
        Assert.Contains("monitoring_event_outbox_pending 0", body);
    }
}
