namespace ClothingStore.Application.Interfaces;

public sealed class TenantSecretCredentials
{
    public string StoreSlug { get; set; } = string.Empty;
    public string? PublishTokenHash { get; set; }
    public string? OwnerEmail { get; set; }
    public string? MercadoPagoAccessToken { get; set; }
    public string? MercadoPagoRefreshToken { get; set; }
    public DateTimeOffset? MercadoPagoTokenExpiresAt { get; set; }
    public string? MercadoPagoUserId { get; set; }
    public string? MercadoPagoAccountLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record TenantPublishAuthorizationResult(
    bool Authorized,
    bool IsSlugConflict,
    string NormalizedStoreSlug,
    IClothingStoreRepository? Repository,
    string? IssuedPublishToken,
    string? ErrorMessage);

public sealed record TenantProviderPaymentLookup(
    string StoreSlug,
    Guid OrderId,
    string ProviderPaymentId);

/// <summary>
/// Resolve repositórios isolados por loja (`storeSlug`) no servidor central multi-tenant,
/// mantendo compatibilidade total com implantações single-tenant no repositório raiz.
/// Armazena credenciais sensíveis (tokens OAuth do Mercado Pago e hash do token de publicação)
/// separadas dos bancos públicos/sync das lojas.
/// </summary>
public interface IMultiTenantStoreResolver
{
    string RootStorageDirectory { get; }
    IClothingStoreRepository DefaultRepository { get; }

    string NormalizeStoreSlug(string? rawSlugOrName);

    Task<IClothingStoreRepository?> ResolveReadRepositoryAsync(
        string? storeSlug,
        CancellationToken ct = default);

    Task<TenantPublishAuthorizationResult> AuthorizeAndResolvePublishRepositoryAsync(
        string? requestedSlug,
        string? syncKeyHeader,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        CancellationToken ct = default);

    Task<bool> AuthorizeTenantOrdersPullAsync(
        string? storeSlug,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListTenantSlugsAsync(CancellationToken ct = default);

    Task<TenantSecretCredentials?> GetTenantCredentialsAsync(
        string? storeSlug,
        CancellationToken ct = default);

    Task SaveMercadoPagoTokensAsync(
        string? storeSlug,
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAt,
        string? userId,
        string? accountLabel,
        CancellationToken ct = default);

    Task ClearMercadoPagoTokensAsync(
        string? storeSlug,
        CancellationToken ct = default);

    Task RegisterProviderPaymentIndexAsync(
        string providerPaymentId,
        string? storeSlug,
        Guid orderId,
        CancellationToken ct = default);

    Task<TenantProviderPaymentLookup?> FindByProviderPaymentIdAsync(
        string providerPaymentId,
        CancellationToken ct = default);
}
