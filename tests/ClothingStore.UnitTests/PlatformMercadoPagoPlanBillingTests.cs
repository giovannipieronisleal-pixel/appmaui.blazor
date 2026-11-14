using System.Net;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public sealed class PlatformMercadoPagoPlanBillingTests
{
    private sealed class MockPlatformMercadoPagoHandler : HttpMessageHandler
    {
        public string AccessTokenToIssue { get; set; } = "APP_USR-PLATFORM-TOKEN-1";
        public string RefreshTokenToIssue { get; set; } = "TG-PLATFORM-REFRESH-1";
        public string RefreshedAccessToken { get; set; } = "APP_USR-PLATFORM-REFRESHED-2";
        public int OAuthExpiresInSeconds { get; set; } = 21600;

        public string NextPaymentId { get; set; } = "88112233";
        public string PaymentStatus { get; set; } = "pending";
        public decimal PaymentTransactionAmount { get; set; } = 59.90m;
        public string? LastPaymentExternalReference { get; private set; }
        public string? LastPaymentNotificationUrl { get; private set; }

        public string? LastPreferenceBody { get; private set; }
        public string? LastPreapprovalBody { get; private set; }
        public string PreapprovalStatus { get; set; } = "pending";
        public decimal PreapprovalTransactionAmount { get; set; } = 59.90m;
        public string AuthorizedPaymentStatus { get; set; } = "approved";

        public bool IncludeSearchResultPayment { get; set; }
        public decimal SearchPaymentAmount { get; set; } = 179.70m;
        public string SearchPaymentStatus { get; set; } = "approved";

        public int CreatePixCalls { get; private set; }
        public int CreatePreferenceCalls { get; private set; }
        public int CreatePreapprovalCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (request.Method == HttpMethod.Post && url.Contains("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                if (body.Contains("refresh_token", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshCalls++;
                    return JsonOk(new
                    {
                        access_token = RefreshedAccessToken,
                        refresh_token = "TG-PLATFORM-REFRESH-2",
                        expires_in = 21600,
                        user_id = 777555L
                    });
                }

                return JsonOk(new
                {
                    access_token = AccessTokenToIssue,
                    refresh_token = RefreshTokenToIssue,
                    expires_in = OAuthExpiresInSeconds,
                    user_id = 777555L
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = 777555L,
                    nickname = "YOULINK_OFICIAL",
                    email = "dono@youlink.com.br"
                });
            }

            if (request.Method == HttpMethod.Post &&
                url.Contains("/v1/payments", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("/search", StringComparison.OrdinalIgnoreCase))
            {
                CreatePixCalls++;
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("external_reference", out var ext))
                    LastPaymentExternalReference = ext.GetString();
                if (doc.RootElement.TryGetProperty("notification_url", out var notif))
                    LastPaymentNotificationUrl = notif.GetString();

                return JsonOk(new
                {
                    id = long.Parse(NextPaymentId),
                    status = "pending",
                    status_detail = "pending_waiting_transfer",
                    point_of_interaction = new
                    {
                        transaction_data = new
                        {
                            qr_code = "00020101021226820014br.gov.bcb.pix2560qr.mercadopago.com/plan1236304ABCD",
                            qr_code_base64 = "aVZCT1J3MEtHZ29BQUFBTlNVaEVVZ0FBQUFFQUFBQUNBUUFBQUFDeA=="
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/v1/payments/search", StringComparison.OrdinalIgnoreCase))
            {
                SearchCalls++;
                if (!IncludeSearchResultPayment)
                {
                    return JsonOk(new { results = Array.Empty<object>() });
                }

                return JsonOk(new
                {
                    results = new[]
                    {
                        new
                        {
                            id = 99001122L,
                            status = SearchPaymentStatus,
                            status_detail = "accredited",
                            transaction_amount = SearchPaymentAmount,
                            external_reference = LastPaymentExternalReference,
                            date_approved = DateTimeOffset.UtcNow
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/v1/payments/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = long.Parse(NextPaymentId),
                    status = PaymentStatus,
                    status_detail = PaymentStatus == "approved" ? "accredited" : "pending_waiting_transfer",
                    transaction_amount = PaymentTransactionAmount,
                    external_reference = LastPaymentExternalReference,
                    date_approved = PaymentStatus == "approved" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null
                });
            }

            if (request.Method == HttpMethod.Post && url.Contains("/checkout/preferences", StringComparison.OrdinalIgnoreCase))
            {
                CreatePreferenceCalls++;
                LastPreferenceBody = body;
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("external_reference", out var ext))
                    LastPaymentExternalReference = ext.GetString();

                return JsonOk(new
                {
                    id = "PREF-PLAN-9001",
                    init_point = "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=PREF-PLAN-9001",
                    sandbox_init_point = "https://sandbox.mercadopago.com.br/checkout/v1/redirect?pref_id=PREF-PLAN-9001"
                });
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/preapproval", StringComparison.OrdinalIgnoreCase))
            {
                CreatePreapprovalCalls++;
                LastPreapprovalBody = body;
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("external_reference", out var ext))
                    LastPaymentExternalReference = ext.GetString();

                return JsonOk(new
                {
                    id = "PREAPP-SUB-1001",
                    status = "pending",
                    init_point = "https://www.mercadopago.com.br/subscriptions/checkout?preapproval_id=PREAPP-SUB-1001"
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/preapproval/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = "PREAPP-SUB-1001",
                    status = PreapprovalStatus,
                    external_reference = LastPaymentExternalReference,
                    payer_email = "lojista@teste.com",
                    auto_recurring = new
                    {
                        transaction_amount = PreapprovalTransactionAmount,
                        currency_id = "BRL",
                        frequency = 1,
                        frequency_type = "months"
                    }
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/authorized_payments/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new
                {
                    id = 44556677L,
                    preapproval_id = "PREAPP-SUB-1001",
                    external_reference = LastPaymentExternalReference,
                    status = AuthorizedPaymentStatus,
                    transaction_amount = PreapprovalTransactionAmount,
                    payment = new
                    {
                        id = 55667788L,
                        status = AuthorizedPaymentStatus
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonOk(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
    }

    private static (
        ClothingStoreRepository Repo,
        string TempDir,
        Pbkdf2PasswordHasher Hasher,
        IImageStorageService Storage,
        MockPlatformMercadoPagoHandler Handler,
        MercadoPagoPixService MpService,
        PlatformMercadoPagoTokenStore TokenStore,
        PlatformSubscriptionService Service) CreatePlatformBillingEnv(string? envToken = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_mp_plan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var hasher = new Pbkdf2PasswordHasher();
        var repo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "store.json"), enablePlatformOwnerCompanion: false);
        var handler = new MockPlatformMercadoPagoHandler();
        var http = new HttpClient(handler);
        var mpService = new MercadoPagoPixService(
            http,
            () => MercadoPagoPixService.ReadFromConfig("CLIENT_ID_123", "CLIENT_SECRET_XYZ", "WEBHOOK_SECRET_ABC"));
        var tokenStore = new PlatformMercadoPagoTokenStore(tempDir, () => envToken);
        var config = new ConfigurationBuilder().Build();
        var storage = new SupabaseImageStorageService(new HttpClient(), config, tempDir);
        var service = new PlatformSubscriptionService(repo, mpService, tokenStore);

        return (repo, tempDir, hasher, storage, handler, mpService, tokenStore, service);
    }

    private static async Task SeedOwnerPricingAsync(ClothingStoreRepository repo)
    {
        var settings = await repo.GetStoreSettingsAsync();
        settings.BasicPlanMonthlyPrice = 59.90m;
        settings.IntermediaryPlanMonthlyPrice = 99.90m;
        settings.PremiumPlanMonthlyPrice = 149.90m;
        settings.PicpayPixKey = "pix@youlink.com.br";
        await repo.UpdateStoreSettingsAsync(settings);
    }

    [TestMethod]
    public async Task PlatformPlanOrder_MercadoPagoPix_ShouldCreateQr_RejectTamperedAmount_AndAutoApproveViaWebhook()
    {
        var env = CreatePlatformBillingEnv(envToken: "APP_USR-PLATFORM-TOKEN-1");
        try
        {
            await SeedOwnerPricingAsync(env.Repo);

            // Tentativa adulterada abaixo do piso mínimo do plano Básico é corrigida pelo piso do servidor (nunca sai por R$ 1,00)
            var minQuote = GoogleAccountTierVerifier.CalculatePlanBenefits(
                59.90m,
                usesFreeDomain: true,
                GoogleAccountTierVerifier.TierWorkspace,
                usesDedicatedOwnServer: false);
            var clampedOrder = await env.Service.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierBasic,
                DurationMonths: 1,
                Amount: 1.00m,
                StoreName: "Loja Adulterada",
                BuyerEmail: "lojista@teste.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoPix),
                publicBaseOrigin: "https://youlink-loja.onrender.com");
            Assert.IsTrue(clampedOrder.Amount >= minQuote.FinalMonthlyPrice,
                $"Valor adulterado deve ser elevado ao piso oficial ({minQuote.FinalMonthlyPrice}), nunca 1,00.");

            env.Handler.NextPaymentId = "88112244";
            var order = await env.Service.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierBasic,
                DurationMonths: 1,
                Amount: 59.90m,
                StoreName: "Boutique Pix MP",
                BuyerEmail: "lojista@teste.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoPix),
                publicBaseOrigin: "https://youlink-loja.onrender.com");

            Assert.AreEqual(PlatformSubscriptionOrder.MethodMercadoPagoPix, order.PaymentMethod);
            Assert.AreEqual(PlatformSubscriptionOrder.StatusPending, order.Status);
            Assert.AreEqual("88112244", order.MercadoPagoPaymentId);
            Assert.IsTrue(order.PixBrCode.Contains("mercadopago", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(string.IsNullOrWhiteSpace(order.MercadoPagoQrCodeBase64));
            Assert.AreEqual(2, env.Handler.CreatePixCalls);
            Assert.AreEqual("https://youlink-loja.onrender.com/api/public/webhooks/mercadopago/platform", env.Handler.LastPaymentNotificationUrl);

            // Webhook com valor pago abaixo do pedido NÃO pode aprovar
            env.Handler.PaymentStatus = "approved";
            env.Handler.PaymentTransactionAmount = 1.00m;
            var lowAmountWebhook = await env.Service.HandleMercadoPagoWebhookAsync("payment", "88112244");
            Assert.IsFalse(lowAmountWebhook.OrderApproved);

            // Webhook legítimo com valor integral aprova e emite cartão automaticamente
            env.Handler.PaymentTransactionAmount = 59.90m;
            var okWebhook = await env.Service.HandleMercadoPagoWebhookAsync("payment", "88112244");
            Assert.IsTrue(okWebhook.Handled);
            Assert.IsTrue(okWebhook.OrderApproved);

            var status = await env.Service.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken);
            Assert.IsTrue(status.IsApproved);
            Assert.IsFalse(string.IsNullOrWhiteSpace(status.IssuedGiftCardCode));
            Assert.IsNotNull(status.AppliedPlanExpiresAt);
        }
        finally
        {
            Directory.Delete(env.TempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlatformPlanOrder_MercadoPagoCheckoutPro_ShouldCreatePreference_AndAutoApproveViaPollingSearch()
    {
        var env = CreatePlatformBillingEnv(envToken: "APP_USR-PLATFORM-TOKEN-1");
        try
        {
            await SeedOwnerPricingAsync(env.Repo);

            var quote = GoogleAccountTierVerifier.CalculatePlanBenefits(59.90m, false, null, false);
            var quarterlyAmount = Math.Round(quote.FinalMonthlyPrice * 3m, 2, MidpointRounding.AwayFromZero);

            var order = await env.Service.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierBasic,
                DurationMonths: 3,
                Amount: quarterlyAmount,
                StoreName: "Loja Checkout Pro",
                BuyerEmail: "cliente@loja.com",
                BuyerFullName: "Maria Clara",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoCheckoutPro),
                publicBaseOrigin: "https://youlink-loja.onrender.com");

            Assert.AreEqual(PlatformSubscriptionOrder.MethodMercadoPagoCheckoutPro, order.PaymentMethod);
            Assert.IsTrue(!string.IsNullOrWhiteSpace(order.MercadoPagoCheckoutUrl) &&
                          order.MercadoPagoCheckoutUrl!.Contains("PREF-PLAN-9001", StringComparison.Ordinal));
            Assert.AreEqual(1, env.Handler.CreatePreferenceCalls);
            Assert.IsTrue(env.Handler.LastPreferenceBody!.Contains("\"back_urls\"", StringComparison.Ordinal));
            Assert.IsTrue(env.Handler.LastPreferenceBody.Contains("/api/public/webhooks/mercadopago/platform", StringComparison.Ordinal));

            // Enquanto ainda pendente, polling consulta /v1/payments/search e mantém aguardando
            var stillPending = await env.Service.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken);
            Assert.IsFalse(stillPending.IsApproved);
            Assert.IsTrue(env.Handler.SearchCalls >= 1);

            // Assim que o cliente conclui no Cartão/MP e a busca retorna approved, aprova sem ação manual
            env.Handler.IncludeSearchResultPayment = true;
            env.Handler.SearchPaymentAmount = quarterlyAmount;
            env.Handler.SearchPaymentStatus = "approved";

            var autoApproved = await env.Service.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken);
            Assert.IsTrue(autoApproved.IsApproved);
            Assert.IsFalse(string.IsNullOrWhiteSpace(autoApproved.IssuedGiftCardCode));
            Assert.IsNotNull(autoApproved.AppliedPlanExpiresAt);
        }
        finally
        {
            Directory.Delete(env.TempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlatformPlanOrder_MercadoPagoSubscription_ShouldValidateEmail_ActivateOnWebhook_AndExtendOnRenewal()
    {
        var env = CreatePlatformBillingEnv(envToken: "APP_USR-PLATFORM-TOKEN-1");
        try
        {
            await SeedOwnerPricingAsync(env.Repo);

            // Assinatura exige e-mail válido do assinante
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await env.Service.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                    PlanTier: StoreSettings.PlanTierBasic,
                    DurationMonths: 1,
                    Amount: 59.90m,
                    StoreName: "Loja Sem Email",
                    BuyerEmail: "usuario-sem-arroba",
                    PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoSubscription),
                    publicBaseOrigin: "https://youlink-loja.onrender.com"));

            var order = await env.Service.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierBasic,
                DurationMonths: 1,
                Amount: 59.90m,
                StoreName: "Loja Assinatura",
                BuyerEmail: "lojista@teste.com",
                PaymentMethod: PlatformSubscriptionOrder.MethodMercadoPagoSubscription),
                publicBaseOrigin: "https://youlink-loja.onrender.com");

            Assert.AreEqual(PlatformSubscriptionOrder.MethodMercadoPagoSubscription, order.PaymentMethod);
            Assert.AreEqual("PREAPP-SUB-1001", order.MercadoPagoPreapprovalId);
            Assert.IsTrue(!string.IsNullOrWhiteSpace(order.MercadoPagoCheckoutUrl) &&
                          order.MercadoPagoCheckoutUrl!.Contains("PREAPP-SUB-1001", StringComparison.Ordinal));
            Assert.AreEqual(1, env.Handler.CreatePreapprovalCalls);
            Assert.IsTrue(env.Handler.LastPreapprovalBody!.Contains("\"auto_recurring\"", StringComparison.Ordinal));
            Assert.IsTrue(env.Handler.LastPreapprovalBody.Contains("\"frequency_type\":\"months\"", StringComparison.Ordinal));

            // Webhook de autorização de preapproval ativa o plano
            env.Handler.PreapprovalStatus = "authorized";
            env.Handler.PreapprovalTransactionAmount = 59.90m;
            var activation = await env.Service.HandleMercadoPagoWebhookAsync("subscription_preapproval", "PREAPP-SUB-1001");
            Assert.IsTrue(activation.Handled);
            Assert.IsTrue(activation.OrderApproved);

            var statusAfterActivation = await env.Service.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken);
            Assert.IsTrue(statusAfterActivation.IsApproved);
            Assert.IsNotNull(statusAfterActivation.AppliedPlanExpiresAt);
            var initialExpiry = statusAfterActivation.AppliedPlanExpiresAt!.Value;

            // Cobrança recorrente subsequente (subscription_authorized_payment) renova e estende a validade
            env.Handler.AuthorizedPaymentStatus = "approved";
            var renewal = await env.Service.HandleMercadoPagoWebhookAsync("subscription_authorized_payment", "44556677");
            Assert.IsTrue(renewal.Handled);
            Assert.IsTrue(renewal.OrderApproved);

            var statusAfterRenewal = await env.Service.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken);
            Assert.IsNotNull(statusAfterRenewal.AppliedPlanExpiresAt);
            Assert.IsTrue(statusAfterRenewal.AppliedPlanExpiresAt!.Value > initialExpiry,
                "Renovação automática de assinatura deve estender AppliedPlanExpiresAt.");
        }
        finally
        {
            Directory.Delete(env.TempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlatformMercadoPagoTokenStore_AndOAuth_ShouldEncryptTokens_Refresh_AndKeepTenantsIsolated()
    {
        var env = CreatePlatformBillingEnv(envToken: null);
        try
        {
            var storeManager = new MultiTenantStoreManager(env.Repo, env.Hasher, env.TempDir);
            var gateway = new MultiTenantGatewayService(
                storeManager,
                env.MpService,
                new HttpClient(env.Handler),
                env.Hasher,
                env.Storage,
                emailService: null,
                platformTokenStore: env.TokenStore);

            // 1) Fluxo OAuth do dono da plataforma salva credenciais criptografadas sem criar loja tenant
            env.Handler.AccessTokenToIssue = "APP_USR-SECRET-OWNER-999";
            env.Handler.OAuthExpiresInSeconds = 60; // perto de expirar (< 10 min) para testar auto-refresh

            var start = gateway.StartPlatformOwnerMercadoPagoOAuth("https://youlink-loja.onrender.com/api/payments/mercadopago/oauth/callback");
            Assert.IsTrue(start.IsConfigured);
            Assert.IsFalse(string.IsNullOrWhiteSpace(start.StateToken));
            Assert.IsFalse(string.IsNullOrWhiteSpace(start.AuthorizationUrl));

            var callback = await gateway.CompleteMercadoPagoOAuthCallbackAsync(
                "CODE_OWNER_123",
                start.StateToken,
                error: null,
                errorDescription: null);

            Assert.IsTrue(callback.Success);
            Assert.AreEqual(0, (await storeManager.ListTenantSlugsAsync()).Count,
                "OAuth da conta dona da plataforma não pode poluir lojas tenant.");

            var storedJsonPath = Path.Combine(env.TempDir, "platform_mp_credentials.json");
            Assert.IsTrue(File.Exists(storedJsonPath));
            var rawJson = await File.ReadAllTextAsync(storedJsonPath, Encoding.UTF8);
            Assert.IsFalse(rawJson.Contains("APP_USR-SECRET-OWNER-999", StringComparison.Ordinal),
                "Access token da plataforma precisa estar criptografado com AES-256-GCM em disco.");
            Assert.IsTrue(rawJson.Contains("enc:v1:", StringComparison.Ordinal));

            var statusOAuth = await env.TokenStore.GetConnectionStatusAsync(isAppConfigured: true);
            Assert.IsTrue(statusOAuth.IsConnected);
            Assert.IsFalse(statusOAuth.UsingEnvironmentToken);
            Assert.IsTrue(statusOAuth.AccountLabel!.Contains("YOULINK_OFICIAL", StringComparison.Ordinal));

            // 2) Como expires_in=60s (< 10 min), GetValidAccessTokenAsync renova automaticamente via refresh_token
            var validToken = await env.TokenStore.GetValidAccessTokenAsync(env.MpService);
            Assert.AreEqual("APP_USR-PLATFORM-REFRESHED-2", validToken);
            Assert.AreEqual(1, env.Handler.RefreshCalls);

            // 3) Quando MP_PLATFORM_ACCESS_TOKEN está presente no ambiente, ele tem prioridade máxima
            var envPriorityStore = new PlatformMercadoPagoTokenStore(env.TempDir, () => "APP_USR-RENDER-ENV-OVERRIDE");
            var envToken = await envPriorityStore.GetValidAccessTokenAsync(env.MpService);
            Assert.AreEqual("APP_USR-RENDER-ENV-OVERRIDE", envToken);
            var envStatus = await envPriorityStore.GetConnectionStatusAsync(isAppConfigured: true);
            Assert.IsTrue(envStatus.IsConnected);
            Assert.IsTrue(envStatus.UsingEnvironmentToken);
        }
        finally
        {
            Directory.Delete(env.TempDir, recursive: true);
        }
    }
}
