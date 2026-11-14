using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Application.Services;

/// <summary>
/// Orquestrador compartilhado entre ClothingStore.Web e ClothingStore.Api para:
/// 1. Publicação multi-tenant em 1 clique sem exigir YOULINK_SYNC_SECRET de lojistas;
/// 2. Fluxo OAuth do Mercado Pago, geração de Pix dinâmico e confirmação automática via webhook/polling;
/// 3. Sincronização de pedidos da vitrine de volta para o aplicativo da loja.
/// </summary>
public sealed class MultiTenantGatewayService
{
    private readonly IMultiTenantStoreResolver _storeResolver;
    private readonly IMercadoPagoPixService _mercadoPagoPixService;
    private readonly HttpClient _httpClient;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IImageStorageService _imageStorage;
    private readonly IEmailService? _emailService;
    private readonly ConcurrentDictionary<string, PendingOAuthState> _pendingOAuthStates = new(StringComparer.Ordinal);

    public MultiTenantGatewayService(
        IMultiTenantStoreResolver storeResolver,
        IMercadoPagoPixService mercadoPagoPixService,
        HttpClient httpClient,
        IPasswordHasher passwordHasher,
        IImageStorageService imageStorage,
        IEmailService? emailService = null)
    {
        _storeResolver = storeResolver;
        _mercadoPagoPixService = mercadoPagoPixService;
        _httpClient = httpClient;
        _passwordHasher = passwordHasher;
        _imageStorage = imageStorage;
        _emailService = emailService;
    }

    public IMultiTenantStoreResolver StoreResolver => _storeResolver;
    public IMercadoPagoPixService MercadoPago => _mercadoPagoPixService;

    public CatalogService CreateCatalogService(IClothingStoreRepository repository) =>
        new(repository);

    public EmployeeAndSettingsService CreateSettingsService(IClothingStoreRepository repository) =>
        new(repository, _passwordHasher, _imageStorage, _emailService);

    public OrderAndShippingService CreateOrderService(IClothingStoreRepository repository) =>
        new(repository, _httpClient, _mercadoPagoPixService);

    public CloudSyncStateService CreateSyncService(IClothingStoreRepository repository) =>
        new(repository, CreateSettingsService(repository));

    public async Task<IClothingStoreRepository?> ResolveReadRepositoryAsync(
        string? storeSlug,
        CancellationToken ct = default) =>
        await _storeResolver.ResolveReadRepositoryAsync(storeSlug, ct);

    public async Task<CloudStorePublishResponse> PublishStoreAsync(
        CloudCatalogSyncPayload payload,
        string? requestedSlug,
        string? syncKeyHeader,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        string publicServerOriginUrl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var slugCandidate = !string.IsNullOrWhiteSpace(requestedSlug)
            ? requestedSlug
            : !string.IsNullOrWhiteSpace(payload.Settings.StoreSlug)
                ? payload.Settings.StoreSlug
                : payload.Settings.StoreName;

        var auth = await _storeResolver.AuthorizeAndResolvePublishRepositoryAsync(
            slugCandidate,
            syncKeyHeader,
            publishTokenHeader,
            hasValidSyncKey,
            authenticatedOwnerEmail,
            ct);

        if (!auth.Authorized || auth.Repository is null)
        {
            return new CloudStorePublishResponse(
                Success: false,
                StoreSlug: auth.NormalizedStoreSlug,
                PublicStoreUrl: string.Empty,
                PublishToken: null,
                CategoriesCount: 0,
                ProductsCount: 0,
                SyncedAt: DateTimeOffset.UtcNow,
                Message: auth.ErrorMessage ?? "Não autorizado a publicar nesta loja.");
        }

        if (string.IsNullOrWhiteSpace(payload.Settings.StoreSlug))
        {
            payload.Settings.StoreSlug = auth.NormalizedStoreSlug;
        }

        var isMultiTenantCentral =
            !hasValidSyncKey ||
            string.Equals(payload.Settings.ServerHostingMode, "PlatformShared", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(publicServerOriginUrl) &&
            (string.IsNullOrWhiteSpace(payload.Settings.OnlineBaseUrl) ||
             (isMultiTenantCentral &&
              string.Equals(payload.Settings.OnlineBaseUrl.TrimEnd('/'), publicServerOriginUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))))
        {
            payload.Settings.OnlineBaseUrl = isMultiTenantCentral
                ? WhatsAppLinkBuilder.BuildCentralTenantCatalogUrl(publicServerOriginUrl, auth.NormalizedStoreSlug)
                : publicServerOriginUrl.TrimEnd('/');
        }

        var syncService = CreateSyncService(auth.Repository);
        var result = await syncService.ApplySyncPayloadAsync(payload, ct);

        var effectiveBase = !string.IsNullOrWhiteSpace(publicServerOriginUrl)
            ? publicServerOriginUrl.TrimEnd('/')
            : result.AppliedSettings.OnlineBaseUrl ?? string.Empty;

        var publicStoreUrl = string.IsNullOrWhiteSpace(effectiveBase)
            ? result.EffectiveCatalogUrl
            : isMultiTenantCentral
                ? WhatsAppLinkBuilder.BuildCentralTenantCatalogUrl(effectiveBase, auth.NormalizedStoreSlug)
                : WhatsAppLinkBuilder.BuildStoreCatalogUrl(effectiveBase, auth.NormalizedStoreSlug);

        return new CloudStorePublishResponse(
            Success: true,
            StoreSlug: auth.NormalizedStoreSlug,
            PublicStoreUrl: publicStoreUrl,
            PublishToken: auth.IssuedPublishToken,
            CategoriesCount: result.CategoriesCount,
            ProductsCount: result.ProductsCount,
            SyncedAt: DateTimeOffset.UtcNow,
            Message: "Loja publicada e sincronizada com sucesso!",
            IsSiteActive: result.IsSiteActive);
    }

    public async Task<StoreOrderDto> PlacePixOrderForStoreAsync(
        string? storeSlug,
        CreatePixOrderRequest request,
        string? publicOriginUrl,
        CancellationToken ct = default)
    {
        var repository = await _storeResolver.ResolveReadRepositoryAsync(storeSlug, ct)
            ?? throw new DomainException("Loja não encontrada neste servidor.");

        var settings = await repository.GetStoreSettingsAsync(ct);
        var effectiveSlug = _storeResolver.NormalizeStoreSlug(
            string.IsNullOrWhiteSpace(storeSlug) ? settings.StoreSlug : storeSlug);

        MercadoPagoOrderCheckoutContext? mpContext = null;
        if (settings.IsAutomaticPixCheckoutReady())
        {
            var accessToken = await EnsureValidMercadoPagoAccessTokenAsync(effectiveSlug, ct);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                string? notificationUrl = null;
                if (!string.IsNullOrWhiteSpace(publicOriginUrl) &&
                    publicOriginUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    notificationUrl = $"{publicOriginUrl.TrimEnd('/')}/api/public/webhooks/mercadopago/{Uri.EscapeDataString(effectiveSlug)}";
                }

                mpContext = new MercadoPagoOrderCheckoutContext(
                    accessToken,
                    notificationUrl,
                    (paymentId, orderId, innerCt) =>
                        _storeResolver.RegisterProviderPaymentIndexAsync(paymentId, effectiveSlug, orderId, innerCt));
            }
        }

        var orderService = CreateOrderService(repository);
        return await orderService.PlacePixOrderAsync(request, mpContext, ct);
    }

    public async Task<PublicOrderStatusDto?> GetPublicOrderStatusAsync(
        string? storeSlug,
        Guid orderId,
        string? statusToken,
        CancellationToken ct = default)
    {
        var repository = await _storeResolver.ResolveReadRepositoryAsync(storeSlug, ct);
        if (repository is null)
            return null;

        var settings = await repository.GetStoreSettingsAsync(ct);
        var effectiveSlug = _storeResolver.NormalizeStoreSlug(
            string.IsNullOrWhiteSpace(storeSlug) ? settings.StoreSlug : storeSlug);

        var accessToken = await EnsureValidMercadoPagoAccessTokenAsync(effectiveSlug, ct);
        var mpCheck = string.IsNullOrWhiteSpace(accessToken)
            ? null
            : new MercadoPagoOrderStatusCheckContext(accessToken);

        return await CreateOrderService(repository)
            .GetPublicOrderStatusAsync(orderId, statusToken, mpCheck, ct);
    }

    public async Task<CloudSyncOrdersResponse?> GetStoreOrdersForSyncAsync(
        string? storeSlug,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        CancellationToken ct = default)
    {
        var effectiveSlug = string.IsNullOrWhiteSpace(storeSlug) ? null : _storeResolver.NormalizeStoreSlug(storeSlug);
        var authorized = await _storeResolver.AuthorizeTenantOrdersPullAsync(
            effectiveSlug,
            publishTokenHeader,
            hasValidSyncKey,
            authenticatedOwnerEmail,
            ct);

        if (!authorized)
            return null;

        var repository = await _storeResolver.ResolveReadRepositoryAsync(effectiveSlug, ct);
        if (repository is null)
            return null;

        var orders = await CreateOrderService(repository).GetOrdersAsync(ct);
        return new CloudSyncOrdersResponse(
            effectiveSlug ?? "default",
            orders.Count,
            orders);
    }

    public async Task<(MercadoPagoOAuthStartResponse Response, string? IssuedPublishToken)> StartMercadoPagoOAuthAsync(
        string? storeSlug,
        string? syncKeyHeader,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        string callbackRedirectUri,
        CancellationToken ct = default)
    {
        var appConfig = _mercadoPagoPixService.GetConfiguredApp();
        if (!appConfig.IsOAuthConfigured)
        {
            return (new MercadoPagoOAuthStartResponse(
                IsConfigured: false,
                AuthorizationUrl: null,
                StateToken: null,
                Message: "O servidor central ainda não possui MP_CLIENT_ID e MP_CLIENT_SECRET configurados pelo administrador da plataforma."), null);
        }

        var auth = await _storeResolver.AuthorizeAndResolvePublishRepositoryAsync(
            storeSlug,
            syncKeyHeader,
            publishTokenHeader,
            hasValidSyncKey,
            authenticatedOwnerEmail,
            ct);

        if (!auth.Authorized)
        {
            throw new DomainException(auth.ErrorMessage ?? "Não autorizado a conectar Mercado Pago para esta loja.");
        }

        CleanupExpiredOAuthStates();
        var stateToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _pendingOAuthStates[stateToken] = new PendingOAuthState(
            auth.NormalizedStoreSlug,
            callbackRedirectUri,
            DateTimeOffset.UtcNow.AddMinutes(15));

        var authUrl = _mercadoPagoPixService.BuildAuthorizationUrl(callbackRedirectUri, stateToken);
        return (new MercadoPagoOAuthStartResponse(
            IsConfigured: true,
            AuthorizationUrl: authUrl,
            StateToken: stateToken,
            Message: "Abra o link abaixo no navegador para autorizar sua conta Mercado Pago."), auth.IssuedPublishToken);
    }

    public async Task<MercadoPagoOAuthCallbackResult> CompleteMercadoPagoOAuthCallbackAsync(
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new MercadoPagoOAuthCallbackResult(
                false,
                string.Empty,
                string.Empty,
                $"Autorização recusada ou cancelada no Mercado Pago: {errorDescription ?? error}");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return new MercadoPagoOAuthCallbackResult(
                false,
                string.Empty,
                string.Empty,
                "Parâmetros de autorização (code/state) ausentes.");
        }

        if (!_pendingOAuthStates.TryRemove(state.Trim(), out var pending) ||
            pending.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return new MercadoPagoOAuthCallbackResult(
                false,
                string.Empty,
                string.Empty,
                "Solicitação expirada ou inválida. Volte ao aplicativo YouLink e clique em Conectar Mercado Pago novamente.");
        }

        var tokenResult = await _mercadoPagoPixService.ExchangeCodeForTokensAsync(
            code,
            pending.RedirectUri,
            ct);

        if (!tokenResult.Success || string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            return new MercadoPagoOAuthCallbackResult(
                false,
                pending.StoreSlug,
                string.Empty,
                tokenResult.ErrorMessage ?? "Não foi possível obter token do Mercado Pago.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(300, tokenResult.ExpiresInSeconds));
        await _storeResolver.SaveMercadoPagoTokensAsync(
            pending.StoreSlug,
            tokenResult.AccessToken,
            tokenResult.RefreshToken,
            expiresAt,
            tokenResult.UserId,
            tokenResult.AccountLabel,
            ct);

        var repository = await _storeResolver.ResolveReadRepositoryAsync(pending.StoreSlug, ct);
        if (repository is not null)
        {
            var settings = await repository.GetStoreSettingsAsync(ct);
            settings.ConfigureMercadoPagoConnection(
                connected: true,
                userId: tokenResult.UserId,
                accountLabel: tokenResult.AccountLabel,
                connectedAt: DateTimeOffset.UtcNow,
                gatewayMode: StoreSettings.PixGatewayMercadoPago);

            if (settings.CanUseDirectPixSales() &&
                !string.IsNullOrWhiteSpace(settings.OriginCep))
            {
                settings.DirectPixSalesEnabled = true;
            }

            await repository.UpdateStoreSettingsAsync(settings, ct);
        }

        return new MercadoPagoOAuthCallbackResult(
            true,
            pending.StoreSlug,
            tokenResult.AccountLabel ?? "Conta Mercado Pago",
            "Mercado Pago conectado com sucesso! Suas vendas Pix na vitrine agora têm confirmação automática.");
    }

    public async Task<MercadoPagoStatusDto> GetMercadoPagoStatusAsync(
        string? storeSlug,
        CancellationToken ct = default)
    {
        var isAppConfigured = _mercadoPagoPixService.GetConfiguredApp().IsOAuthConfigured;
        var repository = await _storeResolver.ResolveReadRepositoryAsync(storeSlug, ct);
        var settings = repository is not null ? await repository.GetStoreSettingsAsync(ct) : null;
        var creds = await _storeResolver.GetTenantCredentialsAsync(storeSlug, ct);

        var isConnected = (settings?.HasMercadoPagoConnected == true) ||
                          !string.IsNullOrWhiteSpace(creds?.MercadoPagoAccessToken);

        var label = settings?.MercadoPagoAccountLabel ?? creds?.MercadoPagoAccountLabel;
        var userId = settings?.MercadoPagoUserId ?? creds?.MercadoPagoUserId;
        var mode = settings?.PixGatewayMode ?? StoreSettings.PixGatewayDirect;

        var msg = !isAppConfigured
            ? "O aplicativo Mercado Pago OAuth ainda não foi configurado no servidor."
            : isConnected
                ? $"Conta conectada: {label ?? "Mercado Pago"}"
                : "Nenhuma conta Mercado Pago conectada ainda.";

        return new MercadoPagoStatusDto(
            isAppConfigured,
            isConnected,
            mode,
            label,
            userId,
            settings?.MercadoPagoConnectedAt,
            msg);
    }

    public async Task<bool> DisconnectMercadoPagoAsync(
        string? storeSlug,
        string? publishTokenHeader,
        bool hasValidSyncKey,
        string? authenticatedOwnerEmail,
        CancellationToken ct = default)
    {
        var effectiveSlug = string.IsNullOrWhiteSpace(storeSlug) ? null : _storeResolver.NormalizeStoreSlug(storeSlug);
        var authorized = await _storeResolver.AuthorizeTenantOrdersPullAsync(
            effectiveSlug,
            publishTokenHeader,
            hasValidSyncKey,
            authenticatedOwnerEmail,
            ct);

        if (!authorized)
            return false;

        await _storeResolver.ClearMercadoPagoTokensAsync(effectiveSlug, ct);

        var repository = await _storeResolver.ResolveReadRepositoryAsync(effectiveSlug, ct);
        if (repository is not null)
        {
            var settings = await repository.GetStoreSettingsAsync(ct);
            settings.ConfigureMercadoPagoConnection(
                connected: false,
                userId: null,
                accountLabel: null,
                connectedAt: null,
                gatewayMode: StoreSettings.PixGatewayDirect);
            await repository.UpdateStoreSettingsAsync(settings, ct);
        }

        return true;
    }

    public async Task<WebhookProcessResult> ProcessMercadoPagoWebhookAsync(
        string? storeSlugHint,
        string? queryDataId,
        string? queryTopic,
        string? xSignature,
        string? xRequestId,
        string? rawBodyJson,
        CancellationToken ct = default)
    {
        var (extractedId, extractedTopic) = ExtractPaymentNotification(queryDataId, queryTopic, rawBodyJson);

        if (string.IsNullOrWhiteSpace(extractedId))
        {
            return new WebhookProcessResult(true, false, false, "Notificação sem data.id ignorada.");
        }

        if (!string.IsNullOrWhiteSpace(extractedTopic) &&
            !extractedTopic.Contains("payment", StringComparison.OrdinalIgnoreCase))
        {
            return new WebhookProcessResult(true, false, false, $"Tópico '{extractedTopic}' ignorado.");
        }

        if (!_mercadoPagoPixService.VerifyWebhookSignature(null, xSignature, xRequestId, extractedId))
        {
            return new WebhookProcessResult(false, true, false, "Assinatura x-signature inválida.");
        }

        var lookup = await _storeResolver.FindByProviderPaymentIdAsync(extractedId, ct);
        var targetSlug = lookup?.StoreSlug ?? (string.IsNullOrWhiteSpace(storeSlugHint) ? null : _storeResolver.NormalizeStoreSlug(storeSlugHint));

        var repository = await _storeResolver.ResolveReadRepositoryAsync(
            targetSlug == "__root__" ? null : targetSlug,
            ct);
        if (repository is null)
        {
            return new WebhookProcessResult(true, false, false, "Loja do pagamento não localizada neste servidor.");
        }

        var accessToken = await EnsureValidMercadoPagoAccessTokenAsync(targetSlug, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new WebhookProcessResult(true, false, false, "Token Mercado Pago da loja não encontrado.");
        }

        var mpPayment = await _mercadoPagoPixService.GetPaymentStatusAsync(accessToken, extractedId, ct);
        if (mpPayment is null || !mpPayment.IsApproved)
        {
            return new WebhookProcessResult(true, false, false, $"Pagamento ainda com status '{mpPayment?.Status ?? "desconhecido"}'.");
        }

        var targetOrderId = lookup?.OrderId ?? Guid.Empty;
        if (targetOrderId == Guid.Empty)
        {
            var orders = await repository.GetOrdersAsync(ct);
            var matched = orders.FirstOrDefault(o =>
                string.Equals(o.ProviderPaymentId, extractedId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(mpPayment.ExternalReference) &&
                 string.Equals(o.OrderNumber, mpPayment.ExternalReference, StringComparison.OrdinalIgnoreCase)));
            if (matched is not null)
                targetOrderId = matched.Id;
        }

        if (targetOrderId == Guid.Empty)
        {
            return new WebhookProcessResult(true, false, false, "Pedido correspondente não localizado.");
        }

        var confirmed = await CreateOrderService(repository)
            .ConfirmAutomaticPixOrderAsync(targetOrderId, extractedId, ct);

        return new WebhookProcessResult(
            true,
            false,
            confirmed is not null && confirmed.Status != StoreOrder.StatusAwaitingPix,
            $"Pagamento {extractedId} processado ({confirmed?.Status ?? "ok"}).");
    }

    public async Task<string?> EnsureValidMercadoPagoAccessTokenAsync(
        string? storeSlug,
        CancellationToken ct = default)
    {
        var creds = await _storeResolver.GetTenantCredentialsAsync(storeSlug, ct);
        if (creds is null || string.IsNullOrWhiteSpace(creds.MercadoPagoAccessToken))
            return null;

        if (creds.MercadoPagoTokenExpiresAt.HasValue &&
            creds.MercadoPagoTokenExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(5) &&
            !string.IsNullOrWhiteSpace(creds.MercadoPagoRefreshToken))
        {
            var refreshed = await _mercadoPagoPixService.RefreshAccessTokenAsync(creds.MercadoPagoRefreshToken, ct);
            if (refreshed.Success && !string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                await _storeResolver.SaveMercadoPagoTokensAsync(
                    storeSlug,
                    refreshed.AccessToken,
                    refreshed.RefreshToken ?? creds.MercadoPagoRefreshToken,
                    DateTimeOffset.UtcNow.AddSeconds(Math.Max(300, refreshed.ExpiresInSeconds)),
                    refreshed.UserId ?? creds.MercadoPagoUserId,
                    refreshed.AccountLabel ?? creds.MercadoPagoAccountLabel,
                    ct);

                return refreshed.AccessToken;
            }
        }

        return creds.MercadoPagoAccessToken;
    }

    private void CleanupExpiredOAuthStates()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _pendingOAuthStates)
        {
            if (pair.Value.ExpiresAt < now)
                _pendingOAuthStates.TryRemove(pair.Key, out _);
        }
    }

    private static (string? PaymentId, string? Topic) ExtractPaymentNotification(
        string? queryDataId,
        string? queryTopic,
        string? rawBodyJson)
    {
        var id = string.IsNullOrWhiteSpace(queryDataId) ? null : queryDataId.Trim();
        var topic = string.IsNullOrWhiteSpace(queryTopic) ? null : queryTopic.Trim();

        if (!string.IsNullOrWhiteSpace(rawBodyJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawBodyJson);
                var root = doc.RootElement;

                if (string.IsNullOrWhiteSpace(topic))
                {
                    if (root.TryGetProperty("type", out var typeProp))
                        topic = typeProp.GetString();
                    else if (root.TryGetProperty("topic", out var topicProp))
                        topic = topicProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    if (root.TryGetProperty("data", out var dataProp) &&
                        dataProp.ValueKind == JsonValueKind.Object &&
                        dataProp.TryGetProperty("id", out var nestedId))
                    {
                        id = nestedId.ToString();
                    }
                    else if (root.TryGetProperty("id", out var rootId) &&
                             (topic is null || topic.Contains("payment", StringComparison.OrdinalIgnoreCase)) &&
                             root.TryGetProperty("action", out _))
                    {
                        id = rootId.ToString();
                    }
                }
            }
            catch
            {
            }
        }

        return (id, topic);
    }

    private sealed record PendingOAuthState(
        string StoreSlug,
        string RedirectUri,
        DateTimeOffset ExpiresAt);
}

public sealed record MercadoPagoOAuthCallbackResult(
    bool Success,
    string StoreSlug,
    string AccountLabel,
    string Message);

public sealed record WebhookProcessResult(
    bool SignatureValid,
    bool IsUnauthorized,
    bool OrderConfirmed,
    string Message);
