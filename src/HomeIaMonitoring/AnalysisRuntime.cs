using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HomeIaMonitoring;

/// <summary>Detector adapter configuration. Only used when analysis execution is explicitly enabled.</summary>
public sealed record AnalysisDetectorOptions(string ModelPath, string ModelVersion, string InputName,
    string OutputName, int Width, int Height, float ScoreThreshold, string[] Labels, int IntraOpThreads,
    int InterOpThreads, string Adapter, float NmsThreshold, string ModelSha256)
{
    public const string ModelPathKey = "MONITORING_DETECTOR_ONNX_MODEL_PATH";
    public const string ModelVersionKey = "MONITORING_DETECTOR_MODEL_VERSION";
    public const string ModelSha256Key = "MONITORING_DETECTOR_ONNX_MODEL_SHA256";
    public const string InputNameKey = "MONITORING_DETECTOR_ONNX_INPUT_NAME";
    public const string OutputNameKey = "MONITORING_DETECTOR_ONNX_OUTPUT_NAME";
    public const string WidthKey = "MONITORING_DETECTOR_INPUT_WIDTH";
    public const string HeightKey = "MONITORING_DETECTOR_INPUT_HEIGHT";
    public const string ScoreThresholdKey = "MONITORING_DETECTOR_SCORE_THRESHOLD";
    public const string NmsThresholdKey = "MONITORING_DETECTOR_NMS_THRESHOLD";
    public const string LabelsKey = "MONITORING_DETECTOR_LABELS";
    public const string AdapterKey = "MONITORING_DETECTOR_ADAPTER";
    public const string IntraOpThreadsKey = "MONITORING_DETECTOR_INTRA_OP_THREADS";
    public const string InterOpThreadsKey = "MONITORING_DETECTOR_INTER_OP_THREADS";

    public const string FlatAdapter = "flat";
    public const string YoloxAdapter = "yolox";

    /// <summary>COCO class order used by YOLOX. <c>MONITORING_DETECTOR_LABELS=coco</c> expands to it.</summary>
    public static readonly string[] CocoLabels =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
        "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
        "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
        "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
        "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
        "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard",
        "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase",
        "scissors", "teddy bear", "hair drier", "toothbrush"
    ];

    public bool Enabled => !string.IsNullOrWhiteSpace(ModelPath);

    public static AnalysisDetectorOptions FromConfiguration(IConfiguration configuration)
    {
        var configuredLabels = configuration[LabelsKey]?.Trim() ?? string.Empty;
        var labels = configuredLabels.Equals("coco", StringComparison.OrdinalIgnoreCase)
            ? CocoLabels
            : configuredLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = new AnalysisDetectorOptions(
            configuration[ModelPathKey]?.Trim() ?? string.Empty,
            configuration[ModelVersionKey]?.Trim() ?? "detector-v1",
            configuration[InputNameKey]?.Trim() ?? "images",
            configuration[OutputNameKey]?.Trim() ?? "output0",
            configuration.GetValue<int?>(WidthKey) ?? 640,
            configuration.GetValue<int?>(HeightKey) ?? 640,
            configuration.GetValue<float?>(ScoreThresholdKey) ?? 0.35f,
            labels,
            configuration.GetValue<int?>(IntraOpThreadsKey) ?? 1,
            configuration.GetValue<int?>(InterOpThreadsKey) ?? 1,
            configuration[AdapterKey]?.Trim().ToLowerInvariant() ?? FlatAdapter,
            configuration.GetValue<float?>(NmsThresholdKey) ?? 0.45f,
            configuration[ModelSha256Key]?.Trim().ToLowerInvariant() ?? string.Empty);
        if (options.Enabled && (options.ModelPath.Length > 512 || !Path.IsPathFullyQualified(options.ModelPath) ||
            options.ModelVersion.Length is 0 or > 64 || options.InputName.Length is 0 or > 128 ||
            options.OutputName.Length is 0 or > 128 || options.Width is < 32 or > 4096 ||
            options.Height is < 32 or > 4096 || options.ScoreThreshold is <= 0 or > 1 ||
            options.Labels.Length is 0 or > 512 || options.Labels.Any(label => label.Length is 0 or > 64) ||
            options.IntraOpThreads is < 1 or > 32 || options.InterOpThreads is < 1 or > 32 ||
            options.Adapter is not (FlatAdapter or YoloxAdapter) || options.NmsThreshold is <= 0 or > 1 ||
            options.ModelSha256 is { Length: > 0 } hash && (hash.Length != 64 ||
                !hash.All(character => char.IsAsciiHexDigit(character)))))
            throw new InvalidOperationException("A configuração do detector de análise é inválida.");
        return options;
    }
}

public sealed record AnalysisRuntimeOptions(bool Enabled, int PollSeconds, int MaximumFrames, int MaximumAttempts,
    AnalysisDetectorOptions Detector)
{
    public const string EnabledKey = "MONITORING_ANALYSIS_ENABLED";
    public const string PollSecondsKey = "MONITORING_ANALYSIS_POLL_SECONDS";
    public const string MaximumFramesKey = "MONITORING_ANALYSIS_MAX_FRAMES";
    public const string MaximumAttemptsKey = "MONITORING_ANALYSIS_MAX_ATTEMPTS";

    public static AnalysisRuntimeOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new AnalysisRuntimeOptions(
            configuration.GetValue<bool?>(EnabledKey) ?? false,
            configuration.GetValue<int?>(PollSecondsKey) ?? 5,
            configuration.GetValue<int?>(MaximumFramesKey) ?? 12,
            configuration.GetValue<int?>(MaximumAttemptsKey) ?? 2,
            AnalysisDetectorOptions.FromConfiguration(configuration));
        if (options.PollSeconds is < 1 or > 60 || options.MaximumFrames is < 1 or > 120 ||
            options.MaximumAttempts is < 1 or > 5 || (options.Enabled && !options.Detector.Enabled))
            throw new InvalidOperationException("A configuração do runtime de análise é inválida.");
        return options;
    }
}

public sealed record AnalysisFrame(int Index, string CameraId, string StreamEpoch, DateTimeOffset CapturedAt,
    int Width, int Height, ReadOnlyMemory<float> RgbNchw);

public sealed record AnalysisDetection(string ClassName, int ClassId, float Score, float X1, float Y1, float X2,
    float Y2)
{
    public bool IsValid => float.IsFinite(Score) && Score is >= 0 and <= 1 && ClassId >= 0 &&
        ClassName.Length is > 0 and <= 64 && float.IsFinite(X1) && float.IsFinite(Y1) && float.IsFinite(X2) &&
        float.IsFinite(Y2) && X1 >= 0 && Y1 >= 0 && X2 <= 1 && Y2 <= 1 && X2 > X1 && Y2 > Y1;
}

public sealed record AnalysisObject(string ClassName, int ClassId, float Score, float X1, float Y1, float X2,
    float Y2, int FrameIndex);

public sealed record AnalysisClipResultV1(string SchemaVersion, string ClipPublicId, string CameraId,
    string AnalysisVersion, string ModelVersion, int FramesAnalyzed, IReadOnlyList<AnalysisObject> Objects,
    DateTimeOffset ProcessedAtUtc);

public sealed record AnalysisClipDescriptor(string ClipPublicId, string CameraId, string StreamEpoch,
    string PlaybackPath, DateTimeOffset CapturedAtUtc);

public interface IAnalysisDetector
{
    string ModelVersion { get; }
    ValueTask<IReadOnlyList<AnalysisDetection>> DetectAsync(AnalysisFrame frame, CancellationToken cancellationToken);
}

public interface IAnalysisFrameReader
{
    IAsyncEnumerable<AnalysisFrame> ReadAsync(AnalysisClipDescriptor clip, int maximumFrames,
        CancellationToken cancellationToken);
}

/// <summary>Versioned analysis result persisted under the shared spool; the portal never receives media.</summary>
public sealed class AnalysisResultStore(MonitoringOptions monitoring)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string SchemaVersion = "analysis-result.v1";

    public async Task<(string Sha256, long Bytes)> WriteAsync(AnalysisClipResultV1 result,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(result.ClipPublicId) || result.SchemaVersion != SchemaVersion)
            throw new ArgumentException("Resultado de análise inválido.");
        var destination = PathFor(result.ClipPublicId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return (sha, bytes.LongLength);
    }

    public async Task<AnalysisClipResultV1?> ReadAsync(string clipPublicId, CancellationToken cancellationToken)
    {
        if (!IsSafeToken(clipPublicId)) return null;
        var path = PathFor(clipPublicId);
        if (!File.Exists(path)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<AnalysisClipResultV1>(
                await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            return result is { SchemaVersion: SchemaVersion, ClipPublicId: var id } &&
                id == clipPublicId && IsSafeToken(result.CameraId) ? result : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private string PathFor(string clipPublicId) =>
        Path.Combine(monitoring.SpoolDirectory, "analysis", "results", clipPublicId + ".json");

    private static bool IsSafeToken(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

/// <summary>ONNX Runtime adapter. The session is created lazily so a disabled runtime never loads weights.</summary>
public sealed class OnnxAnalysisDetector : IAnalysisDetector, IDisposable
{
    private readonly AnalysisDetectorOptions options;
    private readonly Lazy<InferenceSession?> session;
    private string inputName;
    private string outputName;

    public OnnxAnalysisDetector(AnalysisDetectorOptions options)
    {
        this.options = options;
        inputName = options.InputName;
        outputName = options.OutputName;
        session = new Lazy<InferenceSession?>(() =>
        {
            if (!options.Enabled) return null;
            if (!File.Exists(options.ModelPath)) throw new InvalidOperationException("detector_model_unavailable");
            if (options.ModelSha256.Length > 0)
            {
                using var stream = File.OpenRead(options.ModelPath);
                var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (!string.Equals(actual, options.ModelSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("detector_model_checksum_mismatch");
            }
            var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                IntraOpNumThreads = options.IntraOpThreads,
                InterOpNumThreads = options.InterOpThreads,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
            using var lifetime = sessionOptions;
            var created = new InferenceSession(options.ModelPath, sessionOptions);
            var resolvedInput = created.InputMetadata.Keys.FirstOrDefault(key =>
                string.Equals(key.Trim(), options.InputName.Trim(), StringComparison.OrdinalIgnoreCase));
            var resolvedOutput = created.OutputMetadata.Keys.FirstOrDefault(key =>
                string.Equals(key.Trim(), options.OutputName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (resolvedInput is null || resolvedOutput is null)
            {
                var inputs = string.Join(",", created.InputMetadata.Keys);
                var outputs = string.Join(",", created.OutputMetadata.Keys);
                created.Dispose();
                throw new InvalidOperationException(
                    $"detector_io_incompatible:inputs={inputs};outputs={outputs};" +
                    $"configuredInput={options.InputName};configuredOutput={options.OutputName}");
            }
            inputName = resolvedInput;
            outputName = resolvedOutput;
            return created;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ModelVersion => options.ModelVersion;

    public ValueTask<IReadOnlyList<AnalysisDetection>> DetectAsync(AnalysisFrame frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = session.Value ?? throw new InvalidOperationException("detector_not_configured");
        if (frame.Width != options.Width || frame.Height != options.Height ||
            frame.RgbNchw.Length != checked(3 * options.Width * options.Height))
            throw new InvalidDataException("detector_input_shape_invalid");
        var input = frame.RgbNchw.ToArray();
        if (options.Adapter == AnalysisDetectorOptions.YoloxAdapter)
        {
            var plane = options.Width * options.Height;
            for (var index = 0; index < plane; index++)
                (input[index], input[2 * plane + index]) = (input[2 * plane + index], input[index]);
        }
        var tensor = new DenseTensor<float>(input, [1, 3, options.Height, options.Width]);
        using var results = active.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)],
            [outputName]);
        cancellationToken.ThrowIfCancellationRequested();
        var output = results.First().AsTensor<float>();
        var values = output.ToArray();
        IReadOnlyList<AnalysisDetection> detections = options.Adapter == AnalysisDetectorOptions.YoloxAdapter
            ? DecodeYolox(values, options.Width, options.Height, options.ScoreThreshold, options.NmsThreshold,
                options.Labels, 256)
            : DecodeFlat(values, options.ScoreThreshold, options.Labels, 256);
        return ValueTask.FromResult(detections);
    }

    /// <summary>Decodes the contract used by the test graph: one row of x1,y1,x2,y2,score,class.</summary>
    internal static IReadOnlyList<AnalysisDetection> DecodeFlat(float[] values, float scoreThreshold, string[] labels,
        int maximumDetections)
    {
        if (values.Length % 6 != 0 || values.Length > 6 * 10000)
            throw new InvalidDataException("detector_output_shape_invalid");
        var detections = new List<AnalysisDetection>(Math.Min(values.Length / 6, maximumDetections));
        for (var offset = 0; offset < values.Length; offset += 6)
        {
            var classValue = values[offset + 5];
            if (!float.IsFinite(classValue) || classValue < 0 || classValue != MathF.Truncate(classValue) ||
                classValue >= labels.Length || values[offset + 4] < scoreThreshold) continue;
            var detection = new AnalysisDetection(labels[(int)classValue], (int)classValue,
                values[offset + 4], values[offset], values[offset + 1], values[offset + 2], values[offset + 3]);
            if (!detection.IsValid) continue;
            detections.Add(detection);
            if (detections.Count == maximumDetections) break;
        }
        return detections;
    }

    /// <summary>Decodes YOLOX raw output [1, N, 5 + classes] for the fixed strides 8/16/32.</summary>
    internal static IReadOnlyList<AnalysisDetection> DecodeYolox(float[] values, int width, int height,
        float scoreThreshold, float nmsThreshold, string[] labels, int maximumDetections)
    {
        const int rowSize = 85;
        var strides = new[] { 8, 16, 32 };
        var expectedRows = strides.Sum(stride => (width / stride) * (height / stride));
        if (labels.Length < rowSize - 5 || values.Length != expectedRows * rowSize)
            throw new InvalidDataException("detector_output_shape_invalid");

        var candidates = new List<AnalysisDetection>(Math.Min(expectedRows, 1024));
        var row = 0;
        foreach (var stride in strides)
        {
            var gridWidth = width / stride;
            var gridHeight = height / stride;
            for (var gridY = 0; gridY < gridHeight; gridY++)
            {
                for (var gridX = 0; gridX < gridWidth; gridX++, row++)
                {
                    var offset = row * rowSize;
                    var objectness = values[offset + 4];
                    if (!float.IsFinite(objectness) || objectness <= 0) continue;
                    var classId = 0;
                    var classScore = 0f;
                    for (var index = 0; index < labels.Length; index++)
                    {
                        var candidate = values[offset + 5 + index];
                        if (candidate > classScore)
                        {
                            classScore = candidate;
                            classId = index;
                        }
                    }
                    var score = objectness * classScore;
                    if (!float.IsFinite(score) || score < scoreThreshold) continue;

                    var centerX = (values[offset] + gridX) * stride;
                    var centerY = (values[offset + 1] + gridY) * stride;
                    var boxWidth = MathF.Exp(values[offset + 2]) * stride;
                    var boxHeight = MathF.Exp(values[offset + 3]) * stride;
                    var detection = new AnalysisDetection(labels[classId], classId, score,
                        Math.Clamp((centerX - boxWidth / 2) / width, 0, 1),
                        Math.Clamp((centerY - boxHeight / 2) / height, 0, 1),
                        Math.Clamp((centerX + boxWidth / 2) / width, 0, 1),
                        Math.Clamp((centerY + boxHeight / 2) / height, 0, 1));
                    if (detection.IsValid) candidates.Add(detection);
                }
            }
        }

        var selected = new List<AnalysisDetection>(Math.Min(candidates.Count, maximumDetections));
        foreach (var candidate in candidates.OrderByDescending(item => item.Score))
        {
            if (selected.Any(item => item.ClassId == candidate.ClassId &&
                    IntersectionOverUnion(item, candidate) > nmsThreshold)) continue;
            selected.Add(candidate);
            if (selected.Count == maximumDetections) break;
        }
        return selected;
    }

    private static float IntersectionOverUnion(AnalysisDetection left, AnalysisDetection right)
    {
        var intersectionWidth = MathF.Max(0, MathF.Min(left.X2, right.X2) - MathF.Max(left.X1, right.X1));
        var intersectionHeight = MathF.Max(0, MathF.Min(left.Y2, right.Y2) - MathF.Max(left.Y1, right.Y1));
        var intersection = intersectionWidth * intersectionHeight;
        var union = (left.X2 - left.X1) * (left.Y2 - left.Y1) +
            (right.X2 - right.X1) * (right.Y2 - right.Y1) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    public void Dispose()
    {
        if (session.IsValueCreated) session.Value?.Dispose();
    }
}

internal sealed record FfmpegFrameReaderOptions(string FfmpegPath, int Width, int Height, int SampleFps);

/// <summary>Decodes sampled RGB frames from the clip playback file through FFmpeg.</summary>
public sealed class FfmpegAnalysisFrameReader : IAnalysisFrameReader
{
    private readonly FfmpegFrameReaderOptions options;

    public FfmpegAnalysisFrameReader(IConfiguration configuration, AnalysisDetectorOptions detector)
    {
        options = new FfmpegFrameReaderOptions(
            configuration["MONITORING_FFMPEG_PATH"]?.Trim() is { Length: > 0 } path ? path : "ffmpeg",
            detector.Width, detector.Height,
            configuration.GetValue<int?>("MONITORING_ANALYSIS_SAMPLE_FPS") ?? 1);
        if (options.SampleFps is < 1 or > 10)
            throw new InvalidOperationException("A taxa de amostragem de análise é inválida.");
    }

    public async IAsyncEnumerable<AnalysisFrame> ReadAsync(AnalysisClipDescriptor clip, int maximumFrames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-i", clip.PlaybackPath, "-vf",
                     $"fps={options.SampleFps},scale={options.Width}:{options.Height}", "-pix_fmt", "rgb24",
                     "-f", "rawvideo", "pipe:1"
                 })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("analysis_frame_reader_unavailable");
        var frameBytes = checked(options.Width * options.Height * 3);
        var buffer = new byte[frameBytes];
        var index = 0;
        try
        {
            while (index < maximumFrames)
            {
                var read = await process.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, frameBytes,
                    throwOnEndOfStream: false, cancellationToken);
                if (read < frameBytes) break;
                var tensor = new float[frameBytes];
                for (var pixel = 0; pixel < frameBytes; pixel += 3)
                {
                    var target = pixel / 3;
                    tensor[target] = buffer[pixel] / 255f;
                    tensor[options.Width * options.Height + target] = buffer[pixel + 1] / 255f;
                    tensor[2 * options.Width * options.Height + target] = buffer[pixel + 2] / 255f;
                }
                yield return new AnalysisFrame(index, clip.CameraId, clip.StreamEpoch,
                    clip.CapturedAtUtc.AddSeconds(index), options.Width, options.Height, tensor);
                index++;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}

/// <summary>Claims one analysis job at a time, executes the detector and persists a versioned result.</summary>
public sealed class AnalysisWorker(AnalysisDispatchStore store, IClipPlaybackSource clips, AnalysisResultStore results,
    IAnalysisFrameReader frames, IAnalysisDetector detector, AnalysisRuntimeOptions options,
    TimeProvider timeProvider, ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Execução de análise desativada por configuração.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var executed = await RunOnceAsync(stoppingToken);
                if (!executed) await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Falha ao executar a análise de clipe.");
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
            }
        }
    }

    /// <summary>Runs a single claim/execute cycle. Returns false when the queue is empty.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return false;
        var claimed = await store.ClaimNextWithLeaseAsync(cancellationToken);
        if (claimed is null) return false;
        var (job, leaseId) = claimed.Value;
        try
        {
            var clip = await clips.GetAsync(job.ClipPublicId, cancellationToken)
                ?? throw new InvalidOperationException("analysis_clip_not_found");
            var playbackPath = await clips.GetPlaybackPathAsync(job.ClipPublicId, cancellationToken)
                ?? throw new InvalidOperationException("analysis_clip_playback_unavailable");
            var descriptor = new AnalysisClipDescriptor(clip.PublicId, clip.CameraId, clip.ClipId, playbackPath,
                clip.StartedAtUtc);
            var objects = new List<AnalysisObject>();
            var framesAnalyzed = 0;
            await foreach (var frame in frames.ReadAsync(descriptor, options.MaximumFrames, cancellationToken))
            {
                framesAnalyzed++;
                foreach (var detection in await detector.DetectAsync(frame, cancellationToken))
                    objects.Add(new AnalysisObject(detection.ClassName, detection.ClassId, detection.Score,
                        detection.X1, detection.Y1, detection.X2, detection.Y2, frame.Index));
            }
            var result = new AnalysisClipResultV1("analysis-result.v1", clip.PublicId, clip.CameraId,
                job.AnalysisVersion, detector.ModelVersion, framesAnalyzed, objects, timeProvider.GetUtcNow());
            var (sha, _) = await results.WriteAsync(result, cancellationToken);
            await store.CompleteAsync(job.PublicId, leaseId, "completed", sha, null, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Análise {JobPublicId} falhou.", job.PublicId);
            var retryable = job.AttemptCount < options.MaximumAttempts;
            var code = exception switch
            {
                InvalidDataException => "analysis_invalid_media",
                InvalidOperationException exception2 when exception2.Message.StartsWith("analysis_", StringComparison.Ordinal)
                    => exception2.Message,
                _ => "analysis_execution_failed"
            };
            try
            {
                await store.FailAsync(job.PublicId, leaseId, code, retryable, cancellationToken);
            }
            catch (AnalysisConflictException)
            {
                logger.LogWarning("Lease perdido ao registrar falha da análise {JobPublicId}.", job.PublicId);
            }
            return true;
        }
    }
}
