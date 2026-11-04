using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

/// <summary>
/// Credencial serviço-a-serviço de curta duração (P28). O portal é o emissor; o módulo
/// valida assinatura, audiência, residência, expiração e escopo antes de executar.
/// </summary>
public sealed record ServiceTokenOptions(string Mode, string? Secret, string Issuer, string Audience,
    string ResidenceId, int ClockSkewSeconds = 30)
{
    public const string ModeKey = "MONITORING_SERVICE_AUTH_MODE";
    public const string SecretKey = "MONITORING_SERVICE_TOKEN_SECRET";
    public const string IssuerKey = "MONITORING_SERVICE_TOKEN_ISSUER";
    public const string AudienceKey = "MONITORING_SERVICE_TOKEN_AUDIENCE";
    public const string ResidenceKey = "MONITORING_RESIDENCE_ID";

    public bool Enabled => Mode == "required";
    public bool Disabled => Mode == "disabled";

    public static ServiceTokenOptions FromConfiguration(IConfiguration configuration, bool isDevelopment)
    {
        var mode = (configuration[ModeKey] ?? (isDevelopment ? "disabled" : "required")).Trim()
            .ToLowerInvariant();
        if (mode is not ("required" or "disabled"))
            throw new InvalidOperationException("MONITORING_SERVICE_AUTH_MODE deve ser required ou disabled.");
        var secret = configuration[SecretKey]?.Trim();
        if (mode == "required" && (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32))
            throw new InvalidOperationException(
                "MONITORING_SERVICE_TOKEN_SECRET deve ter ao menos 32 bytes no modo required.");
        return new ServiceTokenOptions(mode, secret,
            (configuration[IssuerKey] ?? "home-ia-portal").Trim(),
            (configuration[AudienceKey] ?? "monitoring").Trim(),
            (configuration[ResidenceKey] ?? "home").Trim());
    }
}

public enum ServiceTokenStatus
{
    Valid,
    Missing,
    Malformed,
    InvalidSignature,
    Expired,
    WrongIssuer,
    WrongAudience,
    WrongResidence,
    InsufficientScope
}

public sealed record ServiceTokenValidation(ServiceTokenStatus Status, string? Subject, string? ResidenceId,
    IReadOnlyList<string> Scopes)
{
    public bool IsValid => Status == ServiceTokenStatus.Valid;
}

/// <summary>
/// Validador do token compacto HS256. Mantido independente do emissor do portal para que
/// uma regressão de contrato em um lado falhe o teste do outro.
/// </summary>
public static class ServiceTokenValidator
{
    public static ServiceTokenValidation Validate(string? authorizationHeader, ServiceTokenOptions options,
        string requiredScope, DateTimeOffset now)
    {
        if (options.Disabled)
            return new ServiceTokenValidation(ServiceTokenStatus.Valid, "auth-disabled", options.ResidenceId, []);
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new ServiceTokenValidation(ServiceTokenStatus.Missing, null, null, []);
        if (string.IsNullOrWhiteSpace(options.Secret))
            return new ServiceTokenValidation(ServiceTokenStatus.InvalidSignature, null, null, []);

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3)
            return new ServiceTokenValidation(ServiceTokenStatus.Malformed, null, null, []);

        byte[] headerBytes;
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            headerBytes = Base64Url.DecodeFromChars(parts[0]);
            payloadBytes = Base64Url.DecodeFromChars(parts[1]);
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (FormatException)
        {
            return new ServiceTokenValidation(ServiceTokenStatus.Malformed, null, null, []);
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Secret),
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        if (expected.Length != signature.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, signature))
            return new ServiceTokenValidation(ServiceTokenStatus.InvalidSignature, null, null, []);

        JsonDocument header;
        JsonDocument payload;
        try
        {
            header = JsonDocument.Parse(headerBytes);
            payload = JsonDocument.Parse(payloadBytes);
        }
        catch (JsonException)
        {
            return new ServiceTokenValidation(ServiceTokenStatus.Malformed, null, null, []);
        }

        using (header)
        using (payload)
        {
            var root = payload.RootElement;
            var alg = header.RootElement.TryGetProperty("alg", out var algValue) ? algValue.GetString() : null;
            if (!string.Equals(alg, "HS256", StringComparison.Ordinal))
                return new ServiceTokenValidation(ServiceTokenStatus.Malformed, null, null, []);
            if (!root.TryGetProperty("iss", out var issuer) ||
                !string.Equals(issuer.GetString(), options.Issuer, StringComparison.Ordinal))
                return new ServiceTokenValidation(ServiceTokenStatus.WrongIssuer, null, null, []);
            if (!root.TryGetProperty("aud", out var audience) ||
                !string.Equals(audience.GetString(), options.Audience, StringComparison.Ordinal))
                return new ServiceTokenValidation(ServiceTokenStatus.WrongAudience, null, null, []);
            var residence = root.TryGetProperty("residence_id", out var residenceValue)
                ? residenceValue.GetString()
                : null;
            if (!string.Equals(residence, options.ResidenceId, StringComparison.Ordinal))
                return new ServiceTokenValidation(ServiceTokenStatus.WrongResidence, null, residence, []);
            if (!root.TryGetProperty("exp", out var expValue) || !expValue.TryGetInt64(out var exp) ||
                DateTimeOffset.FromUnixTimeSeconds(exp) + TimeSpan.FromSeconds(options.ClockSkewSeconds) < now)
                return new ServiceTokenValidation(ServiceTokenStatus.Expired, null, residence, []);
            if (!root.TryGetProperty("iat", out var iatValue) || !iatValue.TryGetInt64(out var iat) ||
                DateTimeOffset.FromUnixTimeSeconds(iat) - TimeSpan.FromSeconds(options.ClockSkewSeconds) > now)
                return new ServiceTokenValidation(ServiceTokenStatus.Expired, null, residence, []);

            var scopes = root.TryGetProperty("scope", out var scopeValue) && scopeValue.GetString() is { } scopeText
                ? scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            if (!scopes.Contains(requiredScope, StringComparer.Ordinal))
                return new ServiceTokenValidation(ServiceTokenStatus.InsufficientScope, null, residence, scopes);

            var subject = root.TryGetProperty("sub", out var subValue) ? subValue.GetString() : null;
            return new ServiceTokenValidation(ServiceTokenStatus.Valid, subject, residence, scopes);
        }
    }
}
