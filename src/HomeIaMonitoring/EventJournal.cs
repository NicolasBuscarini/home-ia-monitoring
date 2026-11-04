using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

public sealed record EventJournalOptions(int MaximumEvents, long MaximumJournalBytes)
{
    public const string MaximumEventsKey = "MONITORING_EVENT_JOURNAL_MAX_EVENTS";
    public const string MaximumBytesKey = "MONITORING_EVENT_JOURNAL_MAX_BYTES";

    public static EventJournalOptions FromConfiguration(IConfiguration configuration)
    {
        var maximumEvents = 10_000;
        if (configuration[MaximumEventsKey] is { } rawEvents &&
            (!int.TryParse(rawEvents, out maximumEvents) || maximumEvents is < 1 or > 100_000))
            throw new InvalidOperationException($"{MaximumEventsKey} deve estar entre 1 e 100000.");
        var maximumBytes = 67_108_864L;
        if (configuration[MaximumBytesKey] is { } rawBytes &&
            (!long.TryParse(rawBytes, out maximumBytes) || maximumBytes is < 1_048_576 or > 1_073_741_824))
            throw new InvalidOperationException($"{MaximumBytesKey} deve estar entre 1048576 e 1073741824.");
        return new EventJournalOptions(maximumEvents, maximumBytes);
    }
}

public enum EventAppendStatus
{
    Created,
    Duplicate,
    CapacityReached
}

public sealed record EventJournalRecoveryResult(int RecoveredEvents, int InvalidEvents, long JournalBytes);

public sealed record EventJournalEnvelopeV1(string SchemaVersion, string EventSha256, JsonElement Event);

public sealed record EventJournalAppendResult(string Status, string EventId, string EventSha256);

public sealed record EventJournalItem(string EventSha256, JsonElement Event);

public sealed record EventJournalPage(IReadOnlyList<EventJournalItem> Items, int Total);

public sealed class EventJournalConflictException() : Exception("event_id_conflict");

public sealed class EventJournal(MonitoringOptions monitoring, EventJournalOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Root => Path.Combine(monitoring.SpoolDirectory, "events");
    private bool _recovered;
    private int _eventCount;
    private long _journalBytes;

    public async Task<EventJournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await RecoverUnderLockAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<EventAppendStatus> AppendAsync(EventJournalEnvelopeV1 envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!ValidateIdentity(envelope, out var eventId, out var cameraId, out var streamEpoch, out var error))
            throw new ArgumentException(error, nameof(envelope));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_recovered) await RecoverUnderLockAsync(cancellationToken);
            var directory = Path.Combine(Root, cameraId, streamEpoch);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, eventId + ".json");
            if (File.Exists(destination))
            {
                var existing = await ReadEnvelopeAsync(destination, cancellationToken);
                if (existing is null || !TryReadIdentity(existing, out var existingEventId, out _, out _) ||
                    existingEventId != eventId ||
                    !string.Equals(existing.EventSha256, envelope.EventSha256, StringComparison.Ordinal))
                    throw new EventJournalConflictException();
                return EventAppendStatus.Duplicate;
            }

            var serialized = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (_eventCount >= options.MaximumEvents ||
                _journalBytes + serialized.Length > options.MaximumJournalBytes)
                return EventAppendStatus.CapacityReached;

            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(serialized, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            _eventCount++;
            _journalBytes += serialized.Length;
            return EventAppendStatus.Created;
        }
        finally { _gate.Release(); }
    }

    public async Task<EventJournalPage> ListAsync(IReadOnlyList<string> cameraIds, DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc, int limit, CancellationToken cancellationToken)
    {
        if (cameraIds.Count == 0 || cameraIds.Count > 32 || cameraIds.Any(id => !IsSafeIdentifier(id)))
            throw new ArgumentException("camera_scope_invalid", nameof(cameraIds));
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc)
            throw new ArgumentException("time_range_invalid", nameof(fromUtc));
        if (limit is < 1 or > 500) throw new ArgumentException("limit_invalid", nameof(limit));

        var scope = cameraIds.ToHashSet(StringComparer.Ordinal);
        var matched = new List<(DateTimeOffset StartedAt, string Hash, JsonElement Event)>();
        foreach (var cameraId in scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(Root, cameraId);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                var envelope = await ReadEnvelopeAsync(path, cancellationToken);
                if (envelope is null) continue;
                var startedAt = ReadStartedAt(envelope.Event);
                if (!startedAt.HasValue) continue;
                if (fromUtc.HasValue && startedAt < fromUtc) continue;
                if (toUtc.HasValue && startedAt > toUtc) continue;
                matched.Add((startedAt.Value, envelope.EventSha256, envelope.Event));
            }
        }
        var ordered = matched.OrderByDescending(item => item.StartedAt).ToArray();
        return new EventJournalPage(ordered.Take(limit)
            .Select(item => new EventJournalItem(item.Hash, item.Event)).ToArray(), ordered.Length);
    }

    public static bool IsSafeIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool TryReadIdentity(EventJournalEnvelopeV1? envelope, out string eventId,
        out string cameraId, out string streamEpoch) =>
        ValidateIdentity(envelope, out eventId, out cameraId, out streamEpoch, out _);

    public static bool ValidateIdentity(EventJournalEnvelopeV1? envelope, out string eventId,
        out string cameraId, out string streamEpoch, out string error)
    {
        eventId = cameraId = streamEpoch = string.Empty;
        error = string.Empty;
        if (envelope is null || envelope.SchemaVersion != "1.0" || envelope.Event.ValueKind != JsonValueKind.Object)
        {
            error = "envelope_invalid";
            return false;
        }
        if (!TryReadString(envelope.Event, "eventId", out eventId) || !IsSafeIdentifier(eventId))
        {
            error = "event_id_invalid";
            return false;
        }
        if (!TryReadString(envelope.Event, "cameraId", out cameraId) || !IsSafeIdentifier(cameraId))
        {
            error = "camera_id_invalid";
            return false;
        }
        if (!TryReadString(envelope.Event, "streamEpoch", out streamEpoch) || !IsSafeIdentifier(streamEpoch))
        {
            error = "stream_epoch_invalid";
            return false;
        }
        if (!TryReadString(envelope.Event, "type", out _) ||
            !TryReadTimestamp(envelope.Event, "startedAt") ||
            !TryReadTimestamp(envelope.Event, "endedAt") ||
            !TryReadTimestamp(envelope.Event, "createdAt"))
        {
            error = "event_fields_invalid";
            return false;
        }
        if (envelope.Event.TryGetProperty("alertLevel", out var alert) &&
            alert.ValueKind != JsonValueKind.String)
        {
            error = "alert_level_invalid";
            return false;
        }
        if (!IsValidHash(envelope.EventSha256))
        {
            error = "event_hash_invalid";
            return false;
        }
        if (HashEvent(envelope.Event) != envelope.EventSha256)
        {
            error = "event_hash_mismatch";
            return false;
        }
        return true;
    }

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.String) return false;
        value = node.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryReadTimestamp(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String &&
        node.TryGetDateTimeOffset(out _);

    private static DateTimeOffset? ReadStartedAt(JsonElement element) =>
        element.TryGetProperty("startedAt", out var node) && node.ValueKind == JsonValueKind.String &&
        node.TryGetDateTimeOffset(out var value) ? value : null;

    private static string HashEvent(JsonElement element) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element.GetRawText()))).ToLowerInvariant();

    private static bool IsValidHash(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character));

    private async Task<EventJournalRecoveryResult> RecoverUnderLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        foreach (var temporary in Directory.EnumerateFiles(Root, "*.tmp", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(temporary); }
            catch (IOException) { }
        }
        var valid = 0;
        var invalid = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = await ReadEnvelopeAsync(path, cancellationToken);
            if (envelope is null || Path.GetFileNameWithoutExtension(path) != Identity(envelope))
            {
                invalid++;
                continue;
            }
            valid++;
            bytes += new FileInfo(path).Length;
        }
        _eventCount = valid;
        _journalBytes = bytes;
        _recovered = true;
        return new EventJournalRecoveryResult(valid, invalid, bytes);
    }

    private static string Identity(EventJournalEnvelopeV1 envelope) =>
        TryReadIdentity(envelope, out var eventId, out _, out _) ? eventId : string.Empty;

    private static async Task<EventJournalEnvelopeV1?> ReadEnvelopeAsync(string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<EventJournalEnvelopeV1>(
                await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
            return envelope is not null && TryReadIdentity(envelope, out _, out _, out _) ? envelope : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
