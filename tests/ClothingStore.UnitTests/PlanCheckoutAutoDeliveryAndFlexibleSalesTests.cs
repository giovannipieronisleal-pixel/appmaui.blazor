using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;
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
public class PlanCheckoutAutoDeliveryAndFlexibleSalesTests
{
    private sealed class CapturingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastMethod { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method.Method;
            LastRequestUri = request.RequestUri;
            LastHeaders.Clear();
            foreach (var h in request.Headers)
                LastHeaders[h.Key] = string.Join(",", h.Value);
            return Task.FromResult(responder(request));
        }
    }

    private static (ClothingStoreRepository Repo, string TempDir, Pbkdf2PasswordHasher Hasher, SupabaseImageStorageService Storage) CreateEnv()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_plan_flex_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "db.json");
        var hasher = new Pbkdf2PasswordHasher();
        var repo = new ClothingStoreRepository(hasher, dbPath);
        var config = new ConfigurationBuilder().Build();
        var storage = new SupabaseImageStorageService(new HttpClient(), config, Path.Combine(tempDir, "wwwroot"));
        return (repo, tempDir, hasher, storage);
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Não foi possível localizar a raiz do repositório YouLink.");
    }

    [TestMethod]
    public async Task PremiumAndIntermediate_NonPixManualSalesEnabled_CanBeToggledOnAndOff_WithoutBlockingStockControl()
    {
        var (repo, tempDir, hasher, storage) = CreateEnv();
        try
        {
            var settingsService = new EmployeeAndSettingsService(repo, hasher, storage);
            var productService = new ProductManagementService(repo, storage);
            var stockService = new StockManagementService(repo);
            var orderService = new OrderAndShippingService(repo);

            var jwtConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!" })
                .Build();
            var authService = new AuthService(repo, hasher, new JwtTokenService(jwtConfig));
            var registered = await authService.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Loja Flex Admin",
                "admin.flex@youlink.app",
                "Flex@2026!",
                Username: "admin.flex"));
            var adminEmpId = registered.Employee.EmployeeId;

            // Ativa plano Premium via código pago (DirectPixSalesEnabled continua false = não obriga Pix!)
            var promoCode = PlanActivationCode.Create(StoreSettings.PlanTierPremium, 30);
            await repo.SavePlanActivationCodeAsync(promoCode);
            var redeemed = await settingsService.RedeemActivationCodeAsync(new RedeemActivationCodeRequest { Code = promoCode.Code }, adminEmpId);
            Assert.AreEqual(StoreSettings.PlanTierPremium, redeemed.ActivatedPlanTier);

            var currentSettings = await settingsService.GetAdminStoreSettingsAsync();
            Assert.IsFalse(currentSettings.DirectPixSalesEnabled);
            Assert.IsTrue(currentSettings.ManualSalesEnabled);

            var createdProduct = await productService.CreateProductAsync(new SaveProductRequest
            {
                CategoryName = "Conjuntos",
                Name = "Conjunto Linho",
                BasePrice = 180m,
                InitialColor = "Bege",
                InitialSize = "M",
                InitialStockQuantity = 10
            });
            var variantId = createdProduct.Variants.First().Id;

            // 1) Sem Pix ativado, loja Premium pode registrar vendas normais (Dinheiro)
            var cashSale = await orderService.RegisterManualSaleAsync(
                adminEmpId,
                new RegisterManualSaleRequest(
                    createdProduct.Id,
                    variantId,
                    2,
                    "Carla Mendes",
                    null,
                    "Dinheiro",
                    null,
                    "Venda presencial sem Pix"));
            Assert.AreEqual("Dinheiro", cashSale.PaymentMethod);
            Assert.AreEqual(360m, cashSale.TotalPixAmount);

            var afterCash = await productService.GetProductByIdAsync(createdProduct.Id);
            Assert.AreEqual(8, afterCash!.Variants.First().StockQuantity);

            // 2) Desativando movimentação de vendas sem Pix: bloqueia venda manual não-Pix,
            //    MAS NÃO bloqueia movimentação normal de estoque!
            var disabled = await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = currentSettings.StoreName,
                StoreSlug = currentSettings.StoreSlug,
                WhatsappNumber = currentSettings.WhatsappNumber,
                StoreDescription = currentSettings.StoreDescription,
                ManualSalesEnabled = false
            });
            Assert.IsFalse(disabled.ManualSalesEnabled);

            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderService.RegisterManualSaleAsync(
                    adminEmpId,
                    new RegisterManualSaleRequest(
                        createdProduct.Id,
                        variantId,
                        1,
                        "Cliente Teste",
                        null,
                        "Dinheiro",
                        null,
                        null)));

            var stockAdj = await stockService.RegisterMovementAsync(adminEmpId, new CreateStockMovementRequest
            {
                ProductVariantId = variantId,
                MovementType = "OUT",
                Quantity = 2,
                Reason = "Ajuste de balcão / vitrine sem venda"
            });
            Assert.AreEqual(6, stockAdj.NewQuantity);

            // 3) Reativando movimentação de vendas sem Pix: volta a registrar normalmente (Transferência)
            var reenabled = await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = currentSettings.StoreName,
                StoreSlug = currentSettings.StoreSlug,
                WhatsappNumber = currentSettings.WhatsappNumber,
                StoreDescription = currentSettings.StoreDescription,
                ManualSalesEnabled = true
            });
            Assert.IsTrue(reenabled.ManualSalesEnabled);

            var transferSale = await orderService.RegisterManualSaleAsync(
                adminEmpId,
                new RegisterManualSaleRequest(
                    createdProduct.Id,
                    variantId,
                    1,
                    "Bruna Lima",
                    null,
                    "Transferência",
                    null,
                    "Pago via TED"));
            Assert.AreEqual("Transferência", transferSale.PaymentMethod);

            var afterTransfer = await productService.GetProductByIdAsync(createdProduct.Id);
            Assert.AreEqual(5, afterTransfer!.Variants.First().StockQuantity);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task PlatformSubscriptionOrder_FullAutoDeliveryFlow_Create_RejectWrongClaimToken_Approve_AndActivateBuyerStore()
    {
        var (ownerRepo, ownerDir, ownerHasher, ownerStorage) = CreateEnv();
        var (buyerRepo, buyerDir, buyerHasher, buyerStorage) = CreateEnv();
        try
        {
            var ownerSettingsService = new EmployeeAndSettingsService(ownerRepo, ownerHasher, ownerStorage);
            var ownerSubService = new PlatformSubscriptionService(ownerRepo);
            var buyerSettingsService = new EmployeeAndSettingsService(buyerRepo, buyerHasher, buyerStorage);

            // Dono configura chave Pix oficial e preços da plataforma
            var ownerSynced = await ownerSettingsService.SyncOwnerPricingAsync(new UpdateOwnerPricingSyncRequest(
                "dono@youlink.com.br",
                49.90m,
                89.90m,
                139.90m));
            Assert.AreEqual("dono@youlink.com.br", ownerSynced.PicpayPixKey);
            Assert.AreEqual(139.90m, ownerSynced.PremiumPlanMonthlyPrice);

            // Loja compradora começa no Plano Básico sem nada ativo
            var initialBuyer = await buyerSettingsService.GetAdminStoreSettingsAsync();
            Assert.AreEqual(StoreSettings.PlanTierBasic, initialBuyer.PlanTier);

            // Ao clicar para comprar no checkout, cria pedido e só então recebe chave Pix e EMV
            var order = await ownerSubService.CreatePlanOrderAsync(new CreatePlatformPlanOrderRequest(
                PlanTier: StoreSettings.PlanTierPremium,
                DurationMonths: 3,
                Amount: 419.70m,
                StoreName: "Boutique Primavera",
                StoreSlug: "boutique-primavera",
                DesiredDomain: "https://lojas.boutiqueprimavera.com",
                UseOwnDedicatedServer: false,
                BuyerUserId: Guid.NewGuid(),
                BuyerUsername: "joana.primavera",
                BuyerEmail: "joana@gmail.com",
                BuyerFullName: "Joana Silva"));

            Assert.AreEqual("Pendente", order.Status);
            Assert.AreEqual("dono@youlink.com.br", order.PixKeyUsed);
            Assert.IsTrue(!string.IsNullOrWhiteSpace(order.PixBrCode) && PixPayloadBuilder.VerifyCrc16Ccitt(order.PixBrCode));
            Assert.IsTrue(!string.IsNullOrWhiteSpace(order.ClaimToken));

            // Token errado nunca pode consultar o pedido
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await ownerSubService.GetPlanOrderStatusAsync(order.OrderNumber, "token-falso"));

            var pendingStatus = await ownerSubService.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken);
            Assert.IsFalse(pendingStatus.IsApproved);

            // Dono aprova no YouLink Admin
            var approvedOrder = await ownerSubService.ApprovePlanOrderAsync(order.Id, "Admin Teste");
            Assert.AreEqual("Pago", approvedOrder.Status);
            Assert.IsNotNull(approvedOrder.IssuedGiftCardCode);
            Assert.IsNotNull(approvedOrder.AppliedPlanExpiresAt);

            // Polling no app do comprador detecta aprovação e aplica versão paga automaticamente
            var approvedStatus = await ownerSubService.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken);
            Assert.IsTrue(approvedStatus.IsApproved);
            Assert.AreEqual(StoreSettings.PlanTierPremium, approvedStatus.PlanTier);

            var buyerActivated = await buyerSettingsService.ApplyApprovedRemotePlanAsync(approvedStatus);
            Assert.AreEqual(StoreSettings.PlanTierPremium, buyerActivated.PlanTier);
            Assert.AreEqual(approvedStatus.IssuedGiftCardCode, buyerActivated.ActiveGiftCardCode);
            Assert.IsTrue(buyerActivated.IsSiteActive);
            Assert.IsTrue(buyerActivated.PlanExpiresAt.HasValue && buyerActivated.PlanExpiresAt.Value > DateTimeOffset.UtcNow.AddDays(80));

            // Idempotência: chamar ApplyApprovedRemotePlanAsync novamente mantém mesmo estado sem erro
            var secondPass = await buyerSettingsService.ApplyApprovedRemotePlanAsync(approvedStatus);
            Assert.AreEqual(StoreSettings.PlanTierPremium, secondPass.PlanTier);
            Assert.AreEqual(approvedStatus.IssuedGiftCardCode, secondPass.ActiveGiftCardCode);
        }
        finally
        {
            CleanupDir(ownerDir);
            CleanupDir(buyerDir);
        }
    }

    [TestMethod]
    public void PlanCheckoutAndPlansPages_MustHideOwnerPixKey_UntilUserClicksBuyButton()
    {
        var root = FindRepoRoot();

        var plansPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlansPage.razor"), Encoding.UTF8);
        Assert.IsFalse(plansPage.Contains("@_settings.PicpayPixKey", StringComparison.Ordinal),
            "PlansPage não deve exibir a chave Pix do dono antes do clique de compra.");

        var checkoutPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlanCheckoutPage.razor"), Encoding.UTF8);
        Assert.IsFalse(checkoutPage.Contains("@_settings.PicpayPixKey", StringComparison.Ordinal),
            "PlanCheckoutPage não pode exibir @_settings.PicpayPixKey antes do clique de compra.");
        Assert.IsFalse(checkoutPage.Contains("_settings.PicpayPixKey", StringComparison.Ordinal),
            "PlanCheckoutPage deve usar somente @_activePlanOrder.PixKeyUsed após o clique de compra.");
        Assert.IsTrue(checkoutPage.Contains("_activePlanOrder is null", StringComparison.Ordinal),
            "PlanCheckoutPage deve manter a chave Pix oculta enquanto _activePlanOrder for nulo.");
        Assert.IsTrue(checkoutPage.Contains("@_activePlanOrder.PixKeyUsed", StringComparison.Ordinal),
            "PlanCheckoutPage deve exibir a chave Pix somente após criar _activePlanOrder.");
        Assert.IsTrue(checkoutPage.Contains("ConfirmDirectMobilePlanOrderAsync", StringComparison.Ordinal));

        var dashboardPath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Components", "Pages", "Dashboard.razor");
        if (File.Exists(dashboardPath))
        {
            var dashboard = File.ReadAllText(dashboardPath, Encoding.UTF8);
            Assert.IsTrue(dashboard.Contains("ApprovePlanOrderAsync", StringComparison.Ordinal),
                "YouLink Admin Dashboard deve permitir confirmar Pix e liberar plano automático em 1 clique.");
        }

        var salesRegistryPath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Components", "Pages", "SalesRegistryPage.razor");
        if (File.Exists(salesRegistryPath))
        {
            var salesRegistry = File.ReadAllText(salesRegistryPath, Encoding.UTF8);
            Assert.IsTrue(salesRegistry.Contains("Gateway.DataUpdated", StringComparison.Ordinal),
                "SalesRegistryPage precisa atualizar ao vivo quando pedidos de planos chegarem.");
        }
    }

    [TestMethod]
    public async Task AdminApiClient_ShouldSendSyncKey_ForPlanOrderApproval_AndOwnerPricingSync()
    {
        var handler = new CapturingHttpHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/owner-pricing", StringComparison.OrdinalIgnoreCase))
            {
                var dto = new AdminStoreSettingsDto(
                    Guid.NewGuid(),
                    "Loja Owner",
                    "loja-owner",
                    "5511999999999",
                    "Desc",
                    null,
                    null,
                    3,
                    DateTimeOffset.UtcNow,
                    PlanTier: StoreSettings.PlanTierPremium,
                    PicpayPixKey: "pix@dono.com");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(dto)
                };
            }

            var orderDto = new PlatformSubscriptionOrderDto(
                Guid.NewGuid(), "YLK-20261114-1234", "tok", StoreSettings.PlanTierPremium,
                1, 30, 129.90m, "Loja X", "loja-x", "https://lojas.lojax.com", false,
                null, "u", "u@gmail.com", "U", "pix@dono.com", "", "Pago",
                "LUXO-PREM-30D-AAAABBBB", DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, "Admin", "OK");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(orderDto)
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid") };
        var client = new AdminApiClient(http);

        await client.PushOwnerPricingSyncAsync(
            new UpdateOwnerPricingSyncRequest("pix@dono.com", 49.90m, 89.90m, 129.90m),
            syncSecret: "chave-secreta-sync");
        Assert.AreEqual("POST", handler.LastMethod);
        Assert.AreEqual("/api/platform-sync/owner-pricing", handler.LastRequestUri?.AbsolutePath);
        Assert.AreEqual("chave-secreta-sync", handler.LastHeaders["X-YouLink-Sync-Key"]);

        var orderId = Guid.NewGuid();
        await client.ApprovePlatformPlanOrderRemoteAsync(orderId, syncSecret: "chave-secreta-sync");
        Assert.AreEqual("POST", handler.LastMethod);
        Assert.AreEqual($"/api/platform-sync/plan-orders/{orderId}/approve", handler.LastRequestUri?.AbsolutePath);
        Assert.AreEqual("chave-secreta-sync", handler.LastHeaders["X-YouLink-Sync-Key"]);
    }

    [TestMethod]
    public void PlanActivationCode_RevokeAndReactivate_ShouldToggleAvailability_WithoutConsuming()
    {
        var card = PlanActivationCode.Create(
            targetPlanTier: StoreSettings.PlanTierPremium,
            durationDays: null,
            customCode: "LUXO-PREM-VIT-CARD01",
            targetStoreName: null);

        Assert.IsTrue(card.CanRedeem());
        Assert.IsFalse(card.IsRevoked);
        Assert.IsFalse(card.IsRedeemed);
        Assert.IsNull(card.TargetStoreName);

        card.Revoke();
        Assert.IsTrue(card.IsRevoked);
        Assert.IsFalse(card.CanRedeem());
        Assert.IsNotNull(card.StatusUpdatedAt);

        card.Reactivate();
        Assert.IsFalse(card.IsRevoked);
        Assert.IsFalse(card.IsRedeemed);
        Assert.IsNull(card.RedeemedByStoreName);
        Assert.IsTrue(card.CanRedeem());

        card.MarkRedeemed("Boutique Destino", Guid.NewGuid(), null);
        Assert.IsTrue(card.IsRedeemed);
        Assert.AreEqual("Boutique Destino", card.RedeemedByStoreName);

        Assert.ThrowsException<DomainException>(() => card.Reactivate());
    }

    [TestMethod]
    public async Task RemoteGiftCardRedeem_ShouldUpgradeOnlyRecipientStore_AndLeaveAdminHostStoreUntouched()
    {
        var adminEnv = CreateEnv();
        var recipientEnv = CreateEnv();
        try
        {
            var adminSettings = await adminEnv.Repo.GetStoreSettingsAsync();
            adminSettings.StoreName = "Atelier Verano";
            adminSettings.UpdatePlanAndDesign(StoreSettings.PlanTierBasic);
            await adminEnv.Repo.UpdateStoreSettingsAsync(adminSettings);

            var recipientSettings = await recipientEnv.Repo.GetStoreSettingsAsync();
            recipientSettings.StoreName = "Boutique Recebedora";
            recipientSettings.UpdatePlanAndDesign(StoreSettings.PlanTierBasic);
            await recipientEnv.Repo.UpdateStoreSettingsAsync(recipientSettings);

            var universalCard = PlanActivationCode.Create(
                targetPlanTier: StoreSettings.PlanTierPremium,
                durationDays: null,
                customCode: "LUXO-PREM-VIT-DESTINO",
                targetStoreName: null,
                isGiftedUnpaid: true);
            universalCard.Revoke();
            universalCard.Reactivate();
            await adminEnv.Repo.SavePlanActivationCodeAsync(universalCard);

            var adminService = new EmployeeAndSettingsService(adminEnv.Repo, adminEnv.Hasher, adminEnv.Storage);
            var grant = await adminService.RedeemGiftCardForRemoteStoreAsync(new PlatformGiftCardRedeemRequest(
                Code: universalCard.Code,
                StoreName: "Boutique Recebedora",
                EmployeeId: Guid.NewGuid()));

            Assert.AreEqual("Boutique Recebedora", grant.RedeemedByStoreName);
            Assert.AreEqual(StoreSettings.PlanTierPremium, grant.TargetPlanTier);
            Assert.IsTrue(grant.IsGiftedUnpaid);

            // Central/Admin store MUST remain untouched on Basico; code marked as redeemed by Boutique Recebedora
            var reloadedAdminSettings = await adminEnv.Repo.GetStoreSettingsAsync();
            Assert.AreEqual("Atelier Verano", reloadedAdminSettings.StoreName);
            Assert.AreEqual(StoreSettings.PlanTierBasic, reloadedAdminSettings.GetEffectivePlanTier());
            Assert.IsFalse(string.Equals(universalCard.Code, reloadedAdminSettings.ActiveGiftCardCode, StringComparison.OrdinalIgnoreCase));

            var storedCodeOnHub = await adminEnv.Repo.GetPlanActivationCodeByCodeAsync(universalCard.Code);
            Assert.IsNotNull(storedCodeOnHub);
            Assert.IsTrue(storedCodeOnHub!.IsRedeemed);
            Assert.AreEqual("Boutique Recebedora", storedCodeOnHub.RedeemedByStoreName);

            // Second attempt by any other store must fail because card is already redeemed
            await Assert.ThrowsExceptionAsync<DomainException>(
                () => adminService.RedeemGiftCardForRemoteStoreAsync(new PlatformGiftCardRedeemRequest(universalCard.Code, "Outra Loja")));

            // Recipient store applies the grant locally
            var recipientService = new EmployeeAndSettingsService(recipientEnv.Repo, recipientEnv.Hasher, recipientEnv.Storage);
            var applied = await recipientService.ApplyRedeemedRemoteGiftCardAsync(grant, Guid.NewGuid());
            Assert.AreEqual(StoreSettings.PlanTierPremium, applied.ActivatedPlanTier);

            var reloadedRecipientSettings = await recipientEnv.Repo.GetStoreSettingsAsync();
            Assert.AreEqual("Boutique Recebedora", reloadedRecipientSettings.StoreName);
            Assert.AreEqual(StoreSettings.PlanTierPremium, reloadedRecipientSettings.GetEffectivePlanTier());
            Assert.AreEqual(universalCard.Code, reloadedRecipientSettings.ActiveGiftCardCode);
            Assert.IsTrue(reloadedRecipientSettings.IsSiteActive);
        }
        finally
        {
            CleanupDir(adminEnv.TempDir);
            CleanupDir(recipientEnv.TempDir);
        }
    }

    [TestMethod]
    public async Task SyncPlatformGiftCards_ShouldMergeRemotes_ApplyReactivation_AndNeverUnredeem()
    {
        var env = CreateEnv();
        try
        {
            var service = new EmployeeAndSettingsService(env.Repo, env.Hasher, env.Storage);

            var localPaused = PlanActivationCode.Create(StoreSettings.PlanTierIntermediate, 30, "LUXO-INTR-30D-LOCAL1");
            localPaused.Revoke();
            localPaused.StatusUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await env.Repo.SavePlanActivationCodeAsync(localPaused);

            var alreadyRedeemed = PlanActivationCode.Create(StoreSettings.PlanTierPremium, 30, "LUXO-PREM-30D-USED01");
            alreadyRedeemed.MarkRedeemed("Loja Alfa", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(30));
            await env.Repo.SavePlanActivationCodeAsync(alreadyRedeemed);

            var incoming = new List<PlatformGiftCardSyncDto>
            {
                // Newer reactivation from hub/admin should reactivate paused card
                new(
                    localPaused.Id,
                    localPaused.Code,
                    StoreSettings.PlanTierIntermediate,
                    30,
                    null,
                    null,
                    PlanActivationCode.BillingKindPaid,
                    null,
                    localPaused.CreatedAt,
                    null,
                    IsRevoked: false,
                    RedeemedAt: null,
                    RedeemedByStoreName: null,
                    RedeemedByEmployeeId: null,
                    AppliedPlanExpiresAt: null,
                    StatusUpdatedAt: DateTimeOffset.UtcNow),
                // Attempting to overwrite an already-redeemed card with unredeemed DTO must NOT unredeem it
                new(
                    alreadyRedeemed.Id,
                    alreadyRedeemed.Code,
                    StoreSettings.PlanTierPremium,
                    30,
                    null,
                    null,
                    PlanActivationCode.BillingKindPaid,
                    null,
                    alreadyRedeemed.CreatedAt,
                    null,
                    IsRevoked: false,
                    RedeemedAt: null,
                    RedeemedByStoreName: null,
                    RedeemedByEmployeeId: null,
                    AppliedPlanExpiresAt: null,
                    StatusUpdatedAt: DateTimeOffset.UtcNow),
                // Brand new universal card created remotely
                new(
                    Guid.NewGuid(),
                    "LUXO-PREM-VIT-NEWCARD",
                    StoreSettings.PlanTierPremium,
                    null,
                    null,
                    "Universal",
                    PlanActivationCode.BillingKindPaid,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    IsRevoked: false,
                    RedeemedAt: null,
                    RedeemedByStoreName: null,
                    RedeemedByEmployeeId: null,
                    AppliedPlanExpiresAt: null,
                    StatusUpdatedAt: DateTimeOffset.UtcNow)
            };

            var merged = await service.SyncPlatformGiftCardsAsync(incoming);
            Assert.AreEqual(3, merged.Count);

            var reactivated = await env.Repo.GetPlanActivationCodeByCodeAsync(localPaused.Code);
            Assert.IsNotNull(reactivated);
            Assert.IsFalse(reactivated!.IsRevoked);
            Assert.IsTrue(reactivated.CanRedeem());

            var preserved = await env.Repo.GetPlanActivationCodeByCodeAsync(alreadyRedeemed.Code);
            Assert.IsNotNull(preserved);
            Assert.IsTrue(preserved!.IsRedeemed);
            Assert.AreEqual("Loja Alfa", preserved.RedeemedByStoreName);

            var imported = await env.Repo.GetPlanActivationCodeByCodeAsync("LUXO-PREM-VIT-NEWCARD");
            Assert.IsNotNull(imported);
            Assert.IsTrue(imported!.CanRedeem());
        }
        finally
        {
            CleanupDir(env.TempDir);
        }
    }

    [TestMethod]
    public async Task AdminApiClient_ShouldCallGiftCardRedeemAndSyncEndpoints_WithExpectedPayloads()
    {
        var handler = new CapturingHttpHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/gift-cards/redeem", StringComparison.OrdinalIgnoreCase))
            {
                var grant = new PlatformGiftCardRedeemResponse(
                    "LUXO-PREM-VIT-ABCD1234",
                    StoreSettings.PlanTierPremium,
                    null,
                    null,
                    false,
                    null,
                    "Loja Destinatária",
                    "OK");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(grant) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new List<PlatformGiftCardSyncDto>())
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid") };
        var client = new AdminApiClient(http);

        var redeemed = await client.RedeemPlatformGiftCardAsync(new PlatformGiftCardRedeemRequest("LUXO-PREM-VIT-ABCD1234", "Loja Destinatária"));
        Assert.AreEqual("POST", handler.LastMethod);
        Assert.AreEqual("/api/platform-sync/gift-cards/redeem", handler.LastRequestUri?.AbsolutePath);
        Assert.AreEqual("Loja Destinatária", redeemed.RedeemedByStoreName);

        await client.SyncPlatformGiftCardsAsync([], "sync-secret-123");
        Assert.AreEqual("POST", handler.LastMethod);
        Assert.AreEqual("/api/platform-sync/gift-cards/sync", handler.LastRequestUri?.AbsolutePath);
        Assert.AreEqual("sync-secret-123", handler.LastHeaders["X-YouLink-Sync-Key"]);
    }

    [TestMethod]
    public void PlanCheckoutGuardHelper_ShouldPrompt_WhenAlreadyPremiumOrSameOrDowngrade_AndSkipForOtherStoreOrUpgrade()
    {
        var premiumLifetime = new AdminStoreSettingsDto(
            Guid.NewGuid(),
            "giovanni loja",
            "giovanni-loja",
            "5511999999999",
            "Desc",
            null,
            null,
            3,
            DateTimeOffset.UtcNow,
            PlanTier: StoreSettings.PlanTierPremium,
            PlanActivatedAt: DateTimeOffset.UtcNow.AddDays(-2),
            PlanExpiresAt: null);

        var promptLifetimeSame = PlanCheckoutGuardHelper.EvaluatePurchasePrompt(
            premiumLifetime,
            StoreDesignPresets.PlanPremium,
            "giovanni loja");
        Assert.IsTrue(promptLifetimeSame.ShouldPrompt);
        Assert.IsTrue(promptLifetimeSame.IsLifetime);
        Assert.IsTrue(promptLifetimeSame.IsSameTier);
        Assert.IsTrue(promptLifetimeSame.Headline.Contains("Premium Vitalício", StringComparison.OrdinalIgnoreCase));

        var promptLifetimeDowngrade = PlanCheckoutGuardHelper.EvaluatePurchasePrompt(
            premiumLifetime,
            StoreDesignPresets.PlanBasic,
            "giovanni loja");
        Assert.IsTrue(promptLifetimeDowngrade.ShouldPrompt);
        Assert.IsTrue(promptLifetimeDowngrade.IsDowngrade);

        var promptOtherStore = PlanCheckoutGuardHelper.EvaluatePurchasePrompt(
            premiumLifetime,
            StoreDesignPresets.PlanPremium,
            "Outra Loja Nova");
        Assert.IsFalse(promptOtherStore.ShouldPrompt);

        var basicFree = new AdminStoreSettingsDto(
            Guid.NewGuid(),
            "Loja Inicial",
            "loja-inicial",
            "5511999999999",
            "Desc",
            null,
            null,
            3,
            DateTimeOffset.UtcNow,
            PlanTier: StoreSettings.PlanTierBasic,
            PlanActivatedAt: null,
            PlanExpiresAt: null);
        Assert.IsFalse(PlanCheckoutGuardHelper.EvaluatePurchasePrompt(basicFree, StoreDesignPresets.PlanPremium, "Loja Inicial").ShouldPrompt);

        var intermediateActive = basicFree with
        {
            PlanTier = StoreSettings.PlanTierIntermediate,
            PlanActivatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            PlanExpiresAt = DateTimeOffset.UtcNow.AddDays(29)
        };
        Assert.IsFalse(PlanCheckoutGuardHelper.EvaluatePurchasePrompt(intermediateActive, StoreDesignPresets.PlanPremium, "Loja Inicial").ShouldPrompt);
        Assert.IsTrue(PlanCheckoutGuardHelper.EvaluatePurchasePrompt(intermediateActive, StoreDesignPresets.PlanIntermediate, "Loja Inicial").ShouldPrompt);

        var root = FindRepoRoot();
        var checkoutMarkup = File.ReadAllText(Path.Combine(root, "Components", "Pages", "PlanCheckoutPage.razor"), Encoding.UTF8);
        Assert.IsTrue(checkoutMarkup.Contains("_showExistingPlanModal", StringComparison.Ordinal));
        Assert.IsTrue(checkoutMarkup.Contains("PlanCheckoutGuardHelper", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AdminApiClient_CreatePlatformPlanOrderAsync_ShouldSendExplicitContentLength_AndValidJson()
    {
        long? capturedContentLength = null;
        string? capturedBody = null;

        var handler = new CapturingHttpHandler(req =>
        {
            capturedContentLength = req.Content?.Headers.ContentLength;
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            var orderDto = new PlatformSubscriptionOrderDto(
                Guid.NewGuid(), "YLK-20261114-5678", "tok", StoreSettings.PlanTierPremium,
                12, 365, 1438.80m, "giovanni loja", "giovanni-loja", "https://lojadogiovanni.duckdns.org", false,
                null, "giovanni", "giovannipioroni@gmail.com", "giovanni", "pix@dono.com", "000201...", "Pendente",
                null, null, DateTimeOffset.UtcNow, null, null, "OK");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(orderDto)
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid") };
        var client = new AdminApiClient(http);

        var created = await client.CreatePlatformPlanOrderAsync(new CreatePlatformPlanOrderRequest(
            PlanTier: StoreSettings.PlanTierPremium,
            DurationMonths: 12,
            Amount: 1438.80m,
            StoreName: "giovanni loja",
            StoreSlug: "giovanni-loja",
            DesiredDomain: "lojadogiovanni.duckdns.org",
            UseOwnDedicatedServer: false,
            BuyerUserId: Guid.NewGuid(),
            BuyerUsername: "giovanni",
            BuyerEmail: "giovannipioroni@gmail.com",
            BuyerFullName: "giovanni"));

        Assert.AreEqual("POST", handler.LastMethod);
        Assert.AreEqual("/api/platform-sync/plan-orders", handler.LastRequestUri?.AbsolutePath);
        Assert.IsTrue(capturedContentLength.HasValue && capturedContentLength.Value > 0,
            "AdminApiClient deve preencher Content-Length explicitamente para compatibilidade com receptores HTTP/1.1.");
        Assert.IsTrue(!string.IsNullOrWhiteSpace(capturedBody) && capturedBody!.Contains("giovanni loja", StringComparison.Ordinal));
        Assert.AreEqual("YLK-20261114-5678", created.OrderNumber);
    }

    [TestMethod]
    public async Task AdminApiClient_GetPlatformPlanCatalogAsync_ShouldReturnOfficialPricesWithoutExposingPixKey()
    {
        var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PlatformPlanCatalogDto(
                BasicPlanMonthlyPrice: 59.90m,
                IntermediaryPlanMonthlyPrice: 99.90m,
                PremiumPlanMonthlyPrice: 149.90m,
                HasOfficialPixKey: true))
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid") };
        var client = new AdminApiClient(http);

        var catalog = await client.GetPlatformPlanCatalogAsync();

        Assert.AreEqual("GET", handler.LastMethod);
        Assert.AreEqual("/api/platform-sync/plan-catalog", handler.LastRequestUri?.AbsolutePath);
        Assert.AreEqual(59.90m, catalog.BasicPlanMonthlyPrice);
        Assert.AreEqual(99.90m, catalog.IntermediaryPlanMonthlyPrice);
        Assert.AreEqual(149.90m, catalog.PremiumPlanMonthlyPrice);
        Assert.IsTrue(catalog.HasOfficialPixKey);

        var propNames = typeof(PlatformPlanCatalogDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.IsFalse(propNames.Contains("PicpayPixKey", StringComparer.OrdinalIgnoreCase),
            "Catálogo público de planos nunca pode expor a chave Pix do dono.");
    }

    [TestMethod]
    public async Task AdminApiClient_ShouldClassifyHub404AsEndpointUnavailable_AndMauiGatewayShouldFallbackOn404()
    {
        var json404Handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"Endpoint não encontrado no hub.\"}", Encoding.UTF8, "application/json")
        });
        using var httpJson404 = new HttpClient(json404Handler) { BaseAddress = new Uri("https://example.invalid") };
        var clientJson404 = new AdminApiClient(httpJson404);

        InvalidOperationException? json404Ex = null;
        try
        {
            await clientJson404.GetStoreSettingsAsync();
        }
        catch (InvalidOperationException ex)
        {
            json404Ex = ex;
        }
        Assert.IsNotNull(json404Ex);
        Assert.IsTrue(AdminApiClient.IsEndpointUnavailableError(json404Ex!));

        var plain404Handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpPlain404 = new HttpClient(plain404Handler) { BaseAddress = new Uri("https://example.invalid") };
        var clientPlain404 = new AdminApiClient(httpPlain404);

        InvalidOperationException? plain404Ex = null;
        try
        {
            await clientPlain404.GetStoreSettingsAsync();
        }
        catch (InvalidOperationException ex)
        {
            plain404Ex = ex;
        }
        Assert.IsNotNull(plain404Ex);
        Assert.IsTrue(AdminApiClient.IsEndpointUnavailableError(plain404Ex!));

        var badReqHandler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"Corpo JSON inválido.\"}", Encoding.UTF8, "application/json")
        });
        using var httpBadReq = new HttpClient(badReqHandler) { BaseAddress = new Uri("https://example.invalid") };
        var clientBadReq = new AdminApiClient(httpBadReq);

        InvalidOperationException? badReqEx = null;
        try
        {
            await clientBadReq.GetStoreSettingsAsync();
        }
        catch (InvalidOperationException ex)
        {
            badReqEx = ex;
        }
        Assert.IsNotNull(badReqEx);
        Assert.IsFalse(AdminApiClient.IsEndpointUnavailableError(badReqEx!));

        var root = FindRepoRoot();
        var gatewaySource = File.ReadAllText(Path.Combine(root, "Services", "MauiAdminGateway.cs"), Encoding.UTF8);
        Assert.IsTrue(gatewaySource.Contains("IsEndpointUnavailableOnHubError", StringComparison.Ordinal));
        Assert.IsTrue(gatewaySource.Contains("GetPlatformPlanCatalogAsync", StringComparison.Ordinal));
    }
}
