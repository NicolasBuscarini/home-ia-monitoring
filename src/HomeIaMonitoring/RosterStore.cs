using System.Security.Cryptography;
using System.Text.Json;

namespace HomeIaMonitoring;

public sealed record RosterPersonV1(
    string SchemaVersion,
    string PublicId,
    string? DisplayName,
    string? Purpose,
    DateTimeOffset AuthorizationRecordedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeletedAtUtc,
    long Version)
{
    public const string CurrentSchemaVersion = "1.0";

    public bool IsDeleted => DeletedAtUtc.HasValue;
}

public sealed record RosterPersonSummary(
    string PublicId,
    string DisplayName,
    string Purpose,
    DateTimeOffset AuthorizationRecordedAtUtc,
    DateTimeOffset CreatedAtUtc,
    long Version);

public sealed record RosterPersonPage(IReadOnlyList<RosterPersonSummary> Items, int Page, int PageSize, int Total);

public sealed record RosterPersonRequest(string DisplayName, string Purpose, bool AuthorizationConfirmed);

public sealed class RosterPersonNotFoundException(string publicId)
    : Exception($"roster_person_not_found:{publicId}");

/// <summary>
/// Owns the known-people roster as durable JSON records under the monitoring spool.
/// Authorization, operator identity and audit remain in the portal; this store only enforces
/// data integrity and the authorization-confirmation precondition.
/// </summary>
public sealed class RosterStore(MonitoringOptions monitoring, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RosterPersonV1> _people = new(StringComparer.Ordinal);
    private bool _loaded;

    private string Root => Path.Combine(monitoring.SpoolDirectory, "roster", "people");

    public async Task<RosterPersonPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1) throw new ArgumentException("page_invalid", nameof(page));
        if (pageSize is < 1 or > 100) throw new ArgumentException("page_size_invalid", nameof(pageSize));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            var all = _people.Values
                .Where(person => !person.IsDeleted)
                .OrderBy(person => person.DisplayName, StringComparer.Ordinal)
                .ThenBy(person => person.PublicId, StringComparer.Ordinal)
                .ToList();
            var items = all.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(person => new RosterPersonSummary(person.PublicId, person.DisplayName!, person.Purpose!,
                    person.AuthorizationRecordedAtUtc, person.CreatedAtUtc, person.Version))
                .ToList();
            return new RosterPersonPage(items, page, pageSize, all.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task<RosterPersonSummary> CreateAsync(RosterPersonRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.AuthorizationConfirmed)
            throw new ArgumentException("authorization_confirmation_required", nameof(request));
        var displayName = Normalize(request.DisplayName, 128, "display_name_invalid");
        var purpose = Normalize(request.Purpose, 256, "purpose_invalid");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            string publicId;
            do
                publicId = "PER" + RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6",
                    System.Globalization.CultureInfo.InvariantCulture) +
                    RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6",
                        System.Globalization.CultureInfo.InvariantCulture);
            while (_people.ContainsKey(publicId));

            var person = new RosterPersonV1(RosterPersonV1.CurrentSchemaVersion, publicId, displayName, purpose,
                now, now, null, 1);
            await WriteUnderLockAsync(person, cancellationToken);
            _people[publicId] = person;
            return ToSummary(person);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string publicId, CancellationToken cancellationToken)
    {
        ValidatePublicId(publicId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            if (!_people.TryGetValue(publicId, out var existing))
                throw new RosterPersonNotFoundException(publicId);
            if (existing.IsDeleted) return;

            var tombstone = existing with
            {
                DisplayName = null,
                Purpose = null,
                DeletedAtUtc = timeProvider.GetUtcNow(),
                Version = existing.Version + 1
            };
            await WriteUnderLockAsync(tombstone, cancellationToken);
            _people[publicId] = tombstone;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedUnderLockAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        if (Directory.Exists(Root))
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var person = await TryReadAsync(path, cancellationToken);
                if (person is not null) _people[person.PublicId] = person;
            }
        }
        _loaded = true;
    }

    private async Task WriteUnderLockAsync(RosterPersonV1 person, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var destination = Path.Combine(Root, person.PublicId + ".json");
        var serialized = JsonSerializer.SerializeToUtf8Bytes(person, JsonOptions);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(serialized, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<RosterPersonV1?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var person = JsonSerializer.Deserialize<RosterPersonV1>(bytes, JsonOptions);
            if (person is null ||
                person.SchemaVersion != RosterPersonV1.CurrentSchemaVersion ||
                !IsValidPublicId(person.PublicId) ||
                Path.GetFileName(path) != person.PublicId + ".json" ||
                person.Version < 1)
                return null;
            if (!person.IsDeleted &&
                (string.IsNullOrWhiteSpace(person.DisplayName) || string.IsNullOrWhiteSpace(person.Purpose)))
                return null;
            return person;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static RosterPersonSummary ToSummary(RosterPersonV1 person) =>
        new(person.PublicId, person.DisplayName!, person.Purpose!, person.AuthorizationRecordedAtUtc,
            person.CreatedAtUtc, person.Version);

    private static string Normalize(string? value, int maximumLength, string error)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength)
            throw new ArgumentException(error);
        return normalized;
    }

    private static void ValidatePublicId(string publicId)
    {
        if (!IsValidPublicId(publicId)) throw new ArgumentException("person_id_invalid", nameof(publicId));
    }

    public static bool IsValidPublicId(string? value)
    {
        if (value is not { Length: 15 } || !value.StartsWith("PER", StringComparison.Ordinal)) return false;
        for (var index = 3; index < value.Length; index++)
            if (value[index] is < '0' or > '9')
                return false;
        return true;
    }
}
