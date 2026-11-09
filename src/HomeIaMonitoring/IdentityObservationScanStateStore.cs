using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HomeIaMonitoring;

/// <summary>
/// Estado durável da varredura do producer: posição no spool, ciclos completos e clipes já
/// vistos/tentados. Contém apenas IDs públicos e timestamps; nunca embeddings, imagens ou
/// credenciais. A gravação é atômica (arquivo temporário + rename) para sobreviver a restart
/// sem deixar JSON parcial.
/// </summary>
public sealed record IdentityObservationScanState(
    int ScanOffset,
    long Sweeps,
    IReadOnlyDictionary<string, DateTimeOffset> Seen,
    IReadOnlyDictionary<string, int> FailedAttempts);

public sealed class IdentityObservationScanStateStore(
    string path, ILogger<IdentityObservationScanStateStore>? logger = null)
{
    private const int SchemaVersion = 1;
    private const int MaximumEntries = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IdentityObservationScanState? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;

            var document = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.SchemaVersion != SchemaVersion)
            {
                logger?.LogWarning(
                    "Estado de varredura de observação facial ignorado: esquema ausente ou incompatível.");
                return null;
            }

            var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var pair in document.Seen ?? [])
            {
                if (seen.Count >= MaximumEntries) break;
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == default) continue;
                seen[pair.Key] = pair.Value;
            }

            var failed = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in document.FailedAttempts ?? [])
            {
                if (failed.Count >= MaximumEntries) break;
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) continue;
                failed[pair.Key] = pair.Value;
            }

            return new(Math.Max(0, document.ScanOffset), Math.Max(0, document.Sweeps), seen, failed);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                "Estado de varredura de observação facial inválido; a varredura recomeça do início: {Error}.",
                exception.GetType().Name);
            return null;
        }
    }

    public void Save(IdentityObservationScanState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var document = new StateDocument(
                SchemaVersion,
                state.ScanOffset,
                state.Sweeps,
                state.Seen.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                state.FailedAttempts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                DateTimeOffset.UtcNow);

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                "Falha ao gravar o estado de varredura de observação facial: {Error}.",
                exception.GetType().Name);
        }
    }

    private sealed record StateDocument(
        int SchemaVersion,
        int ScanOffset,
        long Sweeps,
        Dictionary<string, DateTimeOffset>? Seen,
        Dictionary<string, int>? FailedAttempts,
        DateTimeOffset? SavedAtUtc);
}
