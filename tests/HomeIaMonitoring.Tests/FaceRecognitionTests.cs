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
    public void Samples_evenly_spaced_midpoints_across_the_clip()
    {
        Assert.Equal(new[] { TimeSpan.FromSeconds(0.25) },
            FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(0.5), 2));
        Assert.Equal(new[] { TimeSpan.FromSeconds(5) },
            FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(10), 1));
        Assert.Equal(new[] { TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(7.5) },
            FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(10), 2));
        var six = FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(10), 6);
        Assert.Equal(6, six.Count);
        Assert.Equal(10 * 0.5 / 6, six[0].TotalSeconds, 3);
        Assert.Equal(10 * 5.5 / 6, six[5].TotalSeconds, 3);
        Assert.Equal(12, FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(10), 20).Count);
    }

    [Fact]
    public void Accepts_wide_frames_within_the_pixel_budget()
    {
        Assert.True(new RgbImage(2304, 1296, new byte[2304 * 1296 * 3]).IsValid);
        Assert.False(new RgbImage(2304, 1296, new byte[10]).IsValid);
        Assert.False(new RgbImage(2049, 2049, new byte[2049 * 2049 * 3]).IsValid);
    }

    [Fact]
    public void Warp_samples_the_source_image_instead_of_a_constant_black_tensor()
    {
        var pixels = new byte[112 * 112 * 3];
        for (var y = 0; y < 112; y++)
            for (var x = 0; x < 112; x++)
            {
                var offset = (y * 112 + x) * 3;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = (byte)((x + y) % 256);
            }
        var image = new RgbImage(112, 112, pixels);
        FacePoint[] template =
        [
            new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f),
            new(41.5493f, 92.3655f), new(70.7299f, 92.2041f)
        ];

        var tensor = FaceAlignment.Warp(image, FaceAlignment.Fit(template));

        Assert.True(tensor.Distinct().Count() > 1);
        Assert.Equal((20f - 127.5f) / 128f, tensor[10 * 112 + 20], 2);
        Assert.Equal((10f - 127.5f) / 128f, tensor[112 * 112 + 10 * 112 + 20], 2);
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
        float[] referenceX = [38.2946f, 73.5318f, 56.0252f, 41.5493f, 70.7299f];
        float[] referenceY = [51.6963f, 51.5014f, 71.7366f, 92.3655f, 92.2041f];
        var source = Enumerable.Range(0, 5)
            .Select(index => new FacePoint(referenceX[index] / 112f * 150 + 20, referenceY[index] / 112f * 150 + 30))
            .ToArray();
        var transform = FaceAlignment.Fit(source);
        for (var index = 0; index < 5; index++)
        {
            var x = transform.Scale * (transform.Cos * source[index].X - transform.Sin * source[index].Y) +
                    transform.OffsetX;
            var y = transform.Scale * (transform.Sin * source[index].X + transform.Cos * source[index].Y) +
                    transform.OffsetY;
            Assert.Equal(referenceX[index], x, 2);
            Assert.Equal(referenceY[index], y, 2);
        }
        Assert.Throws<ArgumentException>(() => FaceAlignment.Fit([new FacePoint(1, 1), new FacePoint(1, 1),
            new FacePoint(1, 1), new FacePoint(1, 1), new FacePoint(1, 1)]));
    }

    [Fact]
    public void Selects_the_largest_face_across_sampled_frames()
    {
        var small = BlankFrame();
        var large = BlankFrame();
        var detector = new StubDetector(image => image == small
            ? [Face(10, 10, 20, 20)]
            : [Face(30, 30, 60, 60)]);

        var selected = OnnxFaceObservationSource.SelectBestFace([small, large], detector, 16);

        Assert.NotNull(selected);
        Assert.Same(large, selected!.Value.Image);
        Assert.Equal(60, selected.Value.Face.Width);
    }

    [Fact]
    public void Ignores_faces_with_landmarks_outside_the_frame_and_faces_below_the_minimum()
    {
        var outside = BlankFrame();
        var small = BlankFrame();
        var inside = BlankFrame();
        var detector = new StubDetector(image => image == outside
            ? [FaceOutside(0, 0, 80, 80)]
            : image == small
                ? [Face(10, 10, 10, 10)]
                : [Face(10, 10, 40, 40)]);

        var selected = OnnxFaceObservationSource.SelectBestFace([outside, small, inside], detector, 16);

        Assert.NotNull(selected);
        Assert.Same(inside, selected!.Value.Image);
        Assert.Equal(40, selected.Value.Face.Width);
    }

    [Fact]
    public void Returns_no_face_when_none_is_usable()
    {
        var image = BlankFrame();
        var detector = new StubDetector(_ => [FaceOutside(0, 0, 80, 80)]);
        Assert.Null(OnnxFaceObservationSource.SelectBestFace([image], detector, 16));
    }

    private static RgbImage BlankFrame() => new(112, 112, new byte[112 * 112 * 3]);

    private static DetectedFace Face(float x, float y, float width, float height) =>
        new(x, y, width, height, [new FacePoint(0, 0), new FacePoint(0, 0), new FacePoint(0, 0),
            new FacePoint(0, 0), new FacePoint(0, 0)]);

    private static DetectedFace FaceOutside(float x, float y, float width, float height) =>
        new(x, y, width, height, [new FacePoint(200, 200), new FacePoint(200, 200), new FacePoint(200, 200),
            new FacePoint(200, 200), new FacePoint(200, 200)]);

    private sealed class StubDetector(Func<RgbImage, IReadOnlyList<DetectedFace>> detect) : IFaceDetector
    {
        public IReadOnlyList<DetectedFace> Detect(RgbImage image) => detect(image);
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

        public Task<ClipScanPage> ListReadyPageAsync(int take, int offset, CancellationToken cancellationToken) =>
            Task.FromResult(new ClipScanPage([], offset, false));

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
