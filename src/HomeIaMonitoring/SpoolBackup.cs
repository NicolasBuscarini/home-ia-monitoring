using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HomeIaMonitoring;

/// <summary>
/// Verified backup of the durable metadata of the Monitoring spool. It copies only small JSON
/// state (clip manifests, event journal and outbox, analysis results, roster, retention and gaps)
/// into <c>MONITORING_BACKUP_DIRECTORY</c>, never media or biometric ciphertext. Each file is
/// hashed with SHA-256 and described by a <c>manifest.json</c> whose integrity is also hashed.
/// </summary>
public sealed record SpoolBackupArtifact(
    string DirectoryName,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes,
    string Sha256,
    int FileCount);

/// <summary>Result of an isolated restore drill; it never touches the live spool.</summary>
public sealed record SpoolBackupResult(
    string RestoredDirectory,
    string SourceDirectoryName,
    long SizeBytes,
    int FileCount);

/// <summary>
/// Durable-metadata backup and isolated restore of the Monitoring spool. Restore into the live
/// spool remains an operator procedure with the services stopped; <see cref="RestoreAsync"/> only
/// proves, in an isolated directory, that a verified artifact can be brought back file by file.
/// </summary>
public interface ISpoolBackupService
{
    Task<SpoolBackupArtifact> CreateAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SpoolBackupArtifact>> ListAsync(CancellationToken cancellationToken);

    Task<SpoolBackupResult> RestoreAsync(string directoryName, string targetDirectory,
        CancellationToken cancellationToken);

    Task DeleteAsync(string directoryName, CancellationToken cancellationToken);
}

public sealed class SpoolBackupService : ISpoolBackupService
{
    public const string BackupDirectoryKey = "MONITORING_BACKUP_DIRECTORY";

    private const string ManifestFileName = "manifest.json";
    private const int ManifestSchemaVersion = 1;
    private const int MaximumListedArtifacts = 50;

    /// <summary>
    /// Relative spool directories holding durable metadata. Media (<c>clips/media</c>,
    /// <c>clips/thumbnails</c>, <c>clips/playback</c>, <c>segments</c>), the ephemeral segment
    /// manifests (<c>manifests</c>), the temp <c>work</c> directory and the biometric ciphertext
    /// (<c>biometrics</c>, excluded by ADR072) are deliberately not backed up here.
    /// </summary>
    private static readonly string[] MetadataDirectories =
    [
        "clips/manifests",
        "events",
        "analysis",
        "roster",
        "retention",
        "gaps",
        "identity-observations"
    ];

    private static readonly string[] MetadataFiles = ["capture-settings.json"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string spoolDirectory;
    private readonly string backupDirectory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SpoolBackupService> logger;

    public SpoolBackupService(MonitoringOptions monitoring, IConfiguration configuration,
        TimeProvider timeProvider, ILogger<SpoolBackupService> logger)
    {
        this.timeProvider = timeProvider;
        this.logger = logger;
        spoolDirectory = monitoring.SpoolDirectory;
        var configured = configuration[BackupDirectoryKey];
        backupDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(spoolDirectory, "..", "backups"))
            : configured;
        if (!Path.IsPathFullyQualified(backupDirectory))
            throw new InvalidOperationException($"{BackupDirectoryKey} deve ser um caminho absoluto.");
        var spoolFull = Path.GetFullPath(spoolDirectory).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        var backupFull = Path.GetFullPath(backupDirectory).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (backupFull.StartsWith(spoolFull, StringComparison.Ordinal))
            throw new InvalidOperationException($"{BackupDirectoryKey} não pode estar dentro do spool.");
    }

    public async Task<SpoolBackupArtifact> CreateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        var createdAtUtc = timeProvider.GetUtcNow();
        var directoryName = ResolveBackupDirectoryName(createdAtUtc);
        var targetDirectory = Path.Combine(backupDirectory, directoryName);
        var entries = new List<SpoolBackupEntry>();
        try
        {
            Directory.CreateDirectory(targetDirectory);
            long totalBytes = 0;
            foreach (var relative in EnumerateSourceFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(spoolDirectory, relative);
                var destination = Path.Combine(targetDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var sha256 = await CopyAndHashAsync(source, destination, cancellationToken);
                var size = new FileInfo(destination).Length;
                totalBytes += size;
                entries.Add(new SpoolBackupEntry(relative, size, sha256));
            }

            var integritySha256 = ComputeIntegrityHash(entries);
            var manifest = new SpoolBackupManifest(ManifestSchemaVersion, directoryName, createdAtUtc,
                integritySha256, entries.ToArray());
            await WriteManifestAsync(targetDirectory, manifest, cancellationToken);
            logger.LogInformation(
                "Backup do spool criado: {DirectoryName} ({FileCount} arquivos, {SizeBytes} bytes, sha256 {Sha256}).",
                directoryName, entries.Count, totalBytes, integritySha256);
            return new SpoolBackupArtifact(directoryName, createdAtUtc, totalBytes, integritySha256, entries.Count);
        }
        catch
        {
            if (Directory.Exists(targetDirectory)) Directory.Delete(targetDirectory, recursive: true);
            throw;
        }
    }

    public async Task<IReadOnlyList<SpoolBackupArtifact>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(backupDirectory)) return [];
        var artifacts = new List<SpoolBackupArtifact>();
        foreach (var directory in Directory.EnumerateDirectories(backupDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(Path.GetFileName(directory), cancellationToken);
            if (manifest is null) continue;
            artifacts.Add(new SpoolBackupArtifact(manifest.DirectoryName, manifest.CreatedAtUtc,
                manifest.Entries.Sum(entry => entry.SizeBytes), manifest.IntegritySha256,
                manifest.Entries.Length));
        }
        return artifacts
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.DirectoryName, StringComparer.Ordinal)
            .Take(MaximumListedArtifacts)
            .ToArray();
    }

    public async Task<SpoolBackupResult> RestoreAsync(string directoryName, string targetDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (Path.GetFileName(directoryName) != directoryName)
            throw new InvalidOperationException("O nome do backup do spool deve ser um nome de diretório, sem caminho.");
        if (!Path.IsPathFullyQualified(targetDirectory))
            throw new InvalidOperationException("O diretório de destino do restore deve ser um caminho absoluto.");

        var manifest = await ReadManifestAsync(directoryName, cancellationToken)
            ?? throw new InvalidOperationException($"Manifesto de backup do spool ausente ou ilegível: {directoryName}.");
        var sourceDirectory = Path.Combine(backupDirectory, directoryName);

        long totalBytes = 0;
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(sourceDirectory, entry.RelativePath);
            if (!File.Exists(source))
                throw new InvalidOperationException($"Arquivo de backup ausente: {entry.RelativePath}.");
            var sourceHash = await HashFileAsync(source, cancellationToken);
            if (!string.Equals(sourceHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Arquivo de backup não confere com o SHA-256 do manifesto: {entry.RelativePath}.");

            var destination = Path.Combine(targetDirectory, entry.RelativePath);
            if (File.Exists(destination))
                throw new InvalidOperationException($"O destino do restore já existe: {destination}.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
            var restoredHash = await HashFileAsync(destination, cancellationToken);
            if (!string.Equals(restoredHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cópia restaurada não confere com o SHA-256: {entry.RelativePath}.");
            totalBytes += entry.SizeBytes;
        }

        if (!string.Equals(ComputeIntegrityHash(manifest.Entries), manifest.IntegritySha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A integridade do manifesto do backup do spool não confere.");

        logger.LogInformation(
            "Restore de ensaio do spool concluído: {DirectoryName} -> {TargetDirectory} ({FileCount} arquivos, {SizeBytes} bytes).",
            directoryName, targetDirectory, manifest.Entries.Length, totalBytes);
        return new SpoolBackupResult(targetDirectory, directoryName, totalBytes, manifest.Entries.Length);
    }

    public Task DeleteAsync(string directoryName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        if (Path.GetFileName(directoryName) != directoryName)
            throw new InvalidOperationException("O nome do backup do spool deve ser um nome de diretório, sem caminho.");
        var target = Path.Combine(backupDirectory, directoryName);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
            logger.LogInformation("Backup do spool removido: {DirectoryName}.", directoryName);
        }
        return Task.CompletedTask;
    }

    private IEnumerable<string> EnumerateSourceFiles()
    {
        foreach (var directory in MetadataDirectories)
        {
            var absolute = Path.Combine(spoolDirectory, directory);
            if (!Directory.Exists(absolute)) continue;
            foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                yield return Normalize(Path.GetRelativePath(spoolDirectory, file));
            }
        }
        foreach (var file in MetadataFiles)
        {
            var absolute = Path.Combine(spoolDirectory, file);
            if (File.Exists(absolute)) yield return Normalize(file);
        }
    }

    private string ResolveBackupDirectoryName(DateTimeOffset createdAtUtc)
    {
        var stamp = createdAtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var candidate = $"spool-backup-{stamp}";
        for (var suffix = 1; Directory.Exists(Path.Combine(backupDirectory, candidate)); suffix++)
        {
            if (suffix > 20)
                throw new InvalidOperationException("Não foi possível alocar um nome único para o backup do spool.");
            candidate = $"spool-backup-{stamp}-{suffix}";
        }
        return candidate;
    }

    private async Task<SpoolBackupManifest?> ReadManifestAsync(string directoryName, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(backupDirectory, directoryName, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            return await JsonSerializer.DeserializeAsync<SpoolBackupManifest>(stream, JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteManifestAsync(string targetDirectory, SpoolBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(targetDirectory, ManifestFileName);
        var temporaryPath = manifestPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        File.Move(temporaryPath, manifestPath, true);
    }

    private static async Task<string> CopyAndHashAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 81920, true))
            await input.CopyToAsync(output, cancellationToken);
        return await HashFileAsync(destination, cancellationToken);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
    }

    private static string ComputeIntegrityHash(IReadOnlyList<SpoolBackupEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            builder.Append(entry.RelativePath).Append('|').Append(entry.SizeBytes).Append('|')
                .Append(entry.Sha256).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record SpoolBackupEntry(string RelativePath, long SizeBytes, string Sha256);

    private sealed record SpoolBackupManifest(int Schema, string DirectoryName, DateTimeOffset CreatedAtUtc,
        string IntegritySha256, SpoolBackupEntry[] Entries);
}
