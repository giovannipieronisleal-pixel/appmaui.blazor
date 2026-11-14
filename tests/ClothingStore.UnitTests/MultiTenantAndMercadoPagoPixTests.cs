using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public sealed class MultiTenantAndMercadoPagoPixTests
{
    private sealed class MockMercadoPagoHttpHandler : HttpMessageHandler
    {
        public string AccessTokenToIssue { get; set; } = "APP_USR-SECRET-TOKEN-1";
        public string RefreshTokenToIssue { get; set; } = "TG-REFRESH-1";
        public string RefreshedAccessTokenToIssue { get; set; } = "APP_USR-REFRESHED-TOKEN-2";
        public string CurrentPaymentStatus { get; set; } = "pending";
        public string LastCreatedExternalReference { get; private set; } = string.Empty;
        public string? LastNotificationUrl { get; private set; }
        public int RefreshCallsCount { get; private set; }
        public int CreatePaymentCallsCount { get; private set; }
        public int GetPaymentCallsCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (request.Method == HttpMethod.Post && url.Contains("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("refresh_token", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshCallsCount++;
                    return JsonResponse(new
                    {
                        access_token = RefreshedAccessTokenToIssue,
                        refresh_token = "TG-REFRESH-2",
                        expires_in = 21600,
                        user_id = 998877L
                    });
                }

                return JsonResponse(new
                {
                    access_token = AccessTokenToIssue,
                    refresh_token = RefreshTokenToIssue,
                    expires_in = 21600,
                    user_id = 998877L
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(new
                {
                    id = 998877L,
                    nickname = "ATELIER_MP",
                    email = "mp@atelier.com.br"
                });
            }

            if (request.Method == HttpMethod.Post && url.EndsWith("/v1/payments", StringComparison.OrdinalIgnoreCase))
            {
                CreatePaymentCallsCount++;
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                Assert.IsTrue(!string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter));
                Assert.IsTrue(request.Headers.Contains("X-Idempotency-Key"));

                var raw = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("external_reference", out var extRef))
                {
                    LastCreatedExternalReference = extRef.GetString() ?? string.Empty;
                }
                if (doc.RootElement.TryGetProperty("notification_url", out var notif))
                {
                    LastNotificationUrl = notif.GetString();
                }

                return JsonResponse(new
                {
                    id = 9876543210L,
                    status = "pending",
                    external_reference = LastCreatedExternalReference,
                    point_of_interaction = new
                    {
                        transaction_data = new
                        {
                            qr_code = "00020101021226770014BR.GOV.BCB.PIX0136MP-DYNAMIC-PIX-98765432106304ABCD",
                            qr_code_base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && url.Contains("/v1/payments/9876543210", StringComparison.OrdinalIgnoreCase))
            {
                GetPaymentCallsCount++;
                return JsonResponse(new
                {
                    id = 9876543210L,
                    status = CurrentPaymentStatus,
                    external_reference = LastCreatedExternalReference
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unmocked URL: {request.Method} {url}")
            };
        }

        private static HttpResponseMessage JsonResponse(object obj) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
            };
    }

    private static (string TempDir, ClothingStoreRepository DefaultRepo, MultiTenantStoreManager Resolver, MockMercadoPagoHttpHandler MpHandler, MercadoPagoPixService MpService, MultiTenantGatewayService Gateway) CreateMultiTenantHost(
        string? clientId = "mp-client-123",
        string? clientSecret = "mp-secret-456",
        string? webhookSecret = "mp-webhook-secret-xyz")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink-mt-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var defaultDbPath = Path.Combine(tempDir, "db.json");
        var hasher = new Pbkdf2PasswordHasher();
        var defaultRepo = new ClothingStoreRepository(hasher, defaultDbPath);
        var resolver = new MultiTenantStoreManager(defaultRepo, hasher, tempDir);
        var mpHandler = new MockMercadoPagoHttpHandler();
        var mpHttp = new HttpClient(mpHandler);
        var mpService = new MercadoPagoPixService(
            mpHttp,
            () => new MercadoPagoAppConfig(clientId, clientSecret, webhookSecret));
        var storage = new SupabaseImageStorageService(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            Path.Combine(tempDir, "wwwroot"));

        var gateway = new MultiTenantGatewayService(
            resolver,
            mpService,
            mpHttp,
            hasher,
            storage);

        return (tempDir, defaultRepo, resolver, mpHandler, mpService, gateway);
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }

    private static CloudCatalogSyncPayload BuildStorePayload(
        string storeName,
        string storeSlug,
        string pixKey,
        string productName,
        string sku,
        int stock,
        string planTier = StoreSettings.PlanTierPremium,
        bool directPixEnabled = true,
        string pixGatewayMode = StoreSettings.PixGatewayDirect)
    {
        return new CloudCatalogSyncPayload
        {
            Settings = new UpdateStoreSettingsRequest
            {
                StoreName = storeName,
                StoreSlug = storeSlug,
                WhatsappNumber = "5511999998888",
                StoreDescription = $"Catálogo oficial {storeName}",
                IsSiteActive = true,
                AutoSyncOnlineOnSave = true,
                PlanTier = planTier,
                DirectPixSalesEnabled = directPixEnabled,
                PixGatewayMode = pixGatewayMode,
                StorePixKey = pixKey,
                StorePixReceiverName = storeName,
                OriginCep = "01310-100",
                OriginCity = "São Paulo",
                OriginState = "SP",
                ServerHostingMode = "PlatformShared"
            },
            Categories =
            [
                new SaveCategoryRequest
                {
                    Name = "Destaques",
                    Description = "Novidades",
                    DisplayOrder = 1,
                    IsActive = true
                }
            ],
            CatalogSnapshot =
            [
                new CloudProductSyncDto
                {
                    SourceId = Guid.NewGuid(),
                    CategoryName = "Destaques",
                    CategorySlug = "destaques",
                    Name = productName,
                    Slug = Category.GenerateSlug(productName),
                    Description = "Produto de teste",
                    BasePrice = 149.90m,
                    IsVisibleOnSite = true,
                    Variants =
                    [
                        new CloudProductVariantSyncDto
                        {
                            Color = "Preto",
                            ColorHex = "#111827",
                            Size = "M",
                            Sku = sku,
                            StockQuantity = stock,
                            LowStockThreshold = 2,
                            IsActive = true
                        }
                    ],
                    Images = []
                }
            ]
        };
    }

    private static string BuildValidMercadoPagoSignature(string secret, string requestId, string dataId, long ts)
    {
        var manifest = $"id:{dataId};request-id:{requestId};ts:{ts};";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest));
        return $"ts={ts},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    [TestMethod]
    public async Task MultiTenant_PublishStore_IsolatesTenants_WithoutSyncSecret_AndBlocksUnauthorizedConflict()
    {
        var (tempDir, _, resolver, _, _, gateway) = CreateMultiTenantHost();
        try
        {
            var payloadA = BuildStorePayload("Atelier Ana", "atelier-ana", "ana@pix.com.br", "Vestido Floral Ana", "ANA-01", 8);
            var resA = await gateway.PublishStoreAsync(
                payloadA,
                requestedSlug: "atelier-ana",
                syncKeyHeader: null,
                publishTokenHeader: null,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: "ana@atelier.com",
                publicServerOriginUrl: "https://central.youlink.com.br");

            Assert.IsTrue(resA.Success, resA.Message);
            Assert.AreEqual("atelier-ana", resA.StoreSlug);
            Assert.AreEqual("https://central.youlink.com.br/loja/atelier-ana", resA.PublicStoreUrl);
            Assert.IsTrue(!string.IsNullOrWhiteSpace(resA.PublishToken), "Deveria emitir PublishToken único na primeira publicação.");
            Assert.IsTrue(File.Exists(Path.Combine(tempDir, "tenants", "atelier-ana.json")));

            var payloadB = BuildStorePayload("Boutique Bruno", "boutique-bruno", "bruno@pix.com.br", "Jaqueta Couro Bruno", "BRU-01", 4);
            var resB = await gateway.PublishStoreAsync(
                payloadB,
                requestedSlug: "boutique-bruno",
                syncKeyHeader: null,
                publishTokenHeader: null,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: "bruno@boutique.com",
                publicServerOriginUrl: "https://central.youlink.com.br");

            Assert.IsTrue(resB.Success, resB.Message);
            Assert.AreEqual("boutique-bruno", resB.StoreSlug);
            Assert.AreEqual("https://central.youlink.com.br/loja/boutique-bruno", resB.PublicStoreUrl);

            // Verifica isolamento estrito de leitura entre /loja/atelier-ana, /loja/boutique-bruno e inexistente
            var repoA = await gateway.ResolveReadRepositoryAsync("atelier-ana");
            var repoB = await gateway.ResolveReadRepositoryAsync("boutique-bruno");
            var repoMissing = await gateway.ResolveReadRepositoryAsync("loja-fantasma");

            Assert.IsNotNull(repoA);
            Assert.IsNotNull(repoB);
            Assert.IsNull(repoMissing);

            var catServiceA = gateway.CreateCatalogService(repoA!);
            var catServiceB = gateway.CreateCatalogService(repoB!);
            var settingsA = await catServiceA.GetPublicStoreSettingsAsync();
            var productsA = await catServiceA.GetCatalogProductsAsync(new CatalogQueryFilter());
            var settingsB = await catServiceB.GetPublicStoreSettingsAsync();
            var productsB = await catServiceB.GetCatalogProductsAsync(new CatalogQueryFilter());

            Assert.AreEqual("Atelier Ana", settingsA.StoreName);
            Assert.AreEqual(1, productsA.Count);
            Assert.AreEqual("Vestido Floral Ana", productsA[0].Name);

            Assert.AreEqual("Boutique Bruno", settingsB.StoreName);
            Assert.AreEqual(1, productsB.Count);
            Assert.AreEqual("Jaqueta Couro Bruno", productsB[0].Name);

            // Terceiro sem PublishToken e sem e-mail dono é bloqueado de sobrescrever atelier-ana
            var hijackPayload = BuildStorePayload("Hackers", "atelier-ana", "hacker@pix.com", "Item Falso", "HCK-01", 99);
            var hijackRes = await gateway.PublishStoreAsync(
                hijackPayload,
                requestedSlug: "atelier-ana",
                syncKeyHeader: null,
                publishTokenHeader: "token-errado",
                hasValidSyncKey: false,
                authenticatedOwnerEmail: "outro@email.com",
                publicServerOriginUrl: "https://central.youlink.com.br");
            Assert.IsFalse(hijackRes.Success);

            // Dona legítima consegue atualizar usando PublishToken emitido
            var updatePayloadA = BuildStorePayload("Atelier Ana Luxo", "atelier-ana", "ana@pix.com.br", "Vestido Seda Ana", "ANA-02", 6);
            var updateRes = await gateway.PublishStoreAsync(
                updatePayloadA,
                requestedSlug: "atelier-ana",
                syncKeyHeader: null,
                publishTokenHeader: resA.PublishToken,
                hasValidSyncKey: false,
                authenticatedOwnerEmail: null,
                publicServerOriginUrl: "https://central.youlink.com.br");
            Assert.IsTrue(updateRes.Success, updateRes.Message);

            var updatedSettingsA = await catServiceA.GetPublicStoreSettingsAsync();
            var updatedProductsA = await catServiceA.GetCatalogProductsAsync(new CatalogQueryFilter());
            Assert.AreEqual("Atelier Ana Luxo", updatedSettingsA.StoreName);
            Assert.AreEqual("Vestido Seda Ana", updatedProductsA[0].Name);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task MercadoPagoOAuth_EncryptsTokensOnDisk_NeverLeaksSecrets_AndAutoRefreshesExpiredToken()
    {
        var (tempDir, _, resolver, mpHandler, _, gateway) = CreateMultiTenantHost();
        try
        {
            var payload = BuildStorePayload("Loja MP", "loja-mp", string.Empty, "Saia Plissada", "MP-01", 5, directPixEnabled: false);
            var publishRes = await gateway.PublishStoreAsync(
                payload, "loja-mp", null, null, false, "dona@lojamp.com", "https://central.youlink.com.br");
            Assert.IsTrue(publishRes.Success);

            var (startResp, _) = await gateway.StartMercadoPagoOAuthAsync(
                "loja-mp",
                null,
                publishRes.PublishToken,
                false,
                null,
                "https://central.youlink.com.br/api/payments/mercadopago/oauth/callback");

            Assert.IsTrue(startResp.IsConfigured);
            Assert.IsNotNull(startResp.AuthorizationUrl);
            Assert.IsTrue(startResp.AuthorizationUrl!.Contains("mp-client-123", StringComparison.Ordinal));
            Assert.IsTrue(!string.IsNullOrWhiteSpace(startResp.StateToken));

            var callbackRes = await gateway.CompleteMercadoPagoOAuthCallbackAsync(
                code: "TG-AUTH-CODE-999",
                state: startResp.StateToken,
                error: null,
                errorDescription: null);

            Assert.IsTrue(callbackRes.Success, callbackRes.Message);
            Assert.AreEqual("loja-mp", callbackRes.StoreSlug);
            Assert.IsTrue(callbackRes.AccountLabel.Contains("ATELIER_MP", StringComparison.Ordinal));

            // Tokens no tenants_index.json DEVEM estar criptografados com enc:v1: e nunca em texto puro
            var rawIndexJson = File.ReadAllText(Path.Combine(tempDir, "tenants_index.json"));
            Assert.IsTrue(rawIndexJson.Contains("enc:v1:", StringComparison.Ordinal));
            Assert.IsFalse(rawIndexJson.Contains("APP_USR-SECRET-TOKEN-1", StringComparison.Ordinal));
            Assert.IsFalse(rawIndexJson.Contains("TG-REFRESH-1", StringComparison.Ordinal));

            // Catálogo público não expõe AccessToken nem ClientSecret
            var repo = await gateway.ResolveReadRepositoryAsync("loja-mp");
            var publicStore = await gateway.CreateCatalogService(repo!).GetPublicStoreSettingsAsync();
            Assert.IsTrue(publicStore.HasMercadoPagoConnected);
            Assert.AreEqual(StoreSettings.PixGatewayMercadoPago, publicStore.PixGatewayMode);
            Assert.IsTrue(publicStore.DirectPixSalesReady);

            var publicJson = JsonSerializer.Serialize(publicStore);
            Assert.IsFalse(publicJson.Contains("APP_USR-", StringComparison.Ordinal));
            Assert.IsFalse(publicJson.Contains("mp-secret-456", StringComparison.Ordinal));

            // Força expiração do access_token e verifica refresh automático
            await resolver.SaveMercadoPagoTokensAsync(
                "loja-mp",
                "APP_USR-EXPIRED",
                "TG-REFRESH-1",
                DateTimeOffset.UtcNow.AddMinutes(-10),
                "998877",
                "ATELIER_MP");

            var renewedToken = await gateway.EnsureValidMercadoPagoAccessTokenAsync("loja-mp");
            Assert.AreEqual("APP_USR-REFRESHED-TOKEN-2", renewedToken);
            Assert.AreEqual(1, mpHandler.RefreshCallsCount);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task MercadoPagoDynamicPix_WebhookAndPolling_ConfirmOrderAndDeductStockExactlyOnce()
    {
        var (tempDir, _, _, mpHandler, _, gateway) = CreateMultiTenantHost(webhookSecret: "whsec-prod-123");
        try
        {
            var payload = BuildStorePayload(
                "Loja Auto",
                "loja-auto",
                string.Empty,
                "Calça Cargo",
                "CARGO-M",
                5,
                directPixEnabled: false,
                pixGatewayMode: StoreSettings.PixGatewayMercadoPago);
            var pub = await gateway.PublishStoreAsync(
                payload, "loja-auto", null, null, false, "auto@loja.com", "https://central.youlink.com.br");
            Assert.IsTrue(pub.Success);

            var (start, _) = await gateway.StartMercadoPagoOAuthAsync(
                "loja-auto", null, pub.PublishToken, false, null,
                "https://central.youlink.com.br/api/payments/mercadopago/oauth/callback");
            var cb = await gateway.CompleteMercadoPagoOAuthCallbackAsync("AUTH-CODE", start.StateToken, null, null);
            Assert.IsTrue(cb.Success);

            var repo = await gateway.ResolveReadRepositoryAsync("loja-auto");
            Assert.IsNotNull(repo);
            var products = await repo!.GetProductsAsync(includeArchived: false);
            var product = products[0];
            var variant = product.Variants[0];
            Assert.AreEqual(5, variant.StockQuantity);

            // Cliente cria pedido na vitrine com Pix dinâmico Mercado Pago
            var order = await gateway.PlacePixOrderForStoreAsync(
                "loja-auto",
                new CreatePixOrderRequest
                {
                    ProductId = product.Id,
                    ProductVariantId = variant.Id,
                    Quantity = 2,
                    CustomerName = "Camila Costa",
                    CustomerWhatsapp = "11977776666",
                    Cep = "01310-100",
                    Street = "Av Paulista",
                    Number = "1200",
                    Neighborhood = "Bela Vista",
                    City = "São Paulo",
                    State = "SP"
                },
                "https://central.youlink.com.br");

            Assert.AreEqual(StoreOrder.StatusAwaitingPix, order.Status);
            Assert.AreEqual(StoreOrder.PaymentProviderMercadoPago, order.PaymentProvider);
            Assert.AreEqual("9876543210", order.ProviderPaymentId);
            Assert.IsTrue(order.PixBrCode.Contains("MP-DYNAMIC-PIX-9876543210", StringComparison.Ordinal));
            Assert.IsTrue(!string.IsNullOrWhiteSpace(order.PixQrCodeBase64));
            Assert.IsTrue(!string.IsNullOrWhiteSpace(order.PublicStatusToken));
            Assert.AreEqual(1, mpHandler.CreatePaymentCallsCount);
            Assert.AreEqual(
                "https://central.youlink.com.br/api/public/webhooks/mercadopago/loja-auto",
                mpHandler.LastNotificationUrl);

            // Antes da aprovação, estoque continua intacto em 5
            var prodBeforePay = await repo.GetProductByIdAsync(product.Id);
            Assert.AreEqual(5, prodBeforePay!.Variants[0].StockQuantity);

            // Webhook com assinatura HMAC inválida deve ser rejeitado como Unauthorized
            var badWebhook = await gateway.ProcessMercadoPagoWebhookAsync(
                "loja-auto",
                "9876543210",
                "payment",
                "ts=1700000000,v1=assinaturainvalida",
                "req-1",
                "{}");
            Assert.IsTrue(badWebhook.IsUnauthorized);
            Assert.IsFalse(badWebhook.OrderConfirmed);

            // Cliente paga o Pix no banco -> Mercado Pago passa a retornar approved -> webhook válido confirma pedido
            mpHandler.CurrentPaymentStatus = "approved";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var validSig = BuildValidMercadoPagoSignature("whsec-prod-123", "req-2", "9876543210", ts);
            var goodWebhook = await gateway.ProcessMercadoPagoWebhookAsync(
                "loja-auto",
                "9876543210",
                "payment",
                validSig,
                "req-2",
                """{"type":"payment","data":{"id":"9876543210"}}""");

            Assert.IsTrue(goodWebhook.SignatureValid);
            Assert.IsTrue(goodWebhook.OrderConfirmed);

            // Estoque caiu de 5 para 3 uma única vez!
            var prodAfterWebhook = await repo.GetProductByIdAsync(product.Id);
            Assert.AreEqual(3, prodAfterWebhook!.Variants[0].StockQuantity);

            // Segundo webhook duplicado + polling na vitrine NÃO devem baixar estoque novamente (idempotência estrita)
            var duplicateWebhook = await gateway.ProcessMercadoPagoWebhookAsync(
                "loja-auto",
                "9876543210",
                "payment",
                validSig,
                "req-2",
                """{"type":"payment","data":{"id":"9876543210"}}""");
            Assert.IsTrue(duplicateWebhook.SignatureValid);

            var statusPoll = await gateway.GetPublicOrderStatusAsync(
                "loja-auto",
                order.Id,
                order.PublicStatusToken!);
            Assert.IsNotNull(statusPoll);
            Assert.IsTrue(statusPoll!.IsPaid);
            Assert.AreEqual(StoreOrder.StatusPaidPreparing, statusPoll.Status);

            var prodAfterDuplicates = await repo.GetProductByIdAsync(product.Id);
            Assert.AreEqual(3, prodAfterDuplicates!.Variants[0].StockQuantity);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task LocalMergeRemoteOrders_MatchesBySkuAndColorSize_AndDeductsStockExactlyOnce()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink-sync-mt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var localRepo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "local.json"));
            var storage = new SupabaseImageStorageService(
                new HttpClient(),
                new ConfigurationBuilder().Build(),
                Path.Combine(tempDir, "wwwroot"));

            var jwtConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!" })
                .Build();
            var auth = new AuthService(localRepo, hasher, new JwtTokenService(jwtConfig));
            var reg = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Dona Local", "dona@local.com", "1234", Username: "donalocal"));
            var empId = reg.Employee.EmployeeId;

            var pm = new ProductManagementService(localRepo, storage);
            var cat = await pm.CreateCategoryAsync(new SaveCategoryRequest { Name = "Calças" });
            var localProduct = new Product(cat.Id, "Calça Cargo", "Cargo sarja", 189.90m, true);
            var localVariant = localProduct.AddVariant("Preto", "M", "#000000", "CARGO-PRT-M", 2);
            var initMov = localVariant.ApplyStockMovement(empId, StockMovementType.In, 5, "Inicial");
            await localRepo.AddProductAsync(localProduct);
            await localRepo.RecordStockMovementAsync(localVariant, initMov);

            var remoteOrderId = Guid.NewGuid();
            var remoteDto = new StoreOrderDto(
                Id: remoteOrderId,
                OrderNumber: "YL-20261114-9999",
                PaymentMethod: "Pix",
                Status: StoreOrder.StatusPaidPreparing,
                ItemsTotal: 379.80m,
                EstimatedCorreiosPac: 22.90m,
                EstimatedCorreiosSedex: 34.90m,
                AgreedShippingCost: null,
                TotalPixAmount: 379.80m,
                CustomerName: "Lucas Comprador",
                CustomerWhatsapp: "11966665555",
                DeliveryCep: "01310-100",
                DeliveryStreet: "Av Paulista",
                DeliveryNumber: "500",
                DeliveryComplement: null,
                DeliveryNeighborhood: "Bela Vista",
                DeliveryCity: "São Paulo",
                DeliveryState: "SP",
                FormattedDeliveryAddress: "Av Paulista, 500 — Bela Vista — São Paulo/SP — CEP 01310-100",
                CustomerNotes: null,
                DistanceKmFromSeller: 2.5,
                ShippingZoneLabel: "Mesma Cidade",
                EstimatedPacDaysMin: 2,
                EstimatedPacDaysMax: 5,
                EstimatedSedexDaysMin: 1,
                EstimatedSedexDaysMax: 2,
                PixKey: "mercadopago:998877",
                PixBrCode: "000201MP",
                FulfillmentMethod: BrazilShippingCalculator.FulfillmentMethods.CorreiosPac,
                TrackingCode: null,
                SellerNotes: null,
                StockDeducted: true,
                CreatedAt: DateTimeOffset.UtcNow,
                PixConfirmedAt: DateTimeOffset.UtcNow,
                ShippedAt: null,
                DeliveredAt: null,
                CancelledAt: null,
                Items:
                [
                    new StoreOrderItemDto(
                        localProduct.Id,
                        Guid.NewGuid(), // Id diferente gerado pelo sync no servidor central
                        "Calça Cargo",
                        "CARGO-PRT-M",
                        "Preto",
                        "M",
                        2,
                        189.90m,
                        379.80m)
                ],
                PaymentProvider: StoreOrder.PaymentProviderMercadoPago,
                ProviderPaymentId: "mp-pay-777");

            var orderService = new OrderAndShippingService(localRepo);

            // Puxa o pedido remoto 2 vezes e confirma que a baixa local ocorre apenas 1 vez (5 -> 3)
            var merged1 = await orderService.MergeRemoteOrdersAsync([remoteDto], empId);
            Assert.AreEqual(1, merged1);

            var merged2 = await orderService.MergeRemoteOrdersAsync([remoteDto], empId);
            Assert.AreEqual(0, merged2);

            var reloadedProd = await localRepo.GetProductByIdAsync(localProduct.Id);
            Assert.AreEqual(3, reloadedProd!.Variants[0].StockQuantity);

            var localOrders = await orderService.GetOrdersAsync();
            Assert.AreEqual(1, localOrders.Count);
            Assert.AreEqual(StoreOrder.PaymentProviderMercadoPago, localOrders[0].PaymentProvider);
            Assert.AreEqual(StoreOrder.StatusPaidPreparing, localOrders[0].Status);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void OneClickPublish_MercadoPagoUi_And_OrdersAutoRefresh_ShouldBeConfiguredCleanly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }
        Assert.IsNotNull(repoRoot);

        var wizard = File.ReadAllText(Path.Combine(repoRoot!, "Components", "Pages", "CloudPublishWizard.razor"));
        var settingsPage = File.ReadAllText(Path.Combine(repoRoot!, "Components", "Pages", "StoreSettingsPage.razor"));
        var ordersPage = File.ReadAllText(Path.Combine(repoRoot!, "Components", "Pages", "OrdersPage.razor"));
        var ownerPage = File.ReadAllText(Path.Combine(repoRoot!, "src", "ClothingStore.AdminApp", "Components", "Pages", "OwnerSettingsPage.razor"));
        var productDetail = File.ReadAllText(Path.Combine(repoRoot!, "src", "ClothingStore.Web", "Components", "Pages", "ProductDetail.razor"));
        var catalogApiClient = File.ReadAllText(Path.Combine(repoRoot!, "src", "ClothingStore.Shared", "Clients", "CatalogApiClient.cs"));

        Assert.IsTrue(wizard.Contains("Publicar Minha Loja Online (1 Clique)", StringComparison.Ordinal));
        Assert.IsTrue(settingsPage.Contains("Conectar Mercado Pago (1 Clique)", StringComparison.Ordinal));
        Assert.IsTrue(settingsPage.Contains("Pix Direto (Taxa 0%)", StringComparison.Ordinal));
        Assert.IsTrue(ordersPage.Contains("PeriodicTimer", StringComparison.Ordinal));
        Assert.IsTrue(ordersPage.Contains("Pago Automático (Mercado Pago)", StringComparison.Ordinal));
        Assert.IsTrue(ownerPage.Contains("Servidor Central Público das Vitrines", StringComparison.Ordinal));
        Assert.IsTrue(productDetail.Contains("GetPublicOrderStatusAsync", StringComparison.Ordinal));
        Assert.IsTrue(productDetail.Contains("PeriodicTimer", StringComparison.Ordinal));
        Assert.IsTrue(catalogApiClient.Contains("/orders/{orderId}/status", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DirectPixFallback_WorksWithZeroFeeAndManualConfirmation_WhenMercadoPagoNotConnected()
    {
        var (tempDir, _, _, _, _, gateway) = CreateMultiTenantHost(clientId: null, clientSecret: null);
        try
        {
            var payload = BuildStorePayload(
                "Loja Direto Zero Taxa",
                "loja-direto",
                "direto@loja.com.br",
                "Blusa Linho",
                "BL-LIN-P",
                3,
                directPixEnabled: true,
                pixGatewayMode: StoreSettings.PixGatewayDirect);

            var pub = await gateway.PublishStoreAsync(
                payload, "loja-direto", null, null, false, "direto@loja.com", "https://central.youlink.com.br");
            Assert.IsTrue(pub.Success);

            var repo = await gateway.ResolveReadRepositoryAsync("loja-direto");
            var prod = (await repo!.GetProductsAsync(false))[0];
            var order = await gateway.PlacePixOrderForStoreAsync(
                "loja-direto",
                new CreatePixOrderRequest
                {
                    ProductId = prod.Id,
                    ProductVariantId = prod.Variants[0].Id,
                    Quantity = 1,
                    CustomerName = "Joana Direto",
                    CustomerWhatsapp = "11955554444",
                    Cep = "01310-100",
                    Street = "Rua Augusta",
                    Number = "100",
                    Neighborhood = "Consolação",
                    City = "São Paulo",
                    State = "SP"
                },
                "https://central.youlink.com.br");

            Assert.AreEqual(StoreOrder.PaymentProviderDirectPix, order.PaymentProvider);
            Assert.AreEqual(StoreOrder.StatusAwaitingPix, order.Status);
            Assert.IsNull(order.ProviderPaymentId);
            Assert.IsTrue(order.PixBrCode.StartsWith("000201", StringComparison.Ordinal));
            Assert.IsTrue(order.PixBrCode.Contains("direto@loja.com.br", StringComparison.Ordinal));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }
}
