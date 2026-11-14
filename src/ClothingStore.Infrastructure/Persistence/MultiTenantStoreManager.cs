using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Infrastructure.Security;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Gerenciador multi-tenant baseado em repositórios isolados por loja (`App_Data/tenants/{slug}.json`),
/// com índice central de propriedade (`PublishTokenHash`), criptografia AES-GCM para tokens OAuth do Mercado Pago
/// e tabela de roteamento de webhooks por `ProviderPaymentId`.
/// Totalmente retrocompatível com implantações single-tenant no repositório raiz.
/// </summary>
public sealed class MultiTenantStoreManager : IMultiTenantStoreResolver
{
    private readonly IClothingStoreRepository _defaultRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly string _rootDirectory;
    private readonly string _tenantsDirectory;
    private readonly string _indexFilePath;
    private readonly string _keyStoreFilePath;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ClothingStoreRepository> _tenantRepos = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public MultiTenantStoreManager(
        IClothingStoreRepository defaultRepository,
        IPasswordHasher passwordHasher,
        string? customRootDirectory = null)
    {
        _defaultRepository = defaultRepository;
        _passwordHasher = passwordHasher;

        if (!string.IsNullOrWhiteSpace(customRootDirectory))
        {
            _rootDirectory = customRootDirectory;
        }
        else if (defaultRepository is ClothingStoreRepository concreteRepo)
        {
            _rootDirectory = Path.GetDirectoryName(Path.GetFullPath(concreteRepo.StorageFilePath))
                ?? Path.Combine(AppContext.BaseDirectory, "App_Data");
        }
        else
        {
            _rootDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data");
        }

        _tenantsDirectory = Path.Combine(_rootDirectory, "tenants");
        _indexFilePath = Path.Combine(_rootDirectory, "tenants_index.json");
        _keyStoreFilePath = Path.Combine(_rootDirectory, ".mp_keystore.key");
    }

    public string RootStorageDirectory => _rootDirectory;
    public IClothingStoreRepository DefaultRepository => _defaultRepository;

    public string NormalizeStoreSlug(string? rawSlugOrName)
    {
        var candidate = Category.GenerateSlug(rawSlugOrName ?? string.Empty);
        var sanitized = Regex.Replace(candidate, @"[^a-z0-9-]", string.Empty).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "minha-loja" : sanitized;
    }

    public async Task<IClothingStoreRepository?> ResolveReadRepositoryAsync(
        string? storeSlug,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storeSlug))
            return _defaultRepository;

        var normalized = NormalizeStoreSlug(storeSlug);
        var tenantPath = GetTenantDbPath(normalized);
        if (File.Exists(tenantPath))
        {
            return GetOrCreateTenantRepo(normalized);
        }

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            if (index.Tenants.ContainsKey(normalized))
                return GetOrCreateTenantRepo(normalized);
        }
        finally
        {
            _indexLock.Release();
        }

        // Fallback retrocompatível para implantações single-tenant (Render próprio) onde tudo fica no repositório raiz.
        var rootSettings = await _defaultRepository.GetStoreSettingsAsync(ct);
        var rootSlug = NormalizeStoreSlug(string.IsNullOrWhiteSpace(rootSettings.StoreSlug)
            ? rootSettings.StoreName
            : rootSettings.StoreSlug);
        var rootNameSlug = NormalizeStoreSlug(rootSettings.StoreName);

        if (string.Equals(rootSlug, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rootNameSlug, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return _defaultRepository;
        }

        return null;
    }

    public async Task<TenantPublishAuthorizationResult> AuthorizeAndResolvePublishRepositoryAsync(
        string? requestedSlug,
        string? syncKeyHeader,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        CancellationToken ct = default)
    {
        var hasRequestedSlug = !string.IsNullOrWhiteSpace(requestedSlug);
        var normalized = NormalizeStoreSlug(requestedSlug);

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();

            // Retrocompatibilidade BYO Render single-tenant:
            // Se veio com X-YouLink-Sync-Key válido e não há outros tenants gravados no hub central,
            // grava diretamente no repositório raiz (permitindo "/" e "/loja/{slug}").
            if (hasValidSyncKey &&
                !hasRequestedSlug)
            {
                return new TenantPublishAuthorizationResult(
                    Authorized: true,
                    IsSlugConflict: false,
                    NormalizedStoreSlug: normalized,
                    Repository: _defaultRepository,
                    IssuedPublishToken: null,
                    ErrorMessage: null);
            }

            if (hasValidSyncKey &&
                index.Tenants.Count == 0 &&
                !File.Exists(GetTenantDbPath(normalized)))
            {
                return new TenantPublishAuthorizationResult(
                    Authorized: true,
                    IsSlugConflict: false,
                    NormalizedStoreSlug: normalized,
                    Repository: _defaultRepository,
                    IssuedPublishToken: null,
                    ErrorMessage: null);
            }

            index.Tenants.TryGetValue(normalized, out var existing);

            if (existing is null)
            {
                var issuedToken = GenerateSecretToken();
                var now = DateTimeOffset.UtcNow;
                index.Tenants[normalized] = new TenantSecretCredentials
                {
                    StoreSlug = normalized,
                    PublishTokenHash = HashToken(issuedToken),
                    OwnerEmail = NormalizeEmail(authenticatedOwnerEmail),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                SaveIndexUnsafe(index);

                var repo = GetOrCreateTenantRepo(normalized);
                return new TenantPublishAuthorizationResult(
                    Authorized: true,
                    IsSlugConflict: false,
                    NormalizedStoreSlug: normalized,
                    Repository: repo,
                    IssuedPublishToken: issuedToken,
                    ErrorMessage: null);
            }

            var tokenMatches = !string.IsNullOrWhiteSpace(publishTokenHeader) &&
                               VerifyTokenHash(publishTokenHeader, existing.PublishTokenHash);

            var ownerMatches = !string.IsNullOrWhiteSpace(authenticatedOwnerEmail) &&
                               !string.IsNullOrWhiteSpace(existing.OwnerEmail) &&
                               string.Equals(NormalizeEmail(authenticatedOwnerEmail), existing.OwnerEmail, StringComparison.OrdinalIgnoreCase);

            if (hasValidSyncKey || tokenMatches || ownerMatches)
            {
                string? rotatedToken = null;
                if (!tokenMatches && (hasValidSyncKey || ownerMatches))
                {
                    rotatedToken = GenerateSecretToken();
                    existing.PublishTokenHash = HashToken(rotatedToken);
                }

                if (string.IsNullOrWhiteSpace(existing.OwnerEmail) && !string.IsNullOrWhiteSpace(authenticatedOwnerEmail))
                {
                    existing.OwnerEmail = NormalizeEmail(authenticatedOwnerEmail);
                }

                existing.UpdatedAt = DateTimeOffset.UtcNow;
                SaveIndexUnsafe(index);

                return new TenantPublishAuthorizationResult(
                    Authorized: true,
                    IsSlugConflict: false,
                    NormalizedStoreSlug: normalized,
                    Repository: GetOrCreateTenantRepo(normalized),
                    IssuedPublishToken: rotatedToken,
                    ErrorMessage: null);
            }

            return new TenantPublishAuthorizationResult(
                Authorized: false,
                IsSlugConflict: true,
                NormalizedStoreSlug: normalized,
                Repository: null,
                IssuedPublishToken: null,
                ErrorMessage: $"O endereço '/loja/{normalized}' já está em uso por outra loja neste servidor. Escolha outro nome/slug ou conecte com a conta/token original da loja.");
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<bool> AuthorizeTenantOrdersPullAsync(
        string? storeSlug,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        CancellationToken ct = default)
    {
        if (hasValidSyncKey)
            return true;

        if (string.IsNullOrWhiteSpace(storeSlug))
            return false;

        var normalized = NormalizeStoreSlug(storeSlug);

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            if (!index.Tenants.TryGetValue(normalized, out var entry))
                return false;

            if (!string.IsNullOrWhiteSpace(publishTokenHeader) &&
                VerifyTokenHash(publishTokenHeader, entry.PublishTokenHash))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(authenticatedOwnerEmail) &&
                !string.IsNullOrWhiteSpace(entry.OwnerEmail) &&
                string.Equals(NormalizeEmail(authenticatedOwnerEmail), entry.OwnerEmail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListTenantSlugsAsync(CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            return index.Tenants.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<TenantSecretCredentials?> GetTenantCredentialsAsync(
        string? storeSlug,
        CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(storeSlug)
            ? "__root__"
            : NormalizeStoreSlug(storeSlug);

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            if (!index.Tenants.TryGetValue(normalized, out var entry))
            {
                if (normalized == "__root__")
                    return null;

                if (!File.Exists(GetTenantDbPath(normalized)) &&
                    index.Tenants.TryGetValue("__root__", out var rootEntry))
                {
                    entry = rootEntry;
                }
                else
                {
                    return null;
                }
            }

            var key = TokenProtectionHelper.GetOrCreateEncryptionKey(_keyStoreFilePath);
            return new TenantSecretCredentials
            {
                StoreSlug = entry.StoreSlug,
                PublishTokenHash = entry.PublishTokenHash,
                OwnerEmail = entry.OwnerEmail,
                MercadoPagoAccessToken = TokenProtectionHelper.UnprotectSecret(entry.MercadoPagoAccessToken, key),
                MercadoPagoRefreshToken = TokenProtectionHelper.UnprotectSecret(entry.MercadoPagoRefreshToken, key),
                MercadoPagoTokenExpiresAt = entry.MercadoPagoTokenExpiresAt,
                MercadoPagoUserId = entry.MercadoPagoUserId,
                MercadoPagoAccountLabel = entry.MercadoPagoAccountLabel,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt
            };
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task SaveMercadoPagoTokensAsync(
        string? storeSlug,
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAt,
        string? userId,
        string? accountLabel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token do Mercado Pago é obrigatório.", nameof(accessToken));

        var normalized = string.IsNullOrWhiteSpace(storeSlug)
            ? "__root__"
            : NormalizeStoreSlug(storeSlug);

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            var key = TokenProtectionHelper.GetOrCreateEncryptionKey(_keyStoreFilePath);
            var now = DateTimeOffset.UtcNow;

            if (!index.Tenants.TryGetValue(normalized, out var entry))
            {
                entry = new TenantSecretCredentials
                {
                    StoreSlug = normalized,
                    CreatedAt = now
                };
                index.Tenants[normalized] = entry;
            }

            entry.MercadoPagoAccessToken = TokenProtectionHelper.ProtectSecret(accessToken.Trim(), key);
            if (!string.IsNullOrWhiteSpace(refreshToken))
                entry.MercadoPagoRefreshToken = TokenProtectionHelper.ProtectSecret(refreshToken.Trim(), key);
            entry.MercadoPagoTokenExpiresAt = expiresAt;
            entry.MercadoPagoUserId = string.IsNullOrWhiteSpace(userId) ? entry.MercadoPagoUserId : userId.Trim();
            entry.MercadoPagoAccountLabel = string.IsNullOrWhiteSpace(accountLabel) ? entry.MercadoPagoAccountLabel : accountLabel.Trim();
            entry.UpdatedAt = now;

            SaveIndexUnsafe(index);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task ClearMercadoPagoTokensAsync(
        string? storeSlug,
        CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(storeSlug)
            ? "__root__"
            : NormalizeStoreSlug(storeSlug);

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            if (index.Tenants.TryGetValue(normalized, out var entry))
            {
                entry.MercadoPagoAccessToken = null;
                entry.MercadoPagoRefreshToken = null;
                entry.MercadoPagoTokenExpiresAt = null;
                entry.MercadoPagoUserId = null;
                entry.MercadoPagoAccountLabel = null;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                SaveIndexUnsafe(index);
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task RegisterProviderPaymentIndexAsync(
        string providerPaymentId,
        string? storeSlug,
        Guid orderId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentId) || orderId == Guid.Empty)
            return;

        var cleanId = providerPaymentId.Trim();
        var effectiveSlug = string.IsNullOrWhiteSpace(storeSlug)
            ? "__root__"
            : NormalizeStoreSlug(storeSlug);

        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            index.ProviderPayments[cleanId] = new TenantProviderPaymentLookup(
                effectiveSlug,
                orderId,
                cleanId);

            // Mantém índice enxuto caso cresça muito (preserva últimos 5000 pagamentos)
            if (index.ProviderPayments.Count > 5000)
            {
                var overflow = index.ProviderPayments.Keys.Take(index.ProviderPayments.Count - 5000).ToList();
                foreach (var oldKey in overflow)
                    index.ProviderPayments.Remove(oldKey);
            }

            SaveIndexUnsafe(index);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<TenantProviderPaymentLookup?> FindByProviderPaymentIdAsync(
        string providerPaymentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentId))
            return null;

        var cleanId = providerPaymentId.Trim();
        await _indexLock.WaitAsync(ct);
        try
        {
            var index = LoadIndexUnsafe();
            return index.ProviderPayments.TryGetValue(cleanId, out var lookup) ? lookup : null;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private ClothingStoreRepository GetOrCreateTenantRepo(string normalizedSlug)
    {
        return _tenantRepos.GetOrAdd(normalizedSlug, slug =>
        {
            if (!Directory.Exists(_tenantsDirectory))
                Directory.CreateDirectory(_tenantsDirectory);

            return new ClothingStoreRepository(
                _passwordHasher,
                GetTenantDbPath(slug),
                enablePlatformOwnerCompanion: false);
        });
    }

    private string GetTenantDbPath(string normalizedSlug) =>
        Path.Combine(_tenantsDirectory, $"{normalizedSlug}.json");

    private TenantDirectoryState LoadIndexUnsafe()
    {
        try
        {
            if (!Directory.Exists(_rootDirectory))
                Directory.CreateDirectory(_rootDirectory);

            if (File.Exists(_indexFilePath))
            {
                var json = File.ReadAllText(_indexFilePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<TenantDirectoryState>(json, JsonOptions);
                if (parsed is not null)
                {
                    parsed.Tenants ??= new(StringComparer.OrdinalIgnoreCase);
                    parsed.ProviderPayments ??= new(StringComparer.OrdinalIgnoreCase);
                    return parsed;
                }
            }
        }
        catch
        {
        }

        return new TenantDirectoryState();
    }

    private void SaveIndexUnsafe(TenantDirectoryState state)
    {
        if (!Directory.Exists(_rootDirectory))
            Directory.CreateDirectory(_rootDirectory);

        var tempPath = $"{_indexFilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
        File.Move(tempPath, _indexFilePath, overwrite: true);
    }

    private static string GenerateSecretToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VerifyTokenHash(string? candidateToken, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(candidateToken) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var candidateBytes = Encoding.UTF8.GetBytes(HashToken(candidateToken));
        var storedBytes = Encoding.UTF8.GetBytes(storedHash.Trim().ToLowerInvariant());
        return candidateBytes.Length == storedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(candidateBytes, storedBytes);
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private sealed class TenantDirectoryState
    {
        public Dictionary<string, TenantSecretCredentials> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TenantProviderPaymentLookup> ProviderPayments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
