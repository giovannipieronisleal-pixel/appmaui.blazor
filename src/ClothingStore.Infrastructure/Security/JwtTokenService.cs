using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace ClothingStore.Infrastructure.Security;

/// <summary>
/// Serviço emissor e validador de tokens JWT (Padrão RFC 7519 HS256)
/// incluindo claims granulares de funcionário, validação estrita de header/issuer/audience
/// e tokens de desafio temporários isolados para 2FA.
/// </summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private const string PurposeAccessToken = "access";
    private const string PurposeTwoFactorChallenge = "2fa_challenge";
    private static readonly Lazy<byte[]> ProcessEphemeralSecret =
        new(() => RandomNumberGenerator.GetBytes(48));

    private readonly byte[] _secretKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expirationHours;

    public JwtTokenService(IConfiguration configuration)
    {
        _secretKey = ResolveSigningSecret(configuration);
        _issuer = configuration["Jwt:Issuer"] ?? "ClothingStore.Api";
        _audience = configuration["Jwt:Audience"] ?? "ClothingStore.Clients";
        _expirationHours = int.TryParse(configuration["Jwt:ExpirationHours"], out var h) && h > 0 ? h : 12;
    }

    public (string Token, DateTimeOffset ExpiresAt) GenerateToken(User user, Employee employee, string roleName)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(_expirationHours);

        var headerJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        });

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["employee_id"] = employee.Id.ToString(),
            ["email"] = user.Email,
            ["name"] = employee.FullName,
            ["role"] = roleName,
            ["can_manage_products"] = employee.HasProductManagementPermission(roleName) ? "true" : "false",
            ["can_manage_stock"] = employee.HasStockManagementPermission(roleName) ? "true" : "false",
            ["purpose"] = PurposeAccessToken,
            ["iss"] = _issuer,
            ["aud"] = _audience,
            ["nbf"] = now.AddSeconds(-30).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds()
        });

        var headerEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var unsignedToken = $"{headerEncoded}.{payloadEncoded}";
        var signature = ComputeSignature(unsignedToken);

        return ($"{unsignedToken}.{signature}", expiresAt);
    }

    public string GenerateTwoFactorChallengeToken(Guid userId, string email)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(5);

        var headerJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        });

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["email"] = email,
            ["purpose"] = PurposeTwoFactorChallenge,
            ["iss"] = _issuer,
            ["aud"] = _audience,
            ["nbf"] = now.AddSeconds(-15).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds()
        });

        var headerEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var unsignedToken = $"{headerEncoded}.{payloadEncoded}";
        var signature = ComputeSignature(unsignedToken);

        return $"{unsignedToken}.{signature}";
    }

    public bool ValidateTwoFactorChallengeToken(string? token, Guid expectedUserId)
    {
        if (!TryValidateAndParsePayload(token, out var root))
            return false;

        if (!root.TryGetProperty("purpose", out var purposeProp) ||
            !string.Equals(purposeProp.GetString(), PurposeTwoFactorChallenge, StringComparison.Ordinal))
        {
            return false;
        }

        if (!root.TryGetProperty("sub", out var subProp) ||
            !Guid.TryParse(subProp.GetString(), out var tokenUserId))
        {
            return false;
        }

        return tokenUserId == expectedUserId;
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (!TryValidateAndParsePayload(token, out var root))
            return null;

        if (root.TryGetProperty("purpose", out var purposeProp))
        {
            var purpose = purposeProp.GetString();
            if (!string.IsNullOrEmpty(purpose) &&
                !string.Equals(purpose, PurposeAccessToken, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var claims = new List<Claim>();
        if (root.TryGetProperty("sub", out var sub))
            claims.Add(new Claim(ClaimTypes.NameIdentifier, sub.GetString() ?? ""));
        if (root.TryGetProperty("employee_id", out var empId))
            claims.Add(new Claim("employee_id", empId.GetString() ?? ""));
        if (root.TryGetProperty("email", out var email))
            claims.Add(new Claim(ClaimTypes.Email, email.GetString() ?? ""));
        if (root.TryGetProperty("name", out var name))
            claims.Add(new Claim(ClaimTypes.Name, name.GetString() ?? ""));
        if (root.TryGetProperty("role", out var role))
            claims.Add(new Claim(ClaimTypes.Role, role.GetString() ?? ""));
        if (root.TryGetProperty("can_manage_products", out var cmp))
            claims.Add(new Claim("can_manage_products", cmp.GetString() ?? "false"));
        if (root.TryGetProperty("can_manage_stock", out var cms))
            claims.Add(new Claim("can_manage_stock", cms.GetString() ?? "false"));

        var identity = new ClaimsIdentity(claims, "JwtBearer");
        return new ClaimsPrincipal(identity);
    }

    private bool TryValidateAndParsePayload(string? token, out JsonElement rootElement)
    {
        rootElement = default;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
            return false;

        var unsignedToken = $"{parts[0]}.{parts[1]}";

        try
        {
            var providedSigBytes = Base64UrlDecode(parts[2]);
            var expectedSigBytes = ComputeSignatureBytes(unsignedToken);
            if (providedSigBytes.Length != expectedSigBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(providedSigBytes, expectedSigBytes))
            {
                return false;
            }

            var headerBytes = Base64UrlDecode(parts[0]);
            using var headerDoc = JsonDocument.Parse(headerBytes);
            var headerRoot = headerDoc.RootElement;
            if (!headerRoot.TryGetProperty("alg", out var algProp) ||
                !string.Equals(algProp.GetString(), "HS256", StringComparison.Ordinal))
            {
                return false;
            }

            if (headerRoot.TryGetProperty("typ", out var typProp) &&
                !string.Equals(typProp.GetString(), "JWT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var payloadBytes = Base64UrlDecode(parts[1]);
            using var payloadDoc = JsonDocument.Parse(payloadBytes);
            var root = payloadDoc.RootElement;
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (!root.TryGetProperty("exp", out var expProp) ||
                !expProp.TryGetInt64(out var expUnix) ||
                nowUnix > expUnix)
            {
                return false;
            }

            if (root.TryGetProperty("nbf", out var nbfProp) &&
                nbfProp.TryGetInt64(out var nbfUnix) &&
                nowUnix + 60 < nbfUnix)
            {
                return false;
            }

            if (!root.TryGetProperty("iss", out var issProp) ||
                !string.Equals(issProp.GetString(), _issuer, StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("aud", out var audProp) ||
                !string.Equals(audProp.GetString(), _audience, StringComparison.Ordinal))
            {
                return false;
            }

            rootElement = root.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ResolveSigningSecret(IConfiguration configuration)
    {
        var configuredSecret = configuration["JWT_SECRET_KEY"]
            ?? configuration["Jwt:SecretKey"]
            ?? Environment.GetEnvironmentVariable("JWT_SECRET_KEY");

        if (!string.IsNullOrWhiteSpace(configuredSecret))
        {
            var bytes = Encoding.UTF8.GetBytes(configuredSecret.Trim());
            if (bytes.Length >= 32)
                return bytes;
        }

        // Quando JWT_SECRET_KEY não está configurado, gera/persiste um segredo criptográfico aleatório
        // por instalação (ao lado do banco local) em vez de reutilizar uma chave fixa pública no código.
        var dbPath = configuration["Database:FilePath"];
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            try
            {
                var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
                if (!string.IsNullOrWhiteSpace(dbDir))
                {
                    Directory.CreateDirectory(dbDir);
                    var keyFilePath = Path.Combine(dbDir, ".youlink_jwt_secret.key");
                    if (File.Exists(keyFilePath))
                    {
                        var existingText = File.ReadAllText(keyFilePath).Trim();
                        if (existingText.Length >= 32)
                            return Encoding.UTF8.GetBytes(existingText);
                    }

                    var generatedSecret = Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
                    var tempPath = keyFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(tempPath, generatedSecret, Encoding.UTF8);
                    try
                    {
                        File.Move(tempPath, keyFilePath, overwrite: false);
                    }
                    catch
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }

                    if (File.Exists(keyFilePath))
                    {
                        var persistedText = File.ReadAllText(keyFilePath).Trim();
                        if (persistedText.Length >= 32)
                            return Encoding.UTF8.GetBytes(persistedText);
                    }

                    return Encoding.UTF8.GetBytes(generatedSecret);
                }
            }
            catch
            {
            }
        }

        return ProcessEphemeralSecret.Value;
    }

    private string ComputeSignature(string unsignedToken) =>
        Base64UrlEncode(ComputeSignatureBytes(unsignedToken));

    private byte[] ComputeSignatureBytes(string unsignedToken)
    {
        using var hmac = new HMACSHA256(_secretKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
