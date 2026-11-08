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
/// incluindo claims granulares de funcionário e permissões.
/// </summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly byte[] _secretKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expirationHours;

    public JwtTokenService(IConfiguration configuration)
    {
        var rawSecret = configuration["JWT_SECRET_KEY"]
            ?? configuration["Jwt:SecretKey"]
            ?? "ClothingStore_Default_Dev_Secret_Key_2026_ChangeInProduction_998877!";

        _secretKey = Encoding.UTF8.GetBytes(rawSecret);
        _issuer = configuration["Jwt:Issuer"] ?? "ClothingStore.Api";
        _audience = configuration["Jwt:Audience"] ?? "ClothingStore.Clients";
        _expirationHours = int.TryParse(configuration["Jwt:ExpirationHours"], out var h) ? h : 12;
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
            ["iss"] = _issuer,
            ["aud"] = _audience,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds()
        });

        var headerEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var unsignedToken = $"{headerEncoded}.{payloadEncoded}";
        var signature = ComputeSignature(unsignedToken);

        return ($"{unsignedToken}.{signature}", expiresAt);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
            return null;

        var unsignedToken = $"{parts[0]}.{parts[1]}";
        var expectedSignature = ComputeSignature(unsignedToken);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(parts[2]),
                Encoding.UTF8.GetBytes(expectedSignature)))
        {
            return null;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expUnix))
            {
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expUnix)
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
        catch
        {
            return null;
        }
    }

    private string ComputeSignature(string unsignedToken)
    {
        using var hmac = new HMACSHA256(_secretKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
        return Base64UrlEncode(hash);
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

