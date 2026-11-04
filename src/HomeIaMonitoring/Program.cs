using HomeIaMonitoring;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(provider =>
    MonitoringOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider =>
    ServiceTokenOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>(),
        builder.Environment.IsDevelopment()));
builder.Services.AddSingleton(provider =>
    CaptureWriterOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    return CaptureOptions.FromConfiguration(configuration,
        provider.GetRequiredService<MonitoringOptions>(),
        provider.GetRequiredService<CaptureWriterOptions>());
});
builder.Services.AddSingleton(provider =>
    ArchiveOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>(),
        provider.GetRequiredService<MonitoringOptions>()));
builder.Services.AddSingleton(provider =>
    EventJournalOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider =>
    AnalysisDispatchOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SpoolWriter>();
builder.Services.AddSingleton<LivePlaylistSequence>();
builder.Services.AddSingleton<LiveStreamReader>();
builder.Services.AddSingleton<ClipMediaWriter>();
builder.Services.AddSingleton<ClipLibrary>();
builder.Services.AddSingleton<IClipPlaybackSource>(provider => provider.GetRequiredService<ClipLibrary>());
builder.Services.AddSingleton<RosterStore>();
builder.Services.AddSingleton<BiometricStore>();
builder.Services.AddSingleton<BiometricRetentionWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BiometricRetentionWorker>());
builder.Services.AddSingleton<ClipArchiveService>();
builder.Services.AddSingleton<RetentionStore>();
builder.Services.AddSingleton<ClipRetentionWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ClipRetentionWorker>());
builder.Services.AddSingleton<EventJournal>();
builder.Services.AddSingleton<AnalysisDispatchStore>();
builder.Services.AddSingleton(provider =>
    AnalysisRuntimeOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider =>
    AnalysisDetectorOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IAnalysisDetector, OnnxAnalysisDetector>();
builder.Services.AddSingleton<IAnalysisFrameReader, FfmpegAnalysisFrameReader>();
builder.Services.AddSingleton<AnalysisResultStore>();
builder.Services.AddSingleton<AnalysisWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AnalysisWorker>());
builder.Services.AddSingleton<CaptureSettingsStore>();
builder.Services.AddSingleton<CaptureAdapterFactory>();
builder.Services.AddSingleton<ICaptureAdapterFactory>(provider =>
    provider.GetRequiredService<CaptureAdapterFactory>());
builder.Services.AddSingleton<CaptureWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CaptureWorker>());
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

var serviceTokenOptions = app.Services.GetRequiredService<ServiceTokenOptions>();
app.Use(async (context, next) =>
{
    if (serviceTokenOptions.Disabled || context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var requiredScope = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
        ? "monitoring.read"
        : "monitoring.write";
    var validation = ServiceTokenValidator.Validate(context.Request.Headers.Authorization, serviceTokenOptions,
        requiredScope, DateTimeOffset.UtcNow);
    if (validation.IsValid)
    {
        await next();
        return;
    }

    var status = validation.Status == ServiceTokenStatus.InsufficientScope
        ? StatusCodes.Status403Forbidden
        : StatusCodes.Status401Unauthorized;
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error = status == StatusCodes.Status403Forbidden ? "forbidden" : "unauthorized" });
});

app.MapGet("/health/live", () => Results.Ok(new LiveResponse("live")));
app.MapGet("/health/ready", (CaptureSettingsStore settings, AnalysisRuntimeOptions analysis) =>
    Results.Ok(MonitoringContract.Ready(settings.Current.Enabled, analysis.Enabled)));
app.MapGet("/api/v1/monitoring/contract", (CaptureSettingsStore settings, AnalysisRuntimeOptions analysis) =>
    Results.Ok(MonitoringContract.Current(settings.Current.Enabled, analysis.Enabled)));
app.MapGet("/api/v1/monitoring/capabilities", (CaptureSettingsStore settings, AnalysisRuntimeOptions analysis) =>
    Results.Ok(MonitoringContract.Current(settings.Current.Enabled, analysis.Enabled)));
app.MapGet("/api/v1/monitoring/cameras/{cameraId}/status", (string cameraId, MonitoringOptions monitoring,
    TimeProvider timeProvider) =>
{
    var status = CameraStatusReader.Read(monitoring, cameraId, timeProvider.GetUtcNow());
    return Results.Json(status, statusCode: status.Status == "not_found"
        ? StatusCodes.Status404NotFound
        : StatusCodes.Status200OK);
});
app.MapGet("/api/v1/monitoring/clips",
    async (string[]? cameraIds, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? state, int? page,
        int? pageSize, ClipLibrary library, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await library.ListAsync(cameraIds ?? [], fromUtc, toUtc, state, page ?? 1,
                pageSize ?? 25, cancellationToken);
            return Results.Ok(result);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_clip_query", detail = exception.Message });
        }
    });
app.MapGet("/api/v1/monitoring/clips/{publicId}",
    async (string publicId, ClipLibrary library, CancellationToken cancellationToken) =>
    {
        var clip = await library.GetAsync(publicId, cancellationToken);
        return clip is null ? Results.NotFound() : Results.Ok(clip);
    });
app.MapGet("/api/v1/monitoring/clips/{publicId}/thumbnail",
    async (string publicId, ClipLibrary library, CancellationToken cancellationToken) =>
    {
        var path = await library.GetThumbnailPathAsync(publicId, cancellationToken);
        return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
    });
app.MapGet("/api/v1/monitoring/clips/{publicId}/media",
    async (string publicId, ClipLibrary library, CancellationToken cancellationToken) =>
    {
        var path = await library.GetPlaybackPathAsync(publicId, cancellationToken);
        return path is null
            ? Results.Json(new { error = "clip_playback_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.File(path, "video/mp4", enableRangeProcessing: true);
    });
app.MapGet("/api/v1/monitoring/live/{cameraId}/index.m3u8",
    (string cameraId, MonitoringOptions monitoring, CaptureSettingsStore settings, LiveStreamReader live,
        LivePlaylistSequence sequence) =>
    {
        if (!string.Equals(cameraId, monitoring.CameraId, StringComparison.Ordinal)) return Results.NotFound();
        var segments = live.GetRecentSegments(6);
        if (segments.Count == 0) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var target = Math.Max(1, (int)Math.Ceiling(segments.Max(item =>
            (item.Manifest.EndedAt - item.Manifest.StartedAt).TotalSeconds)));
        var epochs = segments.Select(item => item.Manifest.StreamEpoch).ToArray();
        var mediaSequence = sequence.GetStartSequence(segments.Select(item => item.Manifest.SegmentId).ToArray(),
            settings.Current.MaximumSegments);
        var playlist = new System.Text.StringBuilder().AppendLine("#EXTM3U").AppendLine("#EXT-X-VERSION:3")
            .AppendLine($"#EXT-X-TARGETDURATION:{target}").AppendLine($"#EXT-X-MEDIA-SEQUENCE:{mediaSequence}");
        for (var index = 0; index < segments.Count; index++)
        {
            if (LivePlaylistSequence.StartsDiscontinuity(epochs, index)) playlist.AppendLine("#EXT-X-DISCONTINUITY");
            var segment = segments[index];
            var duration = Math.Max(0.1, (segment.Manifest.EndedAt - segment.Manifest.StartedAt).TotalSeconds);
            playlist.AppendLine($"#EXTINF:{duration:F3},")
                .AppendLine($"segments/{segment.Manifest.SegmentId}");
        }
        return Results.Text(playlist.ToString(), "application/vnd.apple.mpegurl");
    });
app.MapGet("/api/v1/monitoring/live/{cameraId}/segments/{segmentId}",
    (string cameraId, string segmentId, MonitoringOptions monitoring, CaptureSettingsStore settings,
        LiveStreamReader live) =>
    {
        if (!string.Equals(cameraId, monitoring.CameraId, StringComparison.Ordinal)) return Results.NotFound();
        var segment = live.FindSegment(segmentId, settings.Current.MaximumSegments);
        return segment is null
            ? Results.NotFound()
            : Results.File(segment.Path, "video/mp2t", enableRangeProcessing: true);
    });
app.MapPost("/api/v1/monitoring/clips/media",
    async (ClipMediaRequest request, MonitoringOptions monitoring, ClipMediaWriter writer,
        CancellationToken cancellationToken) =>
    {
        if (!string.Equals(request.CameraId, monitoring.CameraId, StringComparison.Ordinal))
            return Results.NotFound(new { error = "camera_not_configured" });
        try
        {
            return Results.Ok(await writer.AssembleAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_clip_media", detail = exception.Message });
        }
        catch (InvalidDataException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    });
app.MapGet("/api/v1/monitoring/roster/people",
    async (int? page, int? pageSize, RosterStore roster, CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await roster.ListAsync(page ?? 1, pageSize ?? 25, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_roster_query", detail = exception.Message });
        }
    });
app.MapPost("/api/v1/monitoring/roster/people",
    async (RosterPersonRequest request, RosterStore roster, CancellationToken cancellationToken) =>
    {
        try
        {
            var created = await roster.CreateAsync(request, cancellationToken);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_roster_person", detail = exception.Message });
        }
    });
app.MapDelete("/api/v1/monitoring/roster/people/{publicId}",
    async (string publicId, RosterStore roster, CancellationToken cancellationToken) =>
    {
        try
        {
            await roster.RemoveAsync(publicId, cancellationToken);
            return Results.NoContent();
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_roster_person", detail = exception.Message });
        }
        catch (RosterPersonNotFoundException)
        {
            return Results.NotFound();
        }
    });
app.MapGet("/api/v1/monitoring/biometrics/enrollments",
    async (string? userPublicId, BiometricStore store, CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(userPublicId))
            return Results.BadRequest(new { error = "invalid_biometric_query" });
        try
        {
            return Results.Ok(await store.ListAsync(userPublicId, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_biometric_query", detail = exception.Message });
        }
    });
app.MapGet("/api/v1/monitoring/biometrics/enrollments/{publicId}",
    async (string publicId, BiometricStore store, CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await store.GetAsync(publicId, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_biometric_enrollment", detail = exception.Message });
        }
        catch (BiometricEnrollmentNotFoundException)
        {
            return Results.NotFound();
        }
    });
app.MapPost("/api/v1/monitoring/biometrics/enrollments",
    async (BiometricEnrollmentRequest request, BiometricStore store, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await store.EnrollAsync(request, cancellationToken);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_biometric_enrollment", detail = exception.Message });
        }
    });
app.MapDelete("/api/v1/monitoring/biometrics/enrollments/{publicId}",
    async (string publicId, string? reason, BiometricStore store, CancellationToken cancellationToken) =>
    {
        try
        {
            await store.RevokeAsync(publicId, reason ?? string.Empty, cancellationToken);
            return Results.NoContent();
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_biometric_enrollment", detail = exception.Message });
        }
        catch (BiometricEnrollmentNotFoundException)
        {
            return Results.NotFound();
        }
    });
app.MapGet("/api/v1/monitoring/retention/cameras/{cameraId}",
    async (string cameraId, RetentionStore store, CancellationToken cancellationToken) =>
    {
        if (!ClipArchiveService.IsSafeToken(cameraId))
            return Results.BadRequest(new { error = "invalid_clip_retention_policy" });
        return Results.Ok(await store.GetPolicyAsync(cameraId, cancellationToken));
    });
app.MapPut("/api/v1/monitoring/retention/cameras/{cameraId}",
    async (string cameraId, RetentionPolicyUpdateRequest request, RetentionStore store,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await store.UpdatePolicyAsync(cameraId, request.ExpectedVersion,
                request.RetentionDays, request.MaximumStorageGigabytes, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_clip_retention_policy", detail = exception.Message });
        }
        catch (RetentionConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    });
app.MapGet("/api/v1/monitoring/retention/preservations",
    async (string[]? clipPublicIds, RetentionStore store, CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await store.ListPreservationsAsync(clipPublicIds ?? [], cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_clip_preservation_query", detail = exception.Message });
        }
    });
app.MapPut("/api/v1/monitoring/retention/clips/{clipPublicId}/preservation",
    async (string clipPublicId, PreservationUpdateRequest request, RetentionStore store,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await store.SetPreservationAsync(clipPublicId, request.ExpectedVersion,
                request.IsPreserved, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_clip_preservation", detail = exception.Message });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "clip_not_found" });
        }
        catch (RetentionConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    });
app.MapPost("/api/v1/monitoring/events",
    async (EventJournalEnvelopeV1 envelope, EventJournal journal, CancellationToken cancellationToken) =>
    {
        try
        {
            var status = await journal.AppendAsync(envelope, cancellationToken);
            if (status == EventAppendStatus.CapacityReached)
                return Results.Json(new { error = "event_capacity_reached" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            return Results.Json(new EventJournalAppendResult(
                    status == EventAppendStatus.Created ? "created" : "duplicate", EventEnvelope.EventId(envelope),
                    envelope.EventSha256),
                statusCode: status == EventAppendStatus.Created
                    ? StatusCodes.Status201Created
                    : StatusCodes.Status200OK);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_event", detail = exception.Message });
        }
        catch (EventJournalConflictException)
        {
            return Results.Conflict(new { error = "event_id_conflict" });
        }
    });
app.MapGet("/api/v1/monitoring/events",
    async (string[]? cameraIds, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int? limit,
        EventJournal journal, CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await journal.ListAsync(cameraIds ?? [], fromUtc, toUtc, limit ?? 100,
                cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_event_query", detail = exception.Message });
        }
    });
app.MapGet("/api/v1/monitoring/events/recovery",
    async (EventJournal journal, CancellationToken cancellationToken) =>
        Results.Ok(await journal.RecoverAsync(cancellationToken)));
app.MapGet("/api/v1/monitoring/analysis/policies/{cameraId}",
    async (string cameraId, AnalysisDispatchStore store, CancellationToken cancellationToken) =>
    {
        var policy = await store.GetPolicyAsync(cameraId, cancellationToken);
        return policy is null ? Results.NotFound() : Results.Ok(policy);
    });
app.MapPut("/api/v1/monitoring/analysis/policies/{cameraId}",
    async (string cameraId, AnalysisPolicyUpdateRequest request, AnalysisDispatchStore store,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await store.UpdatePolicyAsync(cameraId, request.ContextId, request.ExpectedVersion,
                request.Policy, request.ActorId, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_analysis_policy", detail = exception.Message });
        }
        catch (AnalysisConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    });
app.MapPost("/api/v1/monitoring/analysis/jobs",
    async (AnalysisEnqueueRequest request, AnalysisDispatchStore store, CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await store.EnqueueAsync(request, cancellationToken);
            return Results.Json(result,
                statusCode: result.Existing ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_analysis_request", detail = exception.Message });
        }
        catch (AnalysisConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (AnalysisDeniedException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (AnalysisCapacityException)
        {
            return Results.Json(new { error = "analysis_capacity_reached" },
                statusCode: StatusCodes.Status429TooManyRequests);
        }
    });
app.MapPost("/api/v1/monitoring/analysis/jobs/claim",
    async (AnalysisDispatchStore store, CancellationToken cancellationToken) =>
    {
        var job = await store.ClaimNextAsync(cancellationToken);
        return job is null ? Results.NoContent() : Results.Ok(job);
    });
app.MapGet("/api/v1/monitoring/analysis/recovery",
    async (AnalysisDispatchStore store, CancellationToken cancellationToken) =>
        Results.Ok(await store.RecoverAsync(cancellationToken)));
app.MapGet("/api/v1/monitoring/analysis/jobs/{publicId}",
    async (string publicId, AnalysisDispatchStore store, CancellationToken cancellationToken) =>
    {
        var job = await store.GetJobAsync(publicId, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    });
app.MapGet("/api/v1/monitoring/analysis/clips",
    async (string[]? clipPublicIds, AnalysisDispatchStore store, CancellationToken cancellationToken) =>
    {
        if (clipPublicIds is null || clipPublicIds.Length is 0 or > 100 ||
            clipPublicIds.Any(id => id.Length is 0 or > 128))
            return Results.BadRequest(new { error = "invalid_analysis_query" });
        return Results.Ok(await store.LatestForClipsAsync(clipPublicIds, cancellationToken));
    });
app.MapGet("/api/v1/monitoring/analysis/results/{clipPublicId}",
    async (string clipPublicId, AnalysisResultStore results, CancellationToken cancellationToken) =>
    {
        var result = await results.ReadAsync(clipPublicId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    });
app.MapGet("/api/v1/monitoring/capture", (MonitoringOptions monitoring, CaptureOptions capture,
    CaptureSettingsStore settings, SpoolWriter writer, CaptureWorker worker) =>
{
    var current = settings.Current;
    return Results.Ok(new CaptureResponse(MonitoringContract.SchemaVersion, capture.Profile, monitoring.CameraId,
        current.Enabled, current.SegmentSeconds, current.MaximumSegments, current.MaximumSegmentBytes,
        current.Revision, worker.Status, writer.CountGaps(), writer.ReadManifests().Count));
});
app.MapPut("/api/v1/monitoring/capture/settings",
    async (CaptureSettingsRequest request, CaptureSettingsStore settings, CancellationToken cancellationToken) =>
    {
        try
        {
            var updated = await settings.UpdateAsync(request.Enabled, request.SegmentSeconds,
                request.MaximumSegments, request.MaximumSegmentBytes, cancellationToken);
            return Results.Ok(new CaptureSettingsResponse(updated.Enabled, updated.SegmentSeconds,
                updated.MaximumSegments, updated.MaximumSegmentBytes, updated.Revision));
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = "capture_settings_invalid", detail = exception.Message });
        }
    });

app.Run();

public partial class Program;

public sealed record LiveResponse(string Status);

public static class EventEnvelope
{
    public static string EventId(EventJournalEnvelopeV1 envelope) =>
        envelope.Event.TryGetProperty("eventId", out var value) ? value.GetString() ?? string.Empty : string.Empty;
}

public sealed record CaptureSettingsRequest(bool Enabled, int SegmentSeconds, int MaximumSegments,
    long MaximumSegmentBytes);

public sealed record AnalysisPolicyUpdateRequest(string ContextId, long ExpectedVersion, string Policy,
    string ActorId);

public sealed record RetentionPolicyUpdateRequest(long ExpectedVersion, int RetentionDays,
    int? MaximumStorageGigabytes);

public sealed record PreservationUpdateRequest(long ExpectedVersion, bool IsPreserved);

public sealed record CaptureSettingsResponse(bool Enabled, int SegmentSeconds, int MaximumSegments,
    long MaximumSegmentBytes, long Revision);

public sealed record CaptureResponse(string SchemaVersion, string Profile, string CameraId, bool Enabled,
    int SegmentSeconds, int MaximumSegments, long MaximumSegmentBytes, long Revision, CaptureStatus Status,
    long Gaps, int Segments);

public static class MonitoringContract
{
    public const string SchemaVersion = "1.0";
    public const string ModuleId = "monitoring";
    public const string ContractVersion = "1.0";
    public const string ExecutionMode = "status-read-only";

    private static readonly string[] BaseCapabilities =
        ["analysis.dispatch", "analysis.read", "biometrics.read", "biometrics.write", "camera.status.read",
            "capture.status.read", "capture.settings", "clips.read", "clips.write", "events.read", "events.write",
            "health", "live.hls.read", "media.contract", "retention.read", "retention.write", "roster.read",
            "roster.write"];

    public static MonitoringModuleResponse Current(bool captureEnabled, bool analysisEnabled = false) => new(
        SchemaVersion, ModuleId, ContractVersion, captureEnabled ? "capture-enabled" : ExecutionMode,
        Capabilities(captureEnabled, analysisEnabled), DateTimeOffset.UtcNow);

    public static MonitoringModuleResponse Ready(bool captureEnabled, bool analysisEnabled = false) =>
        Current(captureEnabled, analysisEnabled) with
        {
            ExecutionMode = captureEnabled ? "capture-ready" : "status-ready"
        };

    private static IReadOnlyList<string> Capabilities(bool captureEnabled, bool analysisEnabled)
    {
        var capabilities = new List<string>(BaseCapabilities);
        if (captureEnabled) capabilities.Add("capture.write");
        if (analysisEnabled) capabilities.Add("analysis.execute");
        return capabilities;
    }
}

public sealed record MonitoringModuleResponse(string SchemaVersion, string ModuleId, string ContractVersion,
    string ExecutionMode, IReadOnlyList<string> Capabilities, DateTimeOffset GeneratedAtUtc);
