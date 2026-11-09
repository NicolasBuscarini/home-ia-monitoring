using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HomeIaMonitoring;

/// <summary>Face candidate in original image coordinates: box plus the five YuNet landmarks.</summary>
public sealed record DetectedFace(float X, float Y, float Width, float Height, IReadOnlyList<FacePoint> Landmarks)
{
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Width) &&
                           float.IsFinite(Height) && Width > 0 && Height > 0 && Landmarks.Count == 5;
}

public sealed record FacePoint(float X, float Y);

/// <summary>Decoded RGB image kept only in memory for one observation attempt.</summary>
public sealed record RgbImage(int Width, int Height, byte[] Pixels)
{
    public bool IsValid => Width > 0 && Height > 0 && (long)Width * Height <= 2048L * 2048L &&
                           (long)Width * Height * 3 == Pixels.Length;
}

public interface IFaceDetector
{
    IReadOnlyList<DetectedFace> Detect(RgbImage image);
}

public interface IFaceEmbedder
{
    IReadOnlyList<float> Embed(float[] tensor);
}

public interface IFaceFrameExtractor
{
    Task<IReadOnlyList<RgbImage>> ExtractAsync(string mediaPath, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>
/// Artifact, threshold and sampling configuration for the real observation source. Weights are supplied by the
/// operator, mounted read-only and verified by SHA-256; the service never downloads or persists them.
/// </summary>
public sealed record FaceRecognitionOptions(
    string ModelsDirectory,
    string? DetectorSha256,
    string? RecognizerSha256,
    float ScoreThreshold,
    float NmsThreshold,
    int MinimumFacePixels,
    int MaximumInputSize,
    int IntraOpThreads,
    int TimeoutSeconds,
    int MaximumFrames,
    string FfmpegPath)
{
    public const string ModelId = "opencv-zoo-sface";
    public const string ModelVersion = "2021dec";
    public const string DetectorFileName = "yunet-2023mar.onnx";
    public const string RecognizerFileName = "sface-2021dec.onnx";
    public const int EmbeddingDimensions = 128;

    public string DetectorPath => Path.Combine(ModelsDirectory, DetectorFileName);
    public string RecognizerPath => Path.Combine(ModelsDirectory, RecognizerFileName);

    public static FaceRecognitionOptions FromConfiguration(IConfiguration configuration)
    {
        var directory = (configuration["MONITORING_FACE_MODELS_DIR"] ?? "/face-models").Trim();
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("MONITORING_FACE_MODELS_DIR deve ser um caminho absoluto.");
        return new FaceRecognitionOptions(
            directory,
            NormalizeHash(configuration["MONITORING_FACE_DETECTOR_SHA256"]),
            NormalizeHash(configuration["MONITORING_FACE_RECOGNIZER_SHA256"]),
            ReadFloat(configuration, "MONITORING_FACE_SCORE_THRESHOLD", 0.6f, 0.1f, 0.95f),
            ReadFloat(configuration, "MONITORING_FACE_NMS_THRESHOLD", 0.3f, 0.1f, 0.9f),
            ReadInt(configuration, "MONITORING_FACE_MINIMUM_PIXELS", 48, 16, 512),
            ReadInt(configuration, "MONITORING_FACE_MAXIMUM_INPUT_SIZE", 640, 64, 1280),
            ReadInt(configuration, "MONITORING_FACE_INTRA_OP_THREADS", 1, 1, 8),
            ReadInt(configuration, "MONITORING_FACE_TIMEOUT_SECONDS", 20, 5, 120),
            ReadInt(configuration, "MONITORING_FACE_MAXIMUM_FRAMES", 6, 1, 12),
            (configuration["MONITORING_CAPTURE_FFMPEG_PATH"] ?? "ffmpeg").Trim());
    }

    private static string? NormalizeHash(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
        return normalized is { Length: 64 } && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    private static float ReadFloat(IConfiguration configuration, string key, float fallback, float minimum,
        float maximum)
    {
        if (configuration[key] is not { } raw) return fallback;
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{key} deve estar entre {minimum} e {maximum}.");
        return value;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback, int minimum, int maximum)
    {
        if (configuration[key] is not { } raw) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{key} deve estar entre {minimum} e {maximum}.");
        return value;
    }
}

/// <summary>Sanitized artifact state: no paths and no model bytes leave this record.</summary>
public sealed record FaceArtifactStatus(bool Ready, string State, string? DetectorSha256, string? RecognizerSha256);

public static class FaceArtifactVerifier
{
    public const string ReadyState = "ready";
    public const string NotConfiguredState = "not_configured";
    public const string ArtifactUnavailableState = "artifact_unavailable";
    public const string ChecksumMismatchState = "artifact_checksum_mismatch";

    public static FaceArtifactStatus Verify(FaceRecognitionOptions options)
    {
        if (options.DetectorSha256 is null || options.RecognizerSha256 is null)
            return new FaceArtifactStatus(false, NotConfiguredState, null, null);
        if (!File.Exists(options.DetectorPath) || !File.Exists(options.RecognizerPath))
            return new FaceArtifactStatus(false, ArtifactUnavailableState, null, null);
        string detector;
        string recognizer;
        try
        {
            detector = Hash(options.DetectorPath);
            recognizer = Hash(options.RecognizerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FaceArtifactStatus(false, ArtifactUnavailableState, null, null);
        }
        if (!string.Equals(detector, options.DetectorSha256, StringComparison.Ordinal) ||
            !string.Equals(recognizer, options.RecognizerSha256, StringComparison.Ordinal))
            return new FaceArtifactStatus(false, ChecksumMismatchState, detector, recognizer);
        return new FaceArtifactStatus(true, ReadyState, detector, recognizer);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>
/// Extracts up to <see cref="FaceRecognitionOptions.MaximumFrames"/> frames from a ready clip with FFmpeg and
/// decodes them as raw RGB in memory. Samples are spread across the clip; the file is never copied or retained.
/// </summary>
public sealed class FfmpegFaceFrameExtractor : IFaceFrameExtractor
{
    private const int MaximumPixels = 2048 * 2048;
    private readonly FaceRecognitionOptions options;

    public FfmpegFaceFrameExtractor(FaceRecognitionOptions options) => this.options = options;

    public async Task<IReadOnlyList<RgbImage>> ExtractAsync(string mediaPath, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(mediaPath)) return [];
        var positions = SamplePositions(duration, options.MaximumFrames);
        var frames = new List<RgbImage>(positions.Count);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            foreach (var position in positions)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var frame = await ReadFrameAsync(mediaPath, position, timeout.Token);
                if (frame is not null) frames.Add(frame);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-clip budget ran out; keep the frames decoded so far.
        }
        return frames;
    }

    internal static IReadOnlyList<TimeSpan> SamplePositions(TimeSpan duration, int maximumFrames)
    {
        var seconds = duration.TotalSeconds;
        if (seconds <= 0) return [TimeSpan.Zero];
        var count = Math.Clamp(maximumFrames, 1, 12);
        if (seconds < 1) return [TimeSpan.FromSeconds(seconds / 2)];
        // Midpoints of equal slices avoid the very start/end (often keyframes or black frames).
        var positions = new TimeSpan[count];
        for (var index = 0; index < count; index++)
            positions[index] = TimeSpan.FromSeconds(seconds * (index + 0.5) / count);
        return positions;
    }

    internal async Task<RgbImage?> ReadFrameAsync(string mediaPath, TimeSpan position,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error",
                     "-ss", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", mediaPath, "-frames:v", "1", "-f", "image2pipe", "-c:v", "ppm", "-"
                 })
            start.ArgumentList.Add(argument);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return null;
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var image = await ReadPpmAsync(process.StandardOutput.BaseStream, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
            if (process.ExitCode != 0) return null;
            return image is { IsValid: true } ? image : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
                                              or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
        }
    }

    private static async Task<RgbImage?> ReadPpmAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(64);
        while (header.Count < 64)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return null;
            header.Add(buffer[0]);
            if (!TryParseHeader(Encoding.ASCII.GetString(header.ToArray()), out var width, out var height))
                continue;
            if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels) return null;
            var pixels = new byte[width * height * 3];
            var offset = 0;
            while (offset < pixels.Length)
            {
                var readBytes = await stream.ReadAsync(pixels.AsMemory(offset), cancellationToken);
                if (readBytes == 0) return null;
                offset += readBytes;
            }
            return new RgbImage(width, height, pixels);
        }
        return null;
    }

    internal static bool TryParseHeader(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!text.StartsWith("P6", StringComparison.Ordinal)) return false;
        var index = 2;
        if (!TryReadNumber(text, ref index, out width) || !TryReadNumber(text, ref index, out height)) return false;
        if (!TryReadNumber(text, ref index, out var maximum) || maximum != 255) return false;
        return index < text.Length && text[index] is '\n' or ' ' or '\r' or '\t' && width > 0 && height > 0;
    }

    private static bool TryReadNumber(string text, ref int index, out int value)
    {
        value = 0;
        while (index < text.Length && text[index] is ' ' or '\n' or '\r' or '\t') index++;
        var digits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            value = value * 10 + (text[index] - '0');
            index++;
            digits++;
            if (digits > 5) return false;
        }
        return digits > 0;
    }
}

/// <summary>YuNet (opencv_zoo 2023mar) inference over an in-memory RGB image.</summary>
public sealed class YuNetFaceDetector : IFaceDetector, IDisposable
{
    private static readonly int[] Strides = [8, 16, 32];
    private readonly FaceRecognitionOptions options;
    private readonly InferenceSession session;
    private readonly int fixedWidth;
    private readonly int fixedHeight;

    public YuNetFaceDetector(FaceRecognitionOptions options)
    {
        this.options = options;
        using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = options.IntraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        session = new InferenceSession(options.DetectorPath, sessionOptions);
        var dimensions = session.InputMetadata.TryGetValue("input", out var metadata) ? metadata.Dimensions : null;
        fixedHeight = dimensions is { Length: 4 } && dimensions[2] > 0 ? dimensions[2] : 0;
        fixedWidth = dimensions is { Length: 4 } && dimensions[3] > 0 ? dimensions[3] : 0;
        foreach (var name in OutputNames())
            if (!session.OutputMetadata.ContainsKey(name))
                throw new InvalidOperationException("face_detector_outputs_incompatible");
    }

    public IReadOnlyList<DetectedFace> Detect(RgbImage image)
    {
        int width, height;
        if (fixedWidth > 0 && fixedHeight > 0)
        {
            width = fixedWidth;
            height = fixedHeight;
        }
        else
        {
            var scale = Math.Min(1f, (float)options.MaximumInputSize / Math.Max(image.Width, image.Height));
            width = Math.Max(32, (int)(image.Width * scale) / 32 * 32);
            height = Math.Max(32, (int)(image.Height * scale) / 32 * 32);
        }
        var scaleX = (float)width / image.Width;
        var scaleY = (float)height / image.Height;
        var input = Resize(image, width, height);
        using var results = session.Run(
            [NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, 3, height, width]))],
            OutputNames());
        var candidates = new List<DetectedFace>(32);
        foreach (var stride in Strides)
        {
            var cols = width / stride;
            var rows = height / stride;
            candidates.AddRange(YuNetPostProcess.Decode(
                results.First(result => result.Name == $"cls_{stride}").AsTensor<float>().ToArray(),
                results.First(result => result.Name == $"obj_{stride}").AsTensor<float>().ToArray(),
                results.First(result => result.Name == $"bbox_{stride}").AsTensor<float>().ToArray(),
                results.First(result => result.Name == $"kps_{stride}").AsTensor<float>().ToArray(),
                cols, rows, stride, options.ScoreThreshold));
        }
        return YuNetPostProcess.Suppress(candidates, options.NmsThreshold)
            .Select(face => face with
            {
                X = face.X / scaleX,
                Y = face.Y / scaleY,
                Width = face.Width / scaleX,
                Height = face.Height / scaleY,
                Landmarks = face.Landmarks.Select(point => new FacePoint(point.X / scaleX, point.Y / scaleY)).ToArray()
            })
            .ToArray();
    }

    public void Dispose() => session.Dispose();

    internal static string[] OutputNames() =>
        Strides.SelectMany(stride => new[] { $"cls_{stride}", $"obj_{stride}", $"bbox_{stride}", $"kps_{stride}" })
            .ToArray();

    private static float[] Resize(RgbImage image, int width, int height)
    {
        var plane = width * height;
        var tensor = new float[3 * plane];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(image.Height - 1, (int)((y + 0.5f) * image.Height / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(image.Width - 1, (int)((x + 0.5f) * image.Width / width));
                var source = (sourceY * image.Width + sourceX) * 3;
                var target = y * width + x;
                tensor[target] = image.Pixels[source];
                tensor[plane + target] = image.Pixels[source + 1];
                tensor[2 * plane + target] = image.Pixels[source + 2];
            }
        }
        return tensor;
    }
}

/// <summary>Pure YuNet decoding/NMS helpers, kept separate so they can be unit tested.</summary>
public static class YuNetPostProcess
{
    public static IReadOnlyList<DetectedFace> Decode(float[] cls, float[] obj, float[] bbox, float[] kps, int cols,
        int rows, int stride, float scoreThreshold)
    {
        var cells = cols * rows;
        if (cells <= 0 || cls.Length < cells || obj.Length < cells || bbox.Length < cells * 4 ||
            kps.Length < cells * 10) return [];
        var faces = new List<DetectedFace>(cells);
        for (var index = 0; index < cells; index++)
        {
            var classScore = cls[index];
            var objectScore = obj[index];
            if (!float.IsFinite(classScore) || !float.IsFinite(objectScore) ||
                classScore is < 0 or > 1 || objectScore is < 0 or > 1) continue;
            var score = MathF.Sqrt(classScore * objectScore);
            if (score < scoreThreshold) continue;
            var row = index / cols;
            var column = index % cols;
            var centerX = (bbox[index * 4] + column) * stride;
            var centerY = (bbox[index * 4 + 1] + row) * stride;
            var width = MathF.Exp(bbox[index * 4 + 2]) * stride;
            var height = MathF.Exp(bbox[index * 4 + 3]) * stride;
            if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) continue;
            var landmarks = new FacePoint[5];
            for (var point = 0; point < 5; point++)
                landmarks[point] = new FacePoint((kps[index * 10 + point * 2] + column) * stride,
                    (kps[index * 10 + point * 2 + 1] + row) * stride);
            var face = new DetectedFace(centerX - width / 2, centerY - height / 2, width, height, landmarks);
            if (face.IsValid) faces.Add(face);
        }
        return faces;
    }

    public static IReadOnlyList<DetectedFace> Suppress(IReadOnlyList<DetectedFace> faces, float threshold)
    {
        var ordered = faces.OrderByDescending(face => face.Width * face.Height).ToArray();
        var kept = new List<DetectedFace>(ordered.Length);
        foreach (var candidate in ordered)
        {
            if (kept.Any(face => IntersectionOverUnion(face, candidate) > threshold)) continue;
            kept.Add(candidate);
        }
        return kept;
    }

    public static float IntersectionOverUnion(DetectedFace left, DetectedFace right)
    {
        var x1 = Math.Max(left.X, right.X);
        var y1 = Math.Max(left.Y, right.Y);
        var x2 = Math.Min(left.X + left.Width, right.X + right.Width);
        var y2 = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var width = Math.Max(0, x2 - x1);
        var height = Math.Max(0, y2 - y1);
        var intersection = width * height;
        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

/// <summary>Umeyama/Park similarity fit to the 112×112 ArcFace template used by SFace, plus bilinear warping.</summary>
public static class FaceAlignment
{
    public const int OutputSize = 112;

    // Template landmarks in output pixels (0..111). Warp() iterates pixel coordinates, so the reference
    // must stay in pixel space; normalizing it would invert to coordinates ~112x outside the source image.
    private static readonly FacePoint[] Reference =
    [
        new(38.2946f, 51.6963f),
        new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f),
        new(70.7299f, 92.2041f)
    ];

    public static SimilarityTransform Fit(IReadOnlyList<FacePoint> landmarks)
    {
        if (landmarks.Count != 5) throw new ArgumentException("Cinco pontos faciais são obrigatórios.", nameof(landmarks));
        float sourceMeanX = 0, sourceMeanY = 0, targetMeanX = 0, targetMeanY = 0;
        for (var index = 0; index < 5; index++)
        {
            sourceMeanX += landmarks[index].X;
            sourceMeanY += landmarks[index].Y;
            targetMeanX += Reference[index].X;
            targetMeanY += Reference[index].Y;
        }
        sourceMeanX /= 5;
        sourceMeanY /= 5;
        targetMeanX /= 5;
        targetMeanY /= 5;
        float dot = 0, cross = 0, variance = 0;
        for (var index = 0; index < 5; index++)
        {
            var sourceX = landmarks[index].X - sourceMeanX;
            var sourceY = landmarks[index].Y - sourceMeanY;
            var targetX = Reference[index].X - targetMeanX;
            var targetY = Reference[index].Y - targetMeanY;
            dot += sourceX * targetX + sourceY * targetY;
            cross += sourceX * targetY - sourceY * targetX;
            variance += sourceX * sourceX + sourceY * sourceY;
        }
        if (variance < 1e-6f) throw new ArgumentException("Pontos faciais degenerados.", nameof(landmarks));
        var angle = MathF.Atan2(cross, dot);
        var scale = MathF.Sqrt(dot * dot + cross * cross) / variance;
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentException("Pontos faciais degenerados.", nameof(landmarks));
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var offsetX = targetMeanX - scale * (cos * sourceMeanX - sin * sourceMeanY);
        var offsetY = targetMeanY - scale * (sin * sourceMeanX + cos * sourceMeanY);
        return new SimilarityTransform(scale, cos, sin, offsetX, offsetY);
    }

    public static float[] Warp(RgbImage image, SimilarityTransform transform)
    {
        var size = OutputSize;
        var plane = size * size;
        var tensor = new float[3 * plane];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var sourceX = (transform.Cos * (x - transform.OffsetX) + transform.Sin * (y - transform.OffsetY)) /
                              transform.Scale;
                var sourceY = (-transform.Sin * (x - transform.OffsetX) + transform.Cos * (y - transform.OffsetY)) /
                              transform.Scale;
                Sample(image, sourceX, sourceY, out var red, out var green, out var blue);
                var target = y * size + x;
                tensor[target] = (red - 127.5f) / 128f;
                tensor[plane + target] = (green - 127.5f) / 128f;
                tensor[2 * plane + target] = (blue - 127.5f) / 128f;
            }
        }
        return tensor;
    }

    private static void Sample(RgbImage image, float x, float y, out float red, out float green, out float blue)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
        {
            red = 0;
            green = 0;
            blue = 0;
            return;
        }
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(image.Width - 1, x0 + 1);
        var y1 = Math.Min(image.Height - 1, y0 + 1);
        var fx = x - x0;
        var fy = y - y0;
        red = Blend(image, x0, y0, x1, y1, fx, fy, 0);
        green = Blend(image, x0, y0, x1, y1, fx, fy, 1);
        blue = Blend(image, x0, y0, x1, y1, fx, fy, 2);
    }

    private static float Blend(RgbImage image, int x0, int y0, int x1, int y1, float fx, float fy, int channel)
    {
        var top = image.Pixels[(y0 * image.Width + x0) * 3 + channel] * (1 - fx) +
                  image.Pixels[(y0 * image.Width + x1) * 3 + channel] * fx;
        var bottom = image.Pixels[(y1 * image.Width + x0) * 3 + channel] * (1 - fx) +
                     image.Pixels[(y1 * image.Width + x1) * 3 + channel] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}

public sealed record SimilarityTransform(float Scale, float Cos, float Sin, float OffsetX, float OffsetY);

/// <summary>SFace (opencv_zoo 2021dec) embedding over the aligned 112×112 tensor.</summary>
public sealed class SFaceEmbedder : IFaceEmbedder, IDisposable
{
    private readonly InferenceSession session;

    public SFaceEmbedder(FaceRecognitionOptions options)
    {
        using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = options.IntraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        session = new InferenceSession(options.RecognizerPath, sessionOptions);
        if (!session.InputMetadata.ContainsKey("data") || !session.OutputMetadata.ContainsKey("fc1"))
        {
            session.Dispose();
            throw new InvalidOperationException("face_recognizer_outputs_incompatible");
        }
    }

    public IReadOnlyList<float> Embed(float[] tensor)
    {
        using var results = session.Run(
            [NamedOnnxValue.CreateFromTensor("data",
                new DenseTensor<float>(tensor, [1, 3, FaceAlignment.OutputSize, FaceAlignment.OutputSize]))],
            ["fc1"]);
        return results.First().AsTensor<float>().ToArray();
    }

    public void Dispose() => session.Dispose();
}

/// <summary>
/// Real observation source: samples frames from a ready clip, detects the largest face with YuNet, aligns it and
/// embeds it with SFace. Fails closed to <c>null</c> when artifacts are unavailable, no face is found or the face
/// is too small; media and embeddings stay in memory.
/// </summary>
public sealed class OnnxFaceObservationSource : IFaceObservationSource, IDisposable
{
    public const string SourceName = "onnx-yunet-sface";
    public const string ObservationSchemaVersion = "face-observation.v1";
    public const string EmbeddingSchemaVersion = "face-embedding.v1";

    private readonly FaceRecognitionOptions options;
    private readonly FaceArtifactStatus status;
    private readonly IClipPlaybackSource playback;
    private readonly IFaceFrameExtractor frames;
    private readonly Func<FaceRecognitionOptions, IFaceDetector> detectorFactory;
    private readonly Func<FaceRecognitionOptions, IFaceEmbedder> embedderFactory;
    private readonly ILogger<OnnxFaceObservationSource> logger;
    private readonly Lazy<IFaceDetector> detector;
    private readonly Lazy<IFaceEmbedder> embedder;

    public OnnxFaceObservationSource(FaceRecognitionOptions options, FaceArtifactStatus status,
        IClipPlaybackSource playback, IFaceFrameExtractor frames,
        Func<FaceRecognitionOptions, IFaceDetector> detectorFactory,
        Func<FaceRecognitionOptions, IFaceEmbedder> embedderFactory,
        ILogger<OnnxFaceObservationSource> logger)
    {
        this.options = options;
        this.status = status;
        this.playback = playback;
        this.frames = frames;
        this.detectorFactory = detectorFactory;
        this.embedderFactory = embedderFactory;
        this.logger = logger;
        detector = new Lazy<IFaceDetector>(() => detectorFactory(options), LazyThreadSafetyMode.ExecutionAndPublication);
        embedder = new Lazy<IFaceEmbedder>(() => embedderFactory(options), LazyThreadSafetyMode.ExecutionAndPublication);
        if (status.Ready)
            logger.LogInformation("Observação facial ativa: model={ModelId}/{ModelVersion} state={State}",
                FaceRecognitionOptions.ModelId, FaceRecognitionOptions.ModelVersion, status.State);
        else
            logger.LogWarning("Observação facial indisponível; producer seguirá sem observações. state={State}",
                status.State);
    }

    public string Name => SourceName;

    public async Task<FaceObservation?> TryCreateAsync(ReadyClip clip, CancellationToken cancellationToken)
    {
        if (!status.Ready) return null;
        var path = await playback.GetPlaybackPathAsync(clip.PublicId, cancellationToken);
        if (path is null) return null;
        var sampled = await frames.ExtractAsync(path, clip.Duration ?? TimeSpan.Zero, cancellationToken);
        var selected = SelectBestFace(sampled, detector.Value, options.MinimumFacePixels);
        if (selected is null) return null;
        var (image, face) = selected.Value;
        SimilarityTransform transform;
        try
        {
            transform = FaceAlignment.Fit(face.Landmarks);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var values = embedder.Value.Embed(FaceAlignment.Warp(image, transform));
        if (values.Count != FaceRecognitionOptions.EmbeddingDimensions ||
            values.Any(value => !float.IsFinite(value)))
        {
            logger.LogWarning("Embedding facial inválido descartado; tipo={Type}", "invalid_embedding");
            return null;
        }
        logger.LogDebug("Observação facial: {Frames} frames, face escolhida {Width}x{Height}",
            sampled.Count, face.Width, face.Height);
        return new FaceObservation(ObservationSchemaVersion, FaceRecognitionOptions.ModelId,
            FaceRecognitionOptions.ModelVersion, EmbeddingSchemaVersion, values.Count, values,
            clip.CameraId, clip.PublicId, clip.EndedAtUtc);
    }

    /// <summary>
    /// Picks the largest usable face across all sampled frames, ignoring faces that touch the frame border
    /// (often partially visible close-ups) or fall below the configured minimum size.
    /// </summary>
    internal static (RgbImage Image, DetectedFace Face)? SelectBestFace(IReadOnlyList<RgbImage> frames,
        IFaceDetector detector, int minimumPixels)
    {
        (RgbImage Image, DetectedFace Face)? best = null;
        var bestArea = 0f;
        foreach (var image in frames)
        {
            foreach (var face in detector.Detect(image))
            {
                if (!face.IsValid || Math.Min(face.Width, face.Height) < minimumPixels) continue;
                if (!LandmarksInside(face, image)) continue;
                var area = face.Width * face.Height;
                if (best is not null && area <= bestArea) continue;
                best = (image, face);
                bestArea = area;
            }
        }
        return best;
    }

    private static bool LandmarksInside(DetectedFace face, RgbImage image) =>
        face.Landmarks.All(point => point.X >= 0 && point.Y >= 0 &&
                                    point.X <= image.Width - 1 && point.Y <= image.Height - 1);

    public void Dispose()
    {
        if (detector.IsValueCreated && detector.Value is IDisposable detectorDisposable) detectorDisposable.Dispose();
        if (embedder.IsValueCreated && embedder.Value is IDisposable embedderDisposable) embedderDisposable.Dispose();
    }
}
