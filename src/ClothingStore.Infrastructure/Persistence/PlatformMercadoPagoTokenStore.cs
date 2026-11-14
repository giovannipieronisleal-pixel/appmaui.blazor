using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Infrastructure.Security;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Gerencia as credenciais Mercado Pago do dono da plataforma para cobrança automática de planos do YouLink.
/// Prioriza a variável de ambiente `MP_PLATFORM_ACCESS_TOKEN` / `MERCADOPAGO_PLATFORM_ACCESS_TOKEN` (resiliente a reinícios)
/// e suporta fallback OAuth criptografado em disco com AES-256-GCM (`platform_mp_credentials.json`).
/// </summary>
public sealed class PlatformMercadoPagoTokenStore : IPlatformMercadoPagoTokenStore
{
    private readonly string _storeFilePath;
    private readonly string _keyStoreFilePath;
    private readonly Func<string?> _envTokenReader;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public PlatformMercadoPagoTokenStore(
        string? rootDirectory = null,
        Func<string?>? envTokenReader = null)
    {
        var resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data")
            : rootDirectory.Trim();

        _storeFilePath = Path.Combine(resolvedRoot, "platform_mp_credentials.json");
        _keyStoreFilePath = Path.Combine(resolvedRoot, "token_master.key");
        _envTokenReader = envTokenReader ?? DefaultEnvTokenReader;
    }

    private static string? DefaultEnvTokenReader()
    {
        var token = Environment.GetEnvironmentVariable("MP_PLATFORM_ACCESS_TOKEN")
                    ?? Environment.GetEnvironmentVariable("MERCADOPAGO_PLATFORM_ACCESS_TOKEN");
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public async Task<string?> GetValidAccessTokenAsync(
        IMercadoPagoPixService? refreshService = null,
        CancellationToken ct = default)
    {
        var envToken = _envTokenReader();
        if (!string.IsNullOrWhiteSpace(envToken))
            return envToken.Trim();

        StoredCredentials? doc;
        string? decryptedAccess;
        string? decryptedRefresh;

        await _lock.WaitAsync(ct);
        try
        {
            doc = LoadDocumentUnsafe();
            if (doc is null || string.IsNullOrWhiteSpace(doc.EncryptedAccessToken))
                return null;

            var key = TokenProtectionHelper.GetOrCreateEncryptionKey(_keyStoreFilePath);
            decryptedAccess = TokenProtectionHelper.UnprotectSecret(doc.EncryptedAccessToken, key);
            decryptedRefresh = TokenProtectionHelper.UnprotectSecret(doc.EncryptedRefreshToken, key);
        }
        finally
        {
            _lock.Release();
        }

        if (string.IsNullOrWhiteSpace(decryptedAccess))
            return null;

        var needsRefresh = doc.ExpiresAt.HasValue && doc.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(10);
        if (!needsRefresh || string.IsNullOrWhiteSpace(decryptedRefresh) || refreshService is null)
            return decryptedAccess;

        var refreshed = await refreshService.RefreshAccessTokenAsync(decryptedRefresh, ct);
        if (!refreshed.Success || string.IsNullOrWhiteSpace(refreshed.AccessToken))
            return decryptedAccess;

        await SaveOAuthTokensAsync(
            refreshed.AccessToken,
            refreshed.RefreshToken ?? decryptedRefresh,
            refreshed.ExpiresInSeconds,
            refreshed.UserId ?? doc.UserId,
            refreshed.AccountLabel ?? doc.AccountLabel,
            ct);

        return refreshed.AccessToken;
    }

    public async Task<PlatformMercadoPagoConnectionStatus> GetConnectionStatusAsync(
        bool isAppConfigured,
        CancellationToken ct = default)
    {
        var envToken = _envTokenReader();
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return new PlatformMercadoPagoConnectionStatus(
                IsAppConfigured: isAppConfigured,
                IsConnected: true,
                AccountLabel: "Token da Plataforma Configurado (Render/Ambiente)",
                UsingEnvironmentToken: true);
        }

        await _lock.WaitAsync(ct);
        try
        {
            var doc = LoadDocumentUnsafe();
            if (doc is null || string.IsNullOrWhiteSpace(doc.EncryptedAccessToken))
            {
                return new PlatformMercadoPagoConnectionStatus(
                    IsAppConfigured: isAppConfigured,
                    IsConnected: false,
                    AccountLabel: null,
                    UsingEnvironmentToken: false);
            }

            var key = TokenProtectionHelper.GetOrCreateEncryptionKey(_keyStoreFilePath);
            var valid = !string.IsNullOrWhiteSpace(TokenProtectionHelper.UnprotectSecret(doc.EncryptedAccessToken, key));
            return new PlatformMercadoPagoConnectionStatus(
                IsAppConfigured: isAppConfigured,
                IsConnected: valid,
                AccountLabel: valid ? (doc.AccountLabel ?? "Conta Mercado Pago Conectada (OAuth)") : null,
                UsingEnvironmentToken: false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveOAuthTokensAsync(
        string accessToken,
        string? refreshToken,
        int expiresInSeconds,
        string? userId,
        string? accountLabel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token do Mercado Pago é obrigatório.", nameof(accessToken));

        await _lock.WaitAsync(ct);
        try
        {
            var key = TokenProtectionHelper.GetOrCreateEncryptionKey(_keyStoreFilePath);
            var existing = LoadDocumentUnsafe();
            var doc = new StoredCredentials
            {
                EncryptedAccessToken = TokenProtectionHelper.ProtectSecret(accessToken.Trim(), key),
                EncryptedRefreshToken = !string.IsNullOrWhiteSpace(refreshToken)
                    ? TokenProtectionHelper.ProtectSecret(refreshToken.Trim(), key)
                    : existing?.EncryptedRefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds > 0 ? expiresInSeconds : 15_552_000),
                UserId = string.IsNullOrWhiteSpace(userId) ? existing?.UserId : userId.Trim(),
                AccountLabel = string.IsNullOrWhiteSpace(accountLabel) ? existing?.AccountLabel : accountLabel.Trim(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            SaveDocumentUnsafe(doc);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearOAuthTokensAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(_storeFilePath))
                File.Delete(_storeFilePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    private StoredCredentials? LoadDocumentUnsafe()
    {
        try
        {
            if (!File.Exists(_storeFilePath))
                return null;

            var json = File.ReadAllText(_storeFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<StoredCredentials>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void SaveDocumentUnsafe(StoredCredentials doc)
    {
        var dir = Path.GetDirectoryName(_storeFilePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var temp = $"{_storeFilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(doc, JsonOptions), Encoding.UTF8);
        File.Move(temp, _storeFilePath, overwrite: true);
    }

    private sealed class StoredCredentials
    {
        public string? EncryptedAccessToken { get; set; }
        public string? EncryptedRefreshToken { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string? UserId { get; set; }
        public string? AccountLabel { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
