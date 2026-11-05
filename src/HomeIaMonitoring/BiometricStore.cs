using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace HomeIaMonitoring;

public sealed record BiometricEnrollmentV1(
    string SchemaVersion,
    string PublicId,
    string UserPublicId,
    string Status,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedReason,
    byte[]? EmbeddingCiphertext,
    byte[]? EmbeddingNonce,
    byte[]? EmbeddingTag,
    string KeyVersion,
    int Dimensions,
    string ModelId,
    string ModelVersion,
    string EmbeddingSchemaVersion,
    string AdapterId,
    string ConsentVersion,
    DateTimeOffset ConsentAcceptedAtUtc,
    long Version)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string ActiveStatus = "active";
    public const string RevokedStatus = "revoked";
    public const string ExpiredStatus = "expired";

    public bool HasPayload => EmbeddingCiphertext is { Length: > 0 } && EmbeddingNonce is { Length: > 0 } &&
                              EmbeddingTag is { Length: > 0 };
}

public sealed record BiometricEnrollmentSummary(
    string PublicId,
    string UserPublicId,
    string Status,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string RevokedReason,
    string ModelId,
    string ModelVersion,
    int Dimensions,
    string KeyVersion,
    string ConsentVersion,
    DateTimeOffset ConsentAcceptedAtUtc);

public sealed record BiometricEnrollmentRequest(
    string UserPublicId,
    byte[]? EmbeddingCiphertext,
    byte[]? EmbeddingNonce,
    byte[]? EmbeddingTag,
    string KeyVersion,
    int Dimensions,
    string ModelId,
    string ModelVersion,
    string EmbeddingSchemaVersion,
    string AdapterId,
    string ConsentVersion);

public sealed record BiometricEnrollmentResult(
    BiometricEnrollmentSummary Enrollment,
    IReadOnlyList<string> ReplacedPublicIds);

public sealed class BiometricEnrollmentNotFoundException(string publicId)
    : Exception($"biometric_enrollment_not_found:{publicId}");

public sealed record BiometricCiphertextEnvelope(
    string PublicId,
    byte[] EmbeddingCiphertext,
    byte[] EmbeddingNonce,
    byte[] EmbeddingTag,
    string KeyVersion,
    int Dimensions,
    string ModelId,
    string ModelVersion,
    string EmbeddingSchemaVersion,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Version);

/// <summary>
/// Owns the operational encrypted biometric registry under the monitoring spool. Identity,
/// authorization, consent text, extraction/protection and audit remain in the portal; this store
/// only receives ciphertext and enforces retention, replacement and tombstone invariants.
/// </summary>
public sealed class BiometricStore(MonitoringOptions monitoring, TimeProvider timeProvider,
    PlatformEventOutbox? outbox = null)
{
    public const string ConsentVersion = "biometric-consent.v1";
    public const int RetentionDays = 365;
    private const int MaximumCiphertextBytes = 65_536;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, BiometricEnrollmentV1> _enrollments = new(StringComparer.Ordinal);
    private bool _loaded;

    private string Root => Path.Combine(monitoring.SpoolDirectory, "biometrics", "enrollments");

    public async Task<IReadOnlyList<BiometricEnrollmentSummary>> ListAsync(string userPublicId,
        CancellationToken cancellationToken)
    {
        ValidateUserPublicId(userPublicId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            await ExpireDueUnderLockAsync(cancellationToken);
            return _enrollments.Values
                .Where(item => item.UserPublicId == userPublicId)
                .OrderByDescending(item => item.EnrolledAtUtc)
                .ThenBy(item => item.PublicId, StringComparer.Ordinal)
                .Take(20)
                .Select(ToSummary)
                .ToList();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Returns only active ciphertext envelopes for the portal's matching boundary. The service never
    /// sees the key or plaintext; this is a service-to-service, read-only export of encrypted payloads.
    /// </summary>
    public async Task<IReadOnlyList<BiometricCiphertextEnvelope>> ListActiveCiphertextsAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            await ExpireDueUnderLockAsync(cancellationToken);
            return _enrollments.Values
                .Where(item => item.Status == BiometricEnrollmentV1.ActiveStatus && item.HasPayload)
                .OrderBy(item => item.PublicId, StringComparer.Ordinal)
                .Select(item => new BiometricCiphertextEnvelope(item.PublicId,
                    item.EmbeddingCiphertext!, item.EmbeddingNonce!, item.EmbeddingTag!,
                    item.KeyVersion, item.Dimensions, item.ModelId, item.ModelVersion,
                    item.EmbeddingSchemaVersion, item.EnrolledAtUtc, item.ExpiresAtUtc, item.Version))
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<BiometricEnrollmentSummary> GetAsync(string publicId, CancellationToken cancellationToken)
    {
        ValidatePublicId(publicId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            return _enrollments.TryGetValue(publicId, out var enrollment)
                ? ToSummary(enrollment)
                : throw new BiometricEnrollmentNotFoundException(publicId);
        }
        finally { _gate.Release(); }
    }

    public async Task<BiometricEnrollmentResult> EnrollAsync(BiometricEnrollmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUserPublicId(request.UserPublicId);
        if (!string.Equals(request.ConsentVersion, ConsentVersion, StringComparison.Ordinal))
            throw new ArgumentException("biometric_consent_required", nameof(request));
        var ciphertext = request.EmbeddingCiphertext;
        if (ciphertext is not { Length: > 0 and <= MaximumCiphertextBytes })
            throw new ArgumentException("embedding_ciphertext_invalid", nameof(request));
        if (request.EmbeddingNonce is not { Length: NonceBytes } ||
            request.EmbeddingTag is not { Length: TagBytes })
            throw new ArgumentException("embedding_protection_invalid", nameof(request));
        if (request.Dimensions is < 1 or > 4096)
            throw new ArgumentException("embedding_dimensions_invalid", nameof(request));
        var keyVersion = Normalize(request.KeyVersion, 64, "key_version_invalid");
        var modelId = Normalize(request.ModelId, 128, "model_id_invalid");
        var modelVersion = Normalize(request.ModelVersion, 128, "model_version_invalid");
        var schemaVersion = Normalize(request.EmbeddingSchemaVersion, 64, "embedding_schema_invalid");
        var adapterId = Normalize(request.AdapterId, 128, "adapter_id_invalid");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            var replaced = new List<string>();
            foreach (var previous in _enrollments.Values
                         .Where(item => item.UserPublicId == request.UserPublicId &&
                                        item.Status == BiometricEnrollmentV1.ActiveStatus)
                         .ToArray())
            {
                var revoked = previous with
                {
                    Status = BiometricEnrollmentV1.RevokedStatus,
                    RevokedAtUtc = now,
                    RevokedReason = "replaced",
                    EmbeddingCiphertext = null,
                    EmbeddingNonce = null,
                    EmbeddingTag = null,
                    Version = previous.Version + 1
                };
                await WriteUnderLockAsync(revoked, cancellationToken);
                _enrollments[revoked.PublicId] = revoked;
                replaced.Add(revoked.PublicId);
            }

            string publicId;
            do
                publicId = "BIO" +
                           RandomNumberGenerator.GetInt32(0, 1_000_000)
                               .ToString("D6", CultureInfo.InvariantCulture) +
                           RandomNumberGenerator.GetInt32(0, 1_000_000)
                               .ToString("D6", CultureInfo.InvariantCulture);
            while (_enrollments.ContainsKey(publicId));

            var enrollment = new BiometricEnrollmentV1(
                BiometricEnrollmentV1.CurrentSchemaVersion,
                publicId,
                request.UserPublicId,
                BiometricEnrollmentV1.ActiveStatus,
                now,
                now.AddDays(RetentionDays),
                null,
                null,
                ciphertext,
                request.EmbeddingNonce,
                request.EmbeddingTag,
                keyVersion,
                request.Dimensions,
                modelId,
                modelVersion,
                schemaVersion,
                adapterId,
                ConsentVersion,
                now,
                1);
            await WriteUnderLockAsync(enrollment, cancellationToken);
            _enrollments[publicId] = enrollment;
            if (outbox is not null)
                await outbox.EnqueueAsync(new PlatformEventOutboxEvent("biometric.enrollment_created",
                    "biometrics", "info", "success", "biometric_enrollment", publicId), cancellationToken);
            return new BiometricEnrollmentResult(ToSummary(enrollment), replaced);
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync(string publicId, string reason, CancellationToken cancellationToken)
    {
        ValidatePublicId(publicId);
        if (reason is not ("owner_request" or "administrator_request"))
            throw new ArgumentException("revocation_reason_invalid", nameof(reason));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            if (!_enrollments.TryGetValue(publicId, out var existing))
                throw new BiometricEnrollmentNotFoundException(publicId);
            if (existing.Status != BiometricEnrollmentV1.ActiveStatus) return;
            var tombstone = existing with
            {
                Status = BiometricEnrollmentV1.RevokedStatus,
                RevokedAtUtc = timeProvider.GetUtcNow(),
                RevokedReason = reason,
                EmbeddingCiphertext = null,
                EmbeddingNonce = null,
                EmbeddingTag = null,
                Version = existing.Version + 1
            };
            await WriteUnderLockAsync(tombstone, cancellationToken);
            _enrollments[publicId] = tombstone;
            if (outbox is not null)
                await outbox.EnqueueAsync(new PlatformEventOutboxEvent("biometric.enrollment_revoked",
                    "biometrics", "warning", "success", "biometric_enrollment", publicId), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Erases due payloads; callable by the retention worker and by list/enroll lazily.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            return await ExpireDueUnderLockAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<int> ExpireDueUnderLockAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var due = _enrollments.Values
            .Where(item => item.Status == BiometricEnrollmentV1.ActiveStatus && item.ExpiresAtUtc <= now)
            .OrderBy(item => item.ExpiresAtUtc)
            .Take(100)
            .ToArray();
        foreach (var enrollment in due)
        {
            var expired = enrollment with
            {
                Status = BiometricEnrollmentV1.ExpiredStatus,
                RevokedAtUtc = now,
                RevokedReason = "expired",
                EmbeddingCiphertext = null,
                EmbeddingNonce = null,
                EmbeddingTag = null,
                Version = enrollment.Version + 1
            };
            await WriteUnderLockAsync(expired, cancellationToken);
            _enrollments[expired.PublicId] = expired;
        }
        return due.Length;
    }

    private async Task EnsureLoadedUnderLockAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        if (Directory.Exists(Root))
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var enrollment = await TryReadAsync(path, cancellationToken);
                if (enrollment is not null) _enrollments[enrollment.PublicId] = enrollment;
            }
        }
        _loaded = true;
    }

    private async Task WriteUnderLockAsync(BiometricEnrollmentV1 enrollment, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var destination = Path.Combine(Root, enrollment.PublicId + ".json");
        var serialized = JsonSerializer.SerializeToUtf8Bytes(enrollment, JsonOptions);
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

    private static async Task<BiometricEnrollmentV1?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var enrollment = JsonSerializer.Deserialize<BiometricEnrollmentV1>(bytes, JsonOptions);
            if (enrollment is null ||
                enrollment.SchemaVersion != BiometricEnrollmentV1.CurrentSchemaVersion ||
                !IsValidPublicId(enrollment.PublicId) ||
                Path.GetFileName(path) != enrollment.PublicId + ".json" ||
                !IsValidUserPublicId(enrollment.UserPublicId) ||
                enrollment.Version < 1 ||
                enrollment.Status is not (BiometricEnrollmentV1.ActiveStatus or BiometricEnrollmentV1.RevokedStatus
                    or BiometricEnrollmentV1.ExpiredStatus))
                return null;
            if (enrollment.Status == BiometricEnrollmentV1.ActiveStatus && !enrollment.HasPayload)
                return null;
            if (enrollment.Status != BiometricEnrollmentV1.ActiveStatus && enrollment.HasPayload)
                return null;
            return enrollment;
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

    private static BiometricEnrollmentSummary ToSummary(BiometricEnrollmentV1 enrollment) =>
        new(enrollment.PublicId, enrollment.UserPublicId, enrollment.Status, enrollment.EnrolledAtUtc,
            enrollment.ExpiresAtUtc, enrollment.RevokedAtUtc, enrollment.RevokedReason ?? string.Empty,
            enrollment.ModelId, enrollment.ModelVersion, enrollment.Dimensions, enrollment.KeyVersion,
            enrollment.ConsentVersion, enrollment.ConsentAcceptedAtUtc);

    private static string Normalize(string? value, int maximumLength, string error)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength)
            throw new ArgumentException(error);
        return normalized;
    }

    private static void ValidateUserPublicId(string userPublicId)
    {
        if (!IsValidUserPublicId(userPublicId)) throw new ArgumentException("user_id_invalid", nameof(userPublicId));
    }

    private static void ValidatePublicId(string publicId)
    {
        if (!IsValidPublicId(publicId)) throw new ArgumentException("biometric_enrollment_id_invalid", nameof(publicId));
    }

    public static bool IsValidUserPublicId(string? value) => HasPrefixAndDigits(value, "USR");

    public static bool IsValidPublicId(string? value) => HasPrefixAndDigits(value, "BIO");

    private static bool HasPrefixAndDigits(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + 12 ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        for (var index = prefix.Length; index < value.Length; index++)
            if (value[index] is < '0' or > '9')
                return false;
        return true;
    }
}

/// <summary>Periodic erasure of expired embedding payloads; the absolute expiry is persisted.</summary>
public sealed class BiometricRetentionWorker(BiometricStore store) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await store.ExpireDueAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
