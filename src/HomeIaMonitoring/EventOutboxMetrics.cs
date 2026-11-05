using System.Globalization;
using System.Text;

namespace HomeIaMonitoring;

/// <summary>
/// Renders the durable outbox status in the Prometheus text exposition format. The values are
/// read from the same snapshot as <c>GET /api/v1/monitoring/events/outbox</c>; free-text errors are
/// intentionally left out of the metric labels to keep cardinality bounded and payloads out.
/// </summary>
public static class EventOutboxMetrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Render(PlatformEventOutboxStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var builder = new StringBuilder();
        Append(builder, "monitoring_event_outbox_enabled", "gauge",
            "Whether the durable event outbox is enabled (1) or disabled (0).",
            status.Enabled ? 1 : 0);
        Append(builder, "monitoring_event_outbox_pending", "gauge",
            "Events waiting to be sent to the portal.", status.Pending);
        Append(builder, "monitoring_event_outbox_pending_bytes", "gauge",
            "Bytes occupied by pending events.", status.PendingBytes);
        Append(builder, "monitoring_event_outbox_sent_total", "counter",
            "Events accepted by the portal since process start.", status.Sent);
        Append(builder, "monitoring_event_outbox_failed_total", "counter",
            "Send attempts that failed and will be retried.", status.Failed);
        Append(builder, "monitoring_event_outbox_dropped_total", "counter",
            "Events discarded by count, byte or age limits.", status.Dropped);
        Append(builder, "monitoring_event_outbox_dead_letter", "gauge",
            "Events moved to dead-letter after a permanent rejection.", status.DeadLettered);
        if (status.LastSuccessUtc is { } lastSuccess)
        {
            Append(builder, "monitoring_event_outbox_last_success_timestamp_seconds", "gauge",
                "Unix time of the last accepted batch.", lastSuccess.ToUnixTimeSeconds());
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, string type, string help, long value)
    {
        builder.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        builder.Append("# TYPE ").Append(name).Append(' ').AppendLine(type);
        builder.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }
}
