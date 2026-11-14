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

/// <summary>
/// Cliente oficial de integração Mercado Pago (OAuth + Pix Dinâmico + Verificação de Pagamento/Webhook).
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

    bool VerifyWebhookSignature(
        string? webhookSecretOverride,
        string? xSignatureHeader,
        string? xRequestIdHeader,
        string? dataIdQueryParam);
}
