using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HomeIaMonitoring;

/// <summary>Self-contained AES-256-GCM envelope (nonce + ciphertext + tag).</summary>
public sealed record EncryptedBackupEnvelope(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

/// <summary>
/// Protects spool backup artifacts at rest. A missing or invalid key leaves the protector
/// unconfigured and <see cref="SpoolBackupService"/> fails closed instead of writing plaintext,
/// matching the posture of the portal's backup protector.
/// </summary>
public interface IBackupArtifactProtector
{
    bool IsConfigured { get; }

    string? ActiveKeyVersion { get; }

    EncryptedBackupEnvelope Seal(byte[] plaintext);

    byte[] Open(EncryptedBackupEnvelope envelope, string keyVersion);
}

/// <summary>
/// AES-256-GCM protector for spool backup files. The active key is a base64 32-byte secret paired
/// with a version label; an absent or invalid key leaves the protector unconfigured so the backup
/// service fails closed. Rotation is manual: an operator re-seals old artifacts before changing the
/// key, or retains the old key, because this slice does not keep a previous key.
/// </summary>
public sealed class AesGcmBackupArtifactProtector : IBackupArtifactProtector
{
    public const string KeySetting = "MONITORING_BACKUP_ENCRYPTION_KEY";
    public const string KeyVersionSetting = "MONITORING_BACKUP_ENCRYPTION_KEY_VERSION";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[]? _key;
    private readonly ILogger<AesGcmBackupArtifactProtector> _logger;

    public AesGcmBackupArtifactProtector(IConfiguration configuration,
        ILogger<AesGcmBackupArtifactProtector> logger)
    {
        _logger = logger;
        ActiveKeyVersion = ReadVersion(configuration);
        _key = ReadKey(configuration);
        if (IsConfigured)
            _logger.LogInformation("Cifra de backup do spool habilitada com a chave {KeyVersion}.",
                ActiveKeyVersion);
        else
            _logger.LogWarning("Cifra de backup do spool desabilitada: chave ausente ou inválida.");
    }

    public bool IsConfigured => _key is not null && ActiveKeyVersion is not null;

    public string? ActiveKeyVersion { get; }

    public EncryptedBackupEnvelope Seal(byte[] plaintext)
    {
        if (_key is null || ActiveKeyVersion is null)
            throw new InvalidOperationException("Cifra de backup indisponível: chave ausente ou inválida.");
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return new EncryptedBackupEnvelope(nonce, ciphertext, tag);
    }

    public byte[] Open(EncryptedBackupEnvelope envelope, string keyVersion)
    {
        if (_key is null || ActiveKeyVersion is null ||
            !string.Equals(ActiveKeyVersion, keyVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Chave de backup indisponível para a versão {keyVersion}.");
        if (envelope.Nonce.Length != NonceBytes || envelope.Tag.Length != TagBytes)
            throw new InvalidOperationException("Artefato de backup cifrado inválido.");
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                "Falha ao decifrar o artefato de backup: tag inválida ou chave divergente.");
        }
        return plaintext;
    }

    private byte[]? ReadKey(IConfiguration configuration)
    {
        var raw = configuration[KeySetting]?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var decoded = Convert.FromBase64String(raw);
            if (decoded.Length == KeyBytes) return decoded;
            _logger.LogError("Chave de backup inválida: {Setting} deve ter {Bytes} bytes.", KeySetting, KeyBytes);
        }
        catch (FormatException)
        {
            _logger.LogError("Chave de backup inválida: {Setting} não é base64.", KeySetting);
        }
        return null;
    }

    private string? ReadVersion(IConfiguration configuration)
    {
        var raw = configuration[KeyVersionSetting]?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.Length > 64 || raw.Any(char.IsControl))
        {
            _logger.LogError("Versão de chave de backup inválida em {Setting}.", KeyVersionSetting);
            return null;
        }
        return raw;
    }
}
