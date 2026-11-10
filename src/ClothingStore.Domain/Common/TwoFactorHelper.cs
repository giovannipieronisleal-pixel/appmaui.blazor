using System.Security.Cryptography;
using System.Text;

namespace ClothingStore.Domain.Common;

/// <summary>
/// Utilitário seguro para Autenticação em Duas Etapas (2FA) via RFC 6238 TOTP
/// (compatível com Google Authenticator, Microsoft Authenticator e Authy)
/// e códigos de emergência/recuperação.
/// </summary>
public static class TwoFactorHelper
{
    private const string Base32Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Gera uma chave secreta Base32 criptograficamente segura para 2FA.
    /// </summary>
    public static string GenerateBase32Secret(int byteCount = 10)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        var sb = new StringBuilder();
        int bitBuffer = 0;
        int bitCount = 0;

        foreach (var b in bytes)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                int index = (bitBuffer >> bitCount) & 0x1F;
                sb.Append(Base32Chars[index]);
            }
        }

        if (bitCount > 0)
        {
            int index = (bitBuffer << (5 - bitCount)) & 0x1F;
            sb.Append(Base32Chars[index]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converte chave Base32 em bytes.
    /// </summary>
    public static byte[] DecodeBase32(string base32)
    {
        if (string.IsNullOrWhiteSpace(base32))
            return Array.Empty<byte>();

        var clean = base32.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
        var byteList = new List<byte>();
        int bitBuffer = 0;
        int bitCount = 0;

        foreach (var c in clean)
        {
            int index = Base32Chars.IndexOf(c);
            if (index < 0)
                continue;

            bitBuffer = (bitBuffer << 5) | index;
            bitCount += 5;

            if (bitCount >= 8)
            {
                bitCount -= 8;
                byteList.Add((byte)((bitBuffer >> bitCount) & 0xFF));
            }
        }

        return byteList.ToArray();
    }

    /// <summary>
    /// Calcula o código TOTP de 6 dígitos para o instante de tempo especificado.
    /// </summary>
    public static string ComputeTotp(string base32Secret, long timeStep)
    {
        var key = DecodeBase32(base32Secret);
        if (key.Length == 0)
            return "000000";

        var timeBytes = BitConverter.GetBytes(timeStep);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(timeBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(timeBytes);

        int offset = hash[^1] & 0x0F;
        int binaryCode = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);

        int otp = binaryCode % 1_000_000;
        return otp.ToString("D6");
    }

    /// <summary>
    /// Gera o código TOTP de 6 dígitos atual.
    /// </summary>
    public static string GenerateCurrentTotp(string base32Secret)
    {
        long timeStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return ComputeTotp(base32Secret, timeStep);
    }

    /// <summary>
    /// Valida se o código de 6 dígitos informado pelo usuário confere com o TOTP (com janela de tolerância de ±1 passo = ±30s).
    /// </summary>
    public static bool VerifyTotp(string base32Secret, string inputCode, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(inputCode))
            return false;

        var cleanCode = inputCode.Trim().Replace(" ", "").Replace("-", "");
        if (cleanCode.Length != 6)
            return false;

        long currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        for (int i = -window; i <= window; i++)
        {
            var expected = ComputeTotp(base32Secret, currentStep + i);
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(cleanCode)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gera códigos de recuperação de emergência (ex: 8392-1049).
    /// </summary>
    public static List<string> GenerateBackupCodes(int count = 4)
    {
        var codes = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int p1 = RandomNumberGenerator.GetInt32(1000, 9999);
            int p2 = RandomNumberGenerator.GetInt32(1000, 9999);
            codes.Add($"{p1}-{p2}");
        }
        return codes;
    }

    /// <summary>
    /// Monta a URI padrão otpauth:// para escaneamento em apps autenticadores.
    /// </summary>
    public static string BuildOtpAuthUri(string base32Secret, string emailOrUser, string issuer = "MinhaLoja")
    {
        var cleanSecret = base32Secret.Trim().ToUpperInvariant();
        var encodedUser = Uri.EscapeDataString(emailOrUser.Trim());
        var encodedIssuer = Uri.EscapeDataString(issuer.Trim());
        return $"otpauth://totp/{encodedIssuer}:{encodedUser}?secret={cleanSecret}&issuer={encodedIssuer}&digits=6&period=30";
    }
}

