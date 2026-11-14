namespace ClothingStore.Application.Interfaces;

public sealed record MercadoPagoAppConfig(
    string? ClientId,
    string? ClientSecret,
    string? WebhookSecret)
{
    public bool IsOAuthConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed record MercadoPagoOAuthTokenResult(
    bool Success,
    string? AccessToken,
    string? RefreshToken,
    int ExpiresInSeconds,
    string? UserId,
    string? AccountLabel,
    string? ErrorMessage);

public sealed record MercadoPagoPixPaymentResult(
    bool Success,
    string ProviderPaymentId,
    string PixQrCode,
    string? PixQrCodeBase64,
    string Status,
    string? StatusDetail,
    string? ErrorMessage);

public sealed record MercadoPagoPaymentLookupResult(
    string ProviderPaymentId,
    string Status,
    string? StatusDetail,
    decimal TransactionAmount,
    string? ExternalReference,
    DateTimeOffset? DateApproved)
{
    public bool IsApproved =>
        string.Equals(Status, "approved", StringComparison.OrdinalIgnoreCase);
}

public sealed record MercadoPagoCheckoutPreferenceResult(
    bool Success,
    string PreferenceId,
    string InitPoint,
    string? SandboxInitPoint,
    string? ErrorMessage);

public sealed record MercadoPagoSubscriptionResult(
    bool Success,
    string SubscriptionId,
    string InitPoint,
    string Status,
    string? ErrorMessage);

public sealed record MercadoPagoPreapprovalLookupResult(
    string PreapprovalId,
    string Status,
    string? ExternalReference,
    string? PayerEmail,
    decimal? TransactionAmount,
    DateTimeOffset? NextPaymentDate)
{
    public bool IsAuthorized =>
        string.Equals(Status, "authorized", StringComparison.OrdinalIgnoreCase);
}

public sealed record MercadoPagoAuthorizedPaymentLookupResult(
    string AuthorizedPaymentId,
    string? PreapprovalId,
    string? PaymentId,
    string Status,
    string? ExternalReference,
    decimal? TransactionAmount)
{
    public bool IsApproved =>
        string.Equals(Status, "processed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "authorized", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "approved", StringComparison.OrdinalIgnoreCase);
}

public sealed record PlatformMercadoPagoConnectionStatus(
    bool IsAppConfigured,
    bool IsConnected,
    string? AccountLabel,
    bool UsingEnvironmentToken);

/// <summary>
/// Armazena de forma criptografada (AES-GCM) ou via variável de ambiente (`MP_PLATFORM_ACCESS_TOKEN`)
/// a conta Mercado Pago do dono da plataforma que recebe assinaturas e compras de planos do YouLink.
/// </summary>
public interface IPlatformMercadoPagoTokenStore
{
    Task<string?> GetValidAccessTokenAsync(IMercadoPagoPixService? refreshService = null, CancellationToken ct = default);

    Task<PlatformMercadoPagoConnectionStatus> GetConnectionStatusAsync(bool isAppConfigured, CancellationToken ct = default);

    Task SaveOAuthTokensAsync(
        string accessToken,
        string? refreshToken,
        int expiresInSeconds,
        string? userId,
        string? accountLabel,
        CancellationToken ct = default);

    Task ClearOAuthTokensAsync(CancellationToken ct = default);
}

/// <summary>
/// Cliente oficial de integração Mercado Pago (OAuth + Pix Dinâmico + Checkout Pro + Assinaturas + Webhook).
/// Usado exclusivamente pelo servidor/API — nenhum secret fica no navegador ou aplicativo do comprador.
/// </summary>
public interface IMercadoPagoPixService
{
    MercadoPagoAppConfig GetConfiguredApp();

    string BuildAuthorizationUrl(string redirectUri, string state);

    Task<MercadoPagoOAuthTokenResult> ExchangeCodeForTokensAsync(
        string code,
        string redirectUri,
        CancellationToken ct = default);

    Task<MercadoPagoOAuthTokenResult> RefreshAccessTokenAsync(
        string refreshToken,
        CancellationToken ct = default);

    Task<MercadoPagoPixPaymentResult> CreatePixPaymentAsync(
        string accessToken,
        decimal transactionAmount,
        string description,
        string orderNumber,
        string? customerName,
        string? payerEmail,
        string? notificationUrl,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<MercadoPagoPaymentLookupResult?> GetPaymentStatusAsync(
        string accessToken,
        string paymentId,
        CancellationToken ct = default);

    Task<MercadoPagoPaymentLookupResult?> SearchLatestPaymentByExternalReferenceAsync(
        string accessToken,
        string externalReference,
        CancellationToken ct = default);

    Task<MercadoPagoCheckoutPreferenceResult> CreateCheckoutPreferenceAsync(
        string accessToken,
        decimal transactionAmount,
        string title,
        string orderNumber,
        string? payerName,
        string? payerEmail,
        string backUrlBase,
        string? notificationUrl,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<MercadoPagoSubscriptionResult> CreatePreapprovalSubscriptionAsync(
        string accessToken,
        decimal recurringAmount,
        int frequencyMonths,
        string reason,
        string orderNumber,
        string payerEmail,
        string backUrl,
        string? notificationUrl,
        CancellationToken ct = default);

    Task<MercadoPagoPreapprovalLookupResult?> GetPreapprovalStatusAsync(
        string accessToken,
        string preapprovalId,
        CancellationToken ct = default);

    Task<MercadoPagoAuthorizedPaymentLookupResult?> GetAuthorizedPaymentAsync(
        string accessToken,
        string authorizedPaymentId,
        CancellationToken ct = default);

    bool VerifyWebhookSignature(
        string? webhookSecretOverride,
        string? xSignatureHeader,
        string? xRequestIdHeader,
        string? dataIdQueryParam);
}
