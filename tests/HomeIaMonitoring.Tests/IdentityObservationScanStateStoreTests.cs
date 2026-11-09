using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class IdentityObservationScanStateStoreTests
{
    [Fact]
    public void SavesAndLoadsStateAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "home-ia-observation-store-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scan-state.json");
        try
        {
            var store = new IdentityObservationScanStateStore(path,
                NullLogger<IdentityObservationScanStateStore>.Instance);
            Assert.Null(store.Load());

            var seenAt = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
            store.Save(new IdentityObservationScanState(7, 3,
                new Dictionary<string, DateTimeOffset> { ["CLP000000000001"] = seenAt },
                new Dictionary<string, int> { ["CLP000000000002"] = 2 }));

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal(7, loaded!.ScanOffset);
            Assert.Equal(3, loaded.Sweeps);
            Assert.Equal(seenAt, loaded.Seen["CLP000000000001"]);
            Assert.Equal(2, loaded.FailedAttempts["CLP000000000002"]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptStateStartsFreshAndIsReplacedOnTheNextSave()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "home-ia-observation-store-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "scan-state.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{not valid json");
            var store = new IdentityObservationScanStateStore(path,
                NullLogger<IdentityObservationScanStateStore>.Instance);

            Assert.Null(store.Load());

            store.Save(new IdentityObservationScanState(0, 0,
                new Dictionary<string, DateTimeOffset>(), new Dictionary<string, int>()));
            Assert.NotNull(store.Load());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
