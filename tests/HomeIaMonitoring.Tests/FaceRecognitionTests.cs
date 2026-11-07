using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class FaceRecognitionTests
{
    private static readonly FaceRecognitionOptions Options = new(
        "/face-models", "detector-hash", "recognizer-hash", 0.6f, 0.3f, 48, 640, 1, 20, 2, "ffmpeg");

    private static readonly FacePoint[] Landmarks =
    [
        new(80, 90), new(120, 90), new(100, 105), new(85, 120), new(115, 120)
    ];

    [Fact]
    public void Samples_frames_across_the_clip_and_respects_the_limit()
    {
        Assert.Equal(new[] { TimeSpan.Zero }, FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(0.5), 2));
        Assert.Equal(new[] { TimeSpan.FromSeconds(5) },
            FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(10), 1));
        Assert.Equal(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7) },
            FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(10), 2));
    }

    [Fact]
    public void Parses_a_ppm_header_and_rejects_other_formats()
    {
        Assert.True(FfmpegFaceFrameExtractor.TryParseHeader("P6\n2 3\n255\n", out var width, out var height));
        Assert.Equal(2, width);
        Assert.Equal(3, height);
        Assert.False(FfmpegFaceFrameExtractor.TryParseHeader("P5\n2 3\n255\n", out _, out _));
        Assert.False(FfmpegFaceFrameExtractor.TryParseHeader("P6\n2 3\n254\n", out _, out _));
    }

    [Fact]
    public void Decodes_and_suppresses_overlapping_yunet_candidates()
    {
        var cls = new float[64];
        var obj = new float[64];
        var bbox = new float[64 * 4];
        var kps = new float[64 * 10];
        cls[0] = 0.81f;
        obj[0] = 0.81f;
        bbox[0] = 0.5f;
        bbox[1] = 0.5f;
        bbox[2] = MathF.Log(2);
        bbox[3] = MathF.Log(2);
        for (var point = 0; point < 5; point++)
        {
            kps[point * 2] = 0.2f + point * 0.1f;
            kps[point * 2 + 1] = 0.3f + point * 0.1f;
        }

        var decoded = YuNetPostProcess.Decode(cls, obj, bbox, kps, 8, 8, 8, 0.6f);
        var face = Assert.Single(decoded);
        Assert.Equal(16, face.Width, 3);
        Assert.Equal(5, face.Landmarks.Count);

        var duplicate = face with { X = face.X + 1 };
        Assert.Single(YuNetPostProcess.Suppress([face, duplicate], 0.3f));
        Assert.False(YuNetPostProcess.Decode(cls, obj, bbox, kps, 8, 8, 8, 0.99f).Any());
    }

    [Fact]
    public void Fits_landmarks_onto_the_reference_template()
    {
        float[] referenceX = [38.2946f / 112, 73.5318f / 112, 56.0252f / 112, 41.5493f / 112, 70.7299f / 112];
        float[] referenceY = [51.6963f / 112, 51.5014f / 112, 71.7366f / 112, 92.3655f / 112, 92.2041f / 112];
        var source = Enumerable.Range(0, 5)
            .Select(index => new FacePoint(referenceX[index] * 150 + 20, referenceY[index] * 150 + 30))
            .ToArray();
        var transform = FaceAlignment.Fit(source);
        for (var index = 0; index < 5; index++)
        {
            var x = transform.Scale * (transform.Cos * source[index].X - transform.Sin * source[index].Y) +
                    transform.OffsetX;
            var y = transform.Scale * (transform.Sin * source[index].X + transform.Cos * source[index].Y) +
                    transform.OffsetY;
            Assert.Equal(referenceX[index], x, 3);
            Assert.Equal(referenceY[index], y, 3);
        }
        Assert.Throws<ArgumentException>(() => FaceAlignment.Fit([new FacePoint(1, 1), new FacePoint(1, 1),
            new FacePoint(1, 1), new FacePoint(1, 1), new FacePoint(1, 1)]));
    }

    [Fact]
    public void Verifies_artifact_state_without_exposing_paths()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var detector = Path.Combine(directory.FullName, FaceRecognitionOptions.DetectorFileName);
            var recognizer = Path.Combine(directory.FullName, FaceRecognitionOptions.RecognizerFileName);
            File.WriteAllBytes(detector, [1, 2, 3]);
            File.WriteAllBytes(recognizer, [4, 5, 6]);
            var configured = Options with
            {
                ModelsDirectory = directory.FullName,
                DetectorSha256 = Hash(detector),
                RecognizerSha256 = Hash(recognizer)
            };
            var ready = FaceArtifactVerifier.Verify(configured);
            Assert.True(ready.Ready);
            Assert.Equal(FaceArtifactVerifier.ReadyState, ready.State);
            Assert.DoesNotContain(directory.FullName, ready.ToString());

            Assert.Equal(FaceArtifactVerifier.NotConfiguredState,
                FaceArtifactVerifier.Verify(configured with { DetectorSha256 = null, RecognizerSha256 = null }).State);
            Assert.Equal(FaceArtifactVerifier.ChecksumMismatchState,
                FaceArtifactVerifier.Verify(configured with { DetectorSha256 = new string('0', 64) }).State);
            Assert.Equal(FaceArtifactVerifier.ArtifactUnavailableState,
                FaceArtifactVerifier.Verify(configured with { ModelsDirectory = "/missing-face-models" }).State);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Creates_an_observation_from_the_largest_face_only()
    {
        var playback = new FakePlayback("/tmp/clip.ts");
        var frames = new FakeFrames([Image()]);
        var small = new DetectedFace(10, 10, 20, 20, Landmarks);
        var large = new DetectedFace(40, 40, 80, 80, Landmarks);
        var detector = new FakeDetector([small, large]);
        var embedder = new FakeEmbedder();
        using var source = Source(frames, detector, embedder, playback, ReadyStatus());

        var observation = await source.TryCreateAsync(
            new ReadyClip("CLP000000000001", "CAM000000000001", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(9)),
            CancellationToken.None);

        Assert.NotNull(observation);
        Assert.Equal("face-observation.v1", observation.SchemaVersion);
        Assert.Equal(FaceRecognitionOptions.ModelId, observation.ModelId);
        Assert.Equal(FaceRecognitionOptions.ModelVersion, observation.ModelVersion);
        Assert.Equal("face-embedding.v1", observation.EmbeddingSchemaVersion);
        Assert.Equal(128, observation.Dimensions);
        Assert.Equal(128, observation.Values.Count);
        Assert.Equal("CAM000000000001", observation.CameraPublicId);
        Assert.Equal("CLP000000000001", observation.ClipPublicId);
        Assert.Equal(1, embedder.Calls);
        Assert.Equal(3 * FaceAlignment.OutputSize * FaceAlignment.OutputSize, embedder.LastLength);
        Assert.Equal(TimeSpan.FromSeconds(9), frames.LastDuration);
    }

    [Fact]
    public async Task Skips_without_a_large_enough_face()
    {
        var embedder = new FakeEmbedder();
        using var noFace = Source(new FakeFrames([Image()]), new FakeDetector([]), embedder,
            new FakePlayback("/tmp/clip.ts"), ReadyStatus());
        Assert.Null(await noFace.TryCreateAsync(Clip(), CancellationToken.None));

        var tiny = new DetectedFace(0, 0, 10, 10, Landmarks);
        using var tooSmall = Source(new FakeFrames([Image()]), new FakeDetector([tiny]), embedder,
            new FakePlayback("/tmp/clip.ts"), ReadyStatus());
        Assert.Null(await tooSmall.TryCreateAsync(Clip(), CancellationToken.None));
        Assert.Equal(0, embedder.Calls);
    }

    [Fact]
    public async Task Fails_closed_when_artifacts_are_unavailable_and_never_loads_the_model()
    {
        var loaded = false;
        var frames = new FakeFrames([Image()]);
        using var source = new OnnxFaceObservationSource(Options,
            new FaceArtifactStatus(false, FaceArtifactVerifier.ChecksumMismatchState, null, null),
            new FakePlayback("/tmp/clip.ts"), frames,
            _ => { loaded = true; return new FakeDetector([]); },
            _ => new FakeEmbedder(),
            NullLogger<OnnxFaceObservationSource>.Instance);

        Assert.Null(await source.TryCreateAsync(Clip(), CancellationToken.None));
        Assert.False(loaded);
        Assert.Equal(0, frames.Calls);
    }

    private static OnnxFaceObservationSource Source(IFaceFrameExtractor frames, IFaceDetector detector,
        IFaceEmbedder embedder, IClipPlaybackSource playback, FaceArtifactStatus status) =>
        new(Options, status, playback, frames, _ => detector, _ => embedder,
            NullLogger<OnnxFaceObservationSource>.Instance);

    private static ReadyClip Clip() =>
        new("CLP000000000001", "CAM000000000001", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(4));

    private static FaceArtifactStatus ReadyStatus() =>
        new(true, FaceArtifactVerifier.ReadyState, new string('a', 64), new string('b', 64));

    private static RgbImage Image()
    {
        var pixels = new byte[200 * 200 * 3];
        Array.Fill(pixels, (byte)128);
        return new RgbImage(200, 200, pixels);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class FakePlayback(string path) : IClipPlaybackSource
    {
        public Task<ClipLibraryItem?> GetAsync(string publicId, CancellationToken cancellationToken) =>
            Task.FromResult<ClipLibraryItem?>(null);

        public Task<IReadOnlyList<ClipLibraryItem>> ListReadyAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClipLibraryItem>>([]);

        public Task<string?> GetPlaybackPathAsync(string publicId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(path);
    }

    private sealed class FakeFrames(IReadOnlyList<RgbImage> frames) : IFaceFrameExtractor
    {
        public int Calls { get; private set; }
        public TimeSpan LastDuration { get; private set; }

        public Task<IReadOnlyList<RgbImage>> ExtractAsync(string mediaPath, TimeSpan duration,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastDuration = duration;
            return Task.FromResult(frames);
        }
    }

    private sealed class FakeDetector(IReadOnlyList<DetectedFace> faces) : IFaceDetector
    {
        public IReadOnlyList<DetectedFace> Detect(RgbImage image) => faces;
    }

    private sealed class FakeEmbedder : IFaceEmbedder
    {
        public int Calls { get; private set; }
        public int LastLength { get; private set; }

        public IReadOnlyList<float> Embed(float[] tensor)
        {
            Calls++;
            LastLength = tensor.Length;
            return Enumerable.Repeat(0.5f, 128).ToArray();
        }
    }
}
