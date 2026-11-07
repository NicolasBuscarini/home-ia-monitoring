using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

public sealed record AnalysisDispatchOptions(int QueueCapacity, int LeaseSeconds, string DefaultAnalysisVersion)
{
    public const string QueueCapacityKey = "MONITORING_ANALYSIS_QUEUE_CAPACITY";
    public const string LeaseSecondsKey = "MONITORING_ANALYSIS_LEASE_SECONDS";
    public const string DefaultAnalysisVersionKey = "MONITORING_ANALYSIS_DEFAULT_VERSION";

    public static AnalysisDispatchOptions FromConfiguration(IConfiguration configuration)
    {
        var capacity = configuration.GetValue<int?>(QueueCapacityKey) ?? 8;
        var leaseSeconds = configuration.GetValue<int?>(LeaseSecondsKey) ?? 60;
        var version = configuration[DefaultAnalysisVersionKey]?.Trim() ?? "analysis-v1";
        if (capacity is < 1 or > 64 || leaseSeconds is < 30 or > 300 || !IsSafeVersion(version))
            throw new InvalidOperationException("A configuração de despacho de análise é inválida.");
        return new(capacity, leaseSeconds, version);
    }

    public static bool IsSafeVersion(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed record AnalysisPolicyV1(string SchemaVersion, string CameraId, string ContextId, string Policy,
    long Version, string UpdatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record AnalysisPolicyResult(string CameraId, string Policy, long Version, DateTimeOffset UpdatedAtUtc);

public sealed record AnalysisEnqueueRequest(string ClipId, string ClipPublicId, string CameraId, string ContextId,
    Guid IdempotencyKey, string AnalysisVersion, string RequestKind);

public sealed record AnalysisJobV1(string SchemaVersion, Guid Id, string PublicId, string ClipId,
    string ClipPublicId, string CameraId, string ContextId, Guid IdempotencyKey, string AnalysisVersion,
    long PolicyVersion, string State, string RequestKind, Guid? LeaseId, DateTimeOffset? LeaseExpiresAtUtc,
    int AttemptCount, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string? Outcome = null,
    string? ResultSha256 = null, string? FailureCode = null);

public sealed record AnalysisJobResult(string PublicId, string ClipPublicId, string AnalysisVersion, string State,
    bool Existing);

/// <summary>Read model exposed to the portal. Does not expose the lease or idempotency key.</summary>
public sealed record AnalysisJobView(string PublicId, string ClipPublicId, string CameraId, string AnalysisVersion,
    string State, string RequestKind, int AttemptCount, string? Outcome, string? ResultSha256,
    string? FailureCode, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record AnalysisRecoveryResult(int ValidPolicies, int ValidJobs, int InvalidFiles, long Bytes);

public sealed class AnalysisConflictException(string code) : Exception(code);

public sealed class AnalysisDeniedException(string code) : Exception(code);

public sealed class AnalysisCapacityException : Exception;

/// <summary>
/// Operational store for clip-analysis dispatch. Policies and jobs are written atomically to the
/// shared spool so the portal can proxy them without owning the queue. The service is a single
/// instance; the in-process gate serializes claim, capacity and policy transitions.
/// </summary>
public sealed class AnalysisDispatchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ValidPolicies = ["always", "large_motion", "manual_only", "never"];
    private readonly MonitoringOptions monitoring;
    private readonly AnalysisDispatchOptions options;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, AnalysisPolicyV1> policies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalysisJobV1> jobs = new(StringComparer.Ordinal);
    private bool loaded;

    public AnalysisDispatchStore(MonitoringOptions monitoring, AnalysisDispatchOptions options,
        TimeProvider timeProvider)
    {
        this.monitoring = monitoring;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    public async Task<AnalysisPolicyResult?> GetPolicyAsync(string cameraId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return policies.TryGetValue(cameraId, out var policy) ? Map(policy) : null;
        }
        finally { gate.Release(); }
    }

    public async Task<AnalysisPolicyResult> UpdatePolicyAsync(string cameraId, string contextId,
        long expectedVersion, string policy, string updatedByUserId, CancellationToken cancellationToken)
    {
        if (!IsSafeToken(cameraId) || !IsSafeToken(contextId) || !IsSafeToken(updatedByUserId) ||
            expectedVersion < 0 || !ValidPolicies.Contains(policy))
            throw new ArgumentException("Política de análise inválida.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var currentVersion = policies.TryGetValue(cameraId, out var current) ? current.Version : 0;
            if (currentVersion != expectedVersion) throw new AnalysisConflictException("policy_version_conflict");
            var now = timeProvider.GetUtcNow();
            var updated = new AnalysisPolicyV1("1.0", cameraId, contextId, policy, checked(currentVersion + 1),
                updatedByUserId, current?.CreatedAtUtc ?? now, now);
            await WriteAtomicAsync(PolicyPath(cameraId), updated, cancellationToken);
            policies[cameraId] = updated;
            return Map(updated);
        }
        finally { gate.Release(); }
    }

    public async Task<AnalysisJobResult> EnqueueAsync(AnalysisEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(request.ClipId) || !IsSafeToken(request.ClipPublicId) ||
            !IsSafeToken(request.CameraId) || !IsSafeToken(request.ContextId) ||
            request.IdempotencyKey == Guid.Empty || !AnalysisDispatchOptions.IsSafeVersion(request.AnalysisVersion) ||
            request.RequestKind is not ("manual" or "automatic"))
            throw new ArgumentException("Pedido de análise inválido.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            await ReconcileExpiredAsync(now, cancellationToken);
            var policy = policies.GetValueOrDefault(request.CameraId);
            var effectivePolicy = policy?.Policy ?? "manual_only";
            if (effectivePolicy == "never" ||
                (request.RequestKind == "automatic" && effectivePolicy == "manual_only"))
                throw new AnalysisDeniedException("analysis_not_allowed");
            var byKey = jobs.Values.SingleOrDefault(job => job.ClipId == request.ClipId &&
                job.IdempotencyKey == request.IdempotencyKey);
            if (byKey is not null)
            {
                if (!string.Equals(byKey.AnalysisVersion, request.AnalysisVersion, StringComparison.Ordinal))
                    throw new AnalysisConflictException("idempotency_conflict");
                return Map(byKey, true);
            }
            var existing = jobs.Values.SingleOrDefault(job => job.ClipId == request.ClipId &&
                job.AnalysisVersion == request.AnalysisVersion && job.State != "failed");
            if (existing is not null) return Map(existing, true);
            var active = jobs.Values.Count(job => job.State is "analysis_pending" or "processing");
            if (active >= options.QueueCapacity) throw new AnalysisCapacityException();
            var publicId = NewPublicId();
            while (jobs.ContainsKey(publicId)) publicId = NewPublicId();
            var created = new AnalysisJobV1("1.0", Guid.NewGuid(), publicId, request.ClipId,
                request.ClipPublicId, request.CameraId, request.ContextId, request.IdempotencyKey,
                request.AnalysisVersion, policy?.Version ?? 0, "analysis_pending", request.RequestKind, null, null,
                0, now, now);
            await WriteAtomicAsync(JobPath(created.PublicId), created, cancellationToken);
            jobs[created.PublicId] = created;
            return Map(created, false);
        }
        finally { gate.Release(); }
    }

    public async Task<AnalysisJobResult?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimNextWithLeaseAsync(cancellationToken);
        return claimed is null
            ? null
            : new AnalysisJobResult(claimed.Value.Job.PublicId, claimed.Value.Job.ClipPublicId,
                claimed.Value.Job.AnalysisVersion, claimed.Value.Job.State, false);
    }

    /// <summary>Claim used by the local worker; the lease is required for a terminal transition.</summary>
    public async Task<(AnalysisJobView Job, Guid LeaseId)?> ClaimNextWithLeaseAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            await ReconcileExpiredAsync(now, cancellationToken);
            var pending = jobs.Values.Where(job => job.State == "analysis_pending")
                .OrderBy(job => job.CreatedAtUtc).ThenBy(job => job.PublicId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (pending is null) return null;
            var leaseId = Guid.NewGuid();
            var claimed = pending with
            {
                State = "processing",
                LeaseId = leaseId,
                LeaseExpiresAtUtc = now.AddSeconds(options.LeaseSeconds),
                AttemptCount = checked(pending.AttemptCount + 1),
                UpdatedAtUtc = now
            };
            await WriteAtomicAsync(JobPath(claimed.PublicId), claimed, cancellationToken);
            jobs[claimed.PublicId] = claimed;
            return (MapView(claimed), leaseId);
        }
        finally { gate.Release(); }
    }

    public async Task<AnalysisJobView?> GetJobAsync(string publicId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return jobs.TryGetValue(publicId, out var job) ? MapView(job) : null;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AnalysisJobView>> LatestForClipsAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return jobs.Values.Where(job => clipPublicIds.Contains(job.ClipPublicId))
                .GroupBy(job => job.ClipPublicId, StringComparer.Ordinal)
                .Select(group => MapView(group.OrderByDescending(job => job.CreatedAtUtc).First()))
                .ToArray();
        }
        finally { gate.Release(); }
    }

    /// <summary>Terminal transition guarded by the lease that the worker received when claiming.</summary>
    public async Task<AnalysisJobView> CompleteAsync(string publicId, Guid leaseId, string outcome,
        string? resultSha256, string? failureCode, CancellationToken cancellationToken)
    {
        if (outcome is not ("completed" or "failed") ||
            (outcome == "completed" && (resultSha256 is null || !IsSha256(resultSha256))) ||
            (resultSha256 is not null && !IsSha256(resultSha256)))
            throw new ArgumentException("Resultado de análise inválido.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (!jobs.TryGetValue(publicId, out var job) || job.State != "processing" || job.LeaseId != leaseId)
                throw new AnalysisConflictException("analysis_lease_lost");
            var now = timeProvider.GetUtcNow();
            var completed = job with
            {
                State = outcome,
                LeaseId = null,
                LeaseExpiresAtUtc = null,
                Outcome = outcome,
                ResultSha256 = resultSha256,
                FailureCode = failureCode,
                UpdatedAtUtc = now
            };
            await WriteAtomicAsync(JobPath(completed.PublicId), completed, cancellationToken);
            jobs[completed.PublicId] = completed;
            return MapView(completed);
        }
        finally { gate.Release(); }
    }

    /// <summary>Returns the job to the queue when a retry is allowed; otherwise records a terminal failure.</summary>
    public async Task<AnalysisJobView> FailAsync(string publicId, Guid leaseId, string failureCode, bool retryable,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(failureCode)) throw new ArgumentException("Código de falha inválido.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (!jobs.TryGetValue(publicId, out var job) || job.State != "processing" || job.LeaseId != leaseId)
                throw new AnalysisConflictException("analysis_lease_lost");
            var now = timeProvider.GetUtcNow();
            var updated = retryable
                ? job with
                {
                    State = "analysis_pending",
                    LeaseId = null,
                    LeaseExpiresAtUtc = null,
                    FailureCode = failureCode,
                    UpdatedAtUtc = now
                }
                : job with
                {
                    State = "failed",
                    LeaseId = null,
                    LeaseExpiresAtUtc = null,
                    Outcome = "failed",
                    FailureCode = failureCode,
                    UpdatedAtUtc = now
                };
            await WriteAtomicAsync(JobPath(updated.PublicId), updated, cancellationToken);
            jobs[updated.PublicId] = updated;
            return MapView(updated);
        }
        finally { gate.Release(); }
    }

    public async Task<AnalysisRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            policies.Clear();
            jobs.Clear();
            loaded = false;
            var validPolicies = 0;
            var validJobs = 0;
            var invalid = 0;
            long bytes = 0;
            foreach (var directory in new[] { PoliciesDirectory, JobsDirectory })
            {
                Directory.CreateDirectory(directory);
                foreach (var temporary in Directory.EnumerateFiles(directory, "*.tmp"))
                    File.Delete(temporary);
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                {
                    bytes += new FileInfo(path).Length;
                    try
                    {
                        var json = await File.ReadAllTextAsync(path, cancellationToken);
                        if (directory == PoliciesDirectory)
                        {
                            var policy = JsonSerializer.Deserialize<AnalysisPolicyV1>(json, JsonOptions);
                            if (policy is null || policy.SchemaVersion != "1.0" ||
                                !IsSafeToken(policy.CameraId) || !ValidPolicies.Contains(policy.Policy) ||
                                Path.GetFileNameWithoutExtension(path) != policy.CameraId)
                            {
                                invalid++;
                                continue;
                            }
                            policies[policy.CameraId] = policy;
                            validPolicies++;
                        }
                        else
                        {
                            var job = JsonSerializer.Deserialize<AnalysisJobV1>(json, JsonOptions);
                            if (job is null || job.SchemaVersion != "1.0" || !IsSafeToken(job.PublicId) ||
                                !IsSafeToken(job.ClipId) || Path.GetFileNameWithoutExtension(path) != job.PublicId ||
                                job.State is not ("analysis_pending" or "processing" or "completed" or "failed"))
                            {
                                invalid++;
                                continue;
                            }
                            jobs[job.PublicId] = job;
                            validJobs++;
                        }
                    }
                    catch (IOException) { invalid++; }
                    catch (JsonException) { invalid++; }
                }
            }
            loaded = true;
            return new AnalysisRecoveryResult(validPolicies, validJobs, invalid, bytes);
        }
        finally { gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        Directory.CreateDirectory(PoliciesDirectory);
        Directory.CreateDirectory(JobsDirectory);
        foreach (var temporary in Directory.EnumerateFiles(PoliciesDirectory, "*.tmp")
                     .Concat(Directory.EnumerateFiles(JobsDirectory, "*.tmp")))
            File.Delete(temporary);
        foreach (var path in Directory.EnumerateFiles(PoliciesDirectory, "*.json"))
        {
            try
            {
                var policy = JsonSerializer.Deserialize<AnalysisPolicyV1>(
                    await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (policy is { SchemaVersion: "1.0" } && IsSafeToken(policy.CameraId) &&
                    ValidPolicies.Contains(policy.Policy)) policies[policy.CameraId] = policy;
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        foreach (var path in Directory.EnumerateFiles(JobsDirectory, "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<AnalysisJobV1>(
                    await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (job is { SchemaVersion: "1.0" } && IsSafeToken(job.PublicId) &&
                    job.State is "analysis_pending" or "processing" or "completed" or "failed")
                    jobs[job.PublicId] = job;
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        loaded = true;
    }

    public async Task<IReadOnlySet<string>> ListActiveClipIdsAsync(string cameraId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return jobs.Values.Where(job => job.CameraId == cameraId &&
                    job.State is "analysis_pending" or "processing")
                .Select(job => job.ClipId).ToHashSet(StringComparer.Ordinal);
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var job in jobs.Values.Where(job => job.State == "processing" &&
                     job.LeaseExpiresAtUtc is not null && job.LeaseExpiresAtUtc < now).ToArray())
        {
            var pending = job with
            {
                State = "analysis_pending",
                LeaseId = null,
                LeaseExpiresAtUtc = null,
                UpdatedAtUtc = now
            };
            await WriteAtomicAsync(JobPath(pending.PublicId), pending, cancellationToken);
            jobs[pending.PublicId] = pending;
        }
    }

    private async Task WriteAtomicAsync<T>(string destination, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string Root => Path.Combine(monitoring.SpoolDirectory, "analysis");
    private string PoliciesDirectory => Path.Combine(Root, "policies");
    private string JobsDirectory => Path.Combine(Root, "jobs");
    private string PolicyPath(string cameraId) => Path.Combine(PoliciesDirectory, cameraId + ".json");
    private string JobPath(string publicId) => Path.Combine(JobsDirectory, publicId + ".json");
    private static AnalysisPolicyResult Map(AnalysisPolicyV1 policy) =>
        new(policy.CameraId, policy.Policy, policy.Version, policy.UpdatedAtUtc);
    private static AnalysisJobResult Map(AnalysisJobV1 job, bool existing) =>
        new(job.PublicId, job.ClipPublicId, job.AnalysisVersion, job.State, existing);
    private static AnalysisJobView MapView(AnalysisJobV1 job) =>
        new(job.PublicId, job.ClipPublicId, job.CameraId, job.AnalysisVersion, job.State, job.RequestKind,
            job.AttemptCount, job.Outcome, job.ResultSha256, job.FailureCode, job.CreatedAtUtc, job.UpdatedAtUtc);
    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string NewPublicId() => "JOB" + Random.Shared.NextInt64(0, 1_000_000_000_000).ToString("D12");
    private static bool IsSafeToken(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
