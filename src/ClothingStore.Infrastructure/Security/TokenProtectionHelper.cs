using System.Security.Cryptography;
using System.Text;

namespace ClothingStore.Infrastructure.Security;

/// <summary>
/// Utilitário compartilhado AES-256-GCM para proteger tokens OAuth persistidos em disco.
/// Usa `YOULINK_TOKEN_ENCRYPTION_KEY` do ambiente quando configurada ou chave aleatória persistida em diretório privado.
/// </summary>
public static class TokenProtectionHelper
{
    public const string EncryptedPrefix = "enc:v1:";

    public static byte[] GetOrCreateEncryptionKey(string keyStoreFilePath)
    {
        var envKey = Environment.GetEnvironmentVariable("YOULINK_TOKEN_ENCRYPTION_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return SHA256.HashData(Encoding.UTF8.GetBytes(envKey.Trim()));

        try
        {
            if (File.Exists(keyStoreFilePath))
            {
                var bytes = Convert.FromBase64String(File.ReadAllText(keyStoreFilePath, Encoding.UTF8).Trim());
                if (bytes.Length == 32)
                    return bytes;
            }
        }
        catch
        {
        }

        var dir = Path.GetDirectoryName(keyStoreFilePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var generated = RandomNumberGenerator.GetBytes(32);
        var temp = $"{keyStoreFilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, Convert.ToBase64String(generated), Encoding.UTF8);
        File.Move(temp, keyStoreFilePath, overwrite: true);
        return generated;
    }

    public static string? ProtectSecret(string? plain, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(plain))
            return null;

        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return EncryptedPrefix + Convert.ToBase64String(payload);
    }

    public static string? UnprotectSecret(string? stored, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return stored;

        try
        {
            var payload = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
            if (payload.Length < 29)
                return null;

            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var cipher = payload.AsSpan(28);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
