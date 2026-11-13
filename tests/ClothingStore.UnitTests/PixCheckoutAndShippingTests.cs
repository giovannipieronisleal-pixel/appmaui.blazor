using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
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
public class PixCheckoutAndShippingTests
{
    private static (ClothingStoreRepository Repo, string TempDir, string DbPath, Pbkdf2PasswordHasher Hasher, SupabaseImageStorageService Storage) CreateIsolatedRepo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_pix_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "db.json");
        var hasher = new Pbkdf2PasswordHasher();
        var repo = new ClothingStoreRepository(hasher, dbPath);
        var config = new ConfigurationBuilder().Build();
        var storage = new SupabaseImageStorageService(new HttpClient(), config, Path.Combine(tempDir, "wwwroot"));
        return (repo, tempDir, dbPath, hasher, storage);
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

    private static async Task<(Guid AdminEmpId, Product Product, ProductVariant Variant)> SeedCatalogProductAsync(
        ClothingStoreRepository repo,
        SupabaseImageStorageService storage,
        decimal price = 159.90m,
        int stock = 5)
    {
        var employees = await repo.GetEmployeesAsync();
        Guid adminEmpId;
        if (employees.Count > 0)
        {
            adminEmpId = employees[0].Id;
        }
        else
        {
            var jwtConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!" })
                .Build();
            var auth = new AuthService(repo, new Pbkdf2PasswordHasher(), new JwtTokenService(jwtConfig));
            var created = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Dona Pix",
                "dona.pix@youlink.app",
                "1234",
                Username: "dona.pix"));
            adminEmpId = created.Employee.EmployeeId;
        }

        var catService = new ProductManagementService(repo, storage);
        var categories = await catService.GetAllCategoriesAsync();
        var catId = categories.Count > 0
            ? categories[0].Id
            : (await catService.CreateCategoryAsync(new SaveCategoryRequest { Name = "Moda Feminina" })).Id;

        var product = new Product(catId, "Jaqueta Corta-Vento Linho", "Peça leve com acabamento premium", price, true);
        var variant = product.AddVariant("Preto", "M", "#18181B", "JAC-PRT-M", 2);
        var movement = variant.ApplyStockMovement(adminEmpId, StockMovementType.In, stock, "Estoque inicial");
        await repo.AddProductAsync(product);
        await repo.RecordStockMovementAsync(variant, movement);

        return (adminEmpId, product, variant);
    }

    [TestMethod]
    public async Task BasicPlan_CannotEnableDirectPixSales_AndBlocksOrders()
    {
        var (repo, tempDir, _, _, storage) = CreateIsolatedRepo();
        try
        {
            var (_, product, variant) = await SeedCatalogProductAsync(repo, storage);
            var settings = await repo.GetStoreSettingsAsync();
            Assert.AreEqual(StoreSettings.PlanTierBasic, settings.GetEffectivePlanTier());
            Assert.IsFalse(settings.DirectPixSalesEnabled);
            Assert.IsFalse(settings.IsDirectPixCheckoutReady());

            Assert.ThrowsException<DomainException>(() =>
                settings.ConfigurePixAndShipping(
                    directPixSalesEnabled: true,
                    storePixKey: "vendas@minhaloja.com.br",
                    storePixReceiverName: "Minha Loja",
                    originCep: "01310-100",
                    originCity: "São Paulo",
                    originState: "SP",
                    deliveryScopeMode: BrazilShippingCalculator.DeliveryScopeModes.AllBrazil,
                    maxDeliveryDistanceKm: 0,
                    defaultPackageWeightGrams: 450));

            var orderService = new OrderAndShippingService(repo);
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderService.PlacePixOrderAsync(new CreatePixOrderRequest
                {
                    ProductId = product.Id,
                    ProductVariantId = variant.Id,
                    Quantity = 1,
                    CustomerName = "Maria Silva",
                    CustomerWhatsapp = "11988887777",
                    Cep = "01310-100",
                    Street = "Av Paulista",
                    Number = "1000",
                    Neighborhood = "Bela Vista",
                    City = "São Paulo",
                    State = "SP"
                }));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task IntermediateAndPremiumPlans_RequireExplicitActivation_AndValidPixAndCep()
    {
        var (repo, tempDir, _, hasher, storage) = CreateIsolatedRepo();
        try
        {
            var (adminEmpId, product, variant) = await SeedCatalogProductAsync(repo, storage);
            var settingsService = new EmployeeAndSettingsService(repo, hasher, storage);

            // Atualiza para Meia Versão (Intermediário), mas ainda NÃO ativa as vendas por Pix
            var updated = await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = "Atelier Paulista",
                StoreSlug = "atelier-paulista",
                WhatsappNumber = "5511999998888",
                StoreDescription = "Catálogo digital oficial",
                PlanTier = StoreSettings.PlanTierIntermediate
            });

            Assert.IsFalse(updated.DirectPixSalesEnabled);
            Assert.IsFalse(updated.DirectPixSalesReady);

            var orderService = new OrderAndShippingService(repo);
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderService.PlacePixOrderAsync(new CreatePixOrderRequest
                {
                    ProductId = product.Id,
                    ProductVariantId = variant.Id,
                    Quantity = 1,
                    CustomerName = "João Souza",
                    CustomerWhatsapp = "11977776666",
                    Cep = "01310-100",
                    Street = "Rua Augusta",
                    Number = "500",
                    Neighborhood = "Consolação",
                    City = "São Paulo",
                    State = "SP"
                }));

            // Tentar ativar sem chave Pix válida falha
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
                {
                    StoreName = "Atelier Paulista",
                    StoreSlug = "atelier-paulista",
                    WhatsappNumber = "5511999998888",
                    StoreDescription = "Catálogo digital oficial",
                    DirectPixSalesEnabled = true,
                    StorePixKey = "123",
                    OriginCep = "01310-100"
                }));

            // Ativação explícita completa na Meia Versão funciona
            var activated = await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = "Atelier Paulista",
                StoreSlug = "atelier-paulista",
                WhatsappNumber = "5511999998888",
                StoreDescription = "Catálogo digital oficial",
                DirectPixSalesEnabled = true,
                StorePixKey = "pix@atelierpaulista.com.br",
                StorePixReceiverName = "Atelier Paulista",
                OriginCep = "01310-100",
                OriginCity = "São Paulo",
                OriginState = "SP",
                DeliveryScopeMode = BrazilShippingCalculator.DeliveryScopeModes.RadiusKm,
                MaxDeliveryDistanceKm = 150,
                DefaultPackageWeightGrams = 500
            });

            Assert.IsTrue(activated.DirectPixSalesEnabled);
            Assert.IsTrue(activated.DirectPixSalesReady);
            Assert.AreEqual("01310-100", activated.OriginCep);

            // Também permanece pronto na Premium
            var premium = await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = "Atelier Paulista",
                StoreSlug = "atelier-paulista",
                WhatsappNumber = "5511999998888",
                StoreDescription = "Catálogo digital oficial",
                PlanTier = StoreSettings.PlanTierPremium
            });
            Assert.IsTrue(premium.DirectPixSalesReady);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void SellerDeliveryLimits_EnforceSameCity_SameState_And_RadiusKm()
    {
        // Origem: São Paulo Capital (01310-100)
        var sameCityOk = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            "01310-100", "São Paulo", "SP",
            BrazilShippingCalculator.DeliveryScopeModes.SameCity, 0,
            "04538-132", "São Paulo", "SP");
        Assert.IsTrue(sameCityOk.IsAllowed);

        var sameCityFarRejected = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            "01310-100", "São Paulo", "SP",
            BrazilShippingCalculator.DeliveryScopeModes.SameCity, 0,
            "13010-111", "Campinas", "SP");
        Assert.IsFalse(sameCityFarRejected.IsAllowed);

        var sameStateOk = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            "01310-100", "São Paulo", "SP",
            BrazilShippingCalculator.DeliveryScopeModes.SameState, 0,
            "13010-111", "Campinas", "SP");
        Assert.IsTrue(sameStateOk.IsAllowed);

        var sameStateOtherUfRejected = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            "01310-100", "São Paulo", "SP",
            BrazilShippingCalculator.DeliveryScopeModes.SameState, 0,
            "20040-020", "Rio de Janeiro", "RJ");
        Assert.IsFalse(sameStateOtherUfRejected.IsAllowed);

        // Raio 120 km: Campinas (~85 km) entra; Belo Horizonte (~490 km) bloqueia
        var radiusCampinasOk = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            "01310-100", "São Paulo", "SP",
            BrazilShippingCalculator.DeliveryScopeModes.RadiusKm, 120,
            "13010-111", "Campinas", "SP");
        Assert.IsTrue(radiusCampinasOk.IsAllowed);

        var radiusBhRejected = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            "01310-100", "São Paulo", "SP",
            BrazilShippingCalculator.DeliveryScopeModes.RadiusKm, 120,
            "30112-000", "Belo Horizonte", "MG");
        Assert.IsFalse(radiusBhRejected.IsAllowed);
        Assert.IsTrue(radiusBhRejected.DistanceKm > 120);

        // Todo o Brasil aceita Manaus
        var allBrazilOk = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            "01310-100", "São Paulo", "SP",
            BrazilShippingCalculator.DeliveryScopeModes.AllBrazil, 0,
            "69005-010", "Manaus", "AM");
        Assert.IsTrue(allBrazilOk.IsAllowed);
    }

    [TestMethod]
    public void CorreiosCalculator_ReturnsRealisticPacAndSedex_WithWeightAndCubicRules()
    {
        var localQuote = BrazilShippingCalculator.EstimateCorreios("01310-100", "01311-200", weightGrams: 450);
        var interstateQuote = BrazilShippingCalculator.EstimateCorreios("01310-100", "20040-020", weightGrams: 450);
        var northQuote = BrazilShippingCalculator.EstimateCorreios("01310-100", "69005-010", weightGrams: 450);
        var heavyQuote = BrazilShippingCalculator.EstimateCorreios("01310-100", "20040-020", weightGrams: 4000);
        var bulkyBoxQuote = BrazilShippingCalculator.EstimateCorreios("01310-100", "20040-020", weightGrams: 450, lengthCm: 50, widthCm: 40, heightCm: 30);

        Assert.IsTrue(localQuote.PacPrice > 0 && localQuote.SedexPrice >= localQuote.PacPrice);
        Assert.IsTrue(localQuote.PacPrice < interstateQuote.PacPrice);
        Assert.IsTrue(interstateQuote.SedexPrice < northQuote.SedexPrice);
        Assert.IsTrue(heavyQuote.PacPrice > interstateQuote.PacPrice);
        Assert.AreEqual(10000, bulkyBoxQuote.CubicWeightGrams);
        Assert.AreEqual(10000, bulkyBoxQuote.BillableWeightGrams);
        Assert.IsTrue(bulkyBoxQuote.SedexPrice > interstateQuote.SedexPrice);
    }

    [TestMethod]
    public void PixPayloadBuilder_GeneratesValidEmvCrc16_ForAllBrazilianKeyTypes()
    {
        var keys = new[]
        {
            "loja@exemplo.com.br",
            "123.456.789-00",
            "(11) 99876-5432",
            "d345e123-4567-489a-bcde-1234567890ab"
        };

        foreach (var rawKey in keys)
        {
            Assert.IsTrue(PixPayloadBuilder.IsValidPixKey(rawKey), $"Chave deveria ser válida: {rawKey}");
            var brCode = PixPayloadBuilder.BuildBrCode(
                rawKey,
                "Atelier São João Moda",
                "São Paulo",
                199.90m,
                "YL20261112AB12");

            Assert.IsTrue(brCode.Contains("BR.GOV.BCB.PIX", StringComparison.Ordinal));
            Assert.IsTrue(brCode.Contains("53039865406199.905802BR", StringComparison.Ordinal));
            Assert.IsTrue(PixPayloadBuilder.VerifyCrc16Ccitt(brCode), $"CRC-16 deveria validar para chave {rawKey}");

            // Alterar 1 dígito do código invalida o CRC
            var corrupted = brCode[..^1] + (brCode[^1] == '0' ? '1' : '0');
            Assert.IsFalse(PixPayloadBuilder.VerifyCrc16Ccitt(corrupted));
        }
    }

    [TestMethod]
    public async Task EndToEnd_PixOrderLifecycle_DistanceValidation_ConfirmPix_Stock_Fulfillment_And_Persistence()
    {
        var (repo, tempDir, dbPath, hasher, storage) = CreateIsolatedRepo();
        try
        {
            var (adminEmpId, product, variant) = await SeedCatalogProductAsync(repo, storage, price: 120.00m, stock: 5);
            var settingsService = new EmployeeAndSettingsService(repo, hasher, storage);

            await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = "YouLink Modas",
                StoreSlug = "youlink-modas",
                WhatsappNumber = "5511999990000",
                StoreDescription = "Roupas selecionadas",
                PlanTier = StoreSettings.PlanTierIntermediate,
                DirectPixSalesEnabled = true,
                StorePixKey = "11999990000",
                StorePixReceiverName = "YouLink Modas",
                OriginCep = "01310-100",
                OriginCity = "São Paulo",
                OriginState = "SP",
                DeliveryScopeMode = BrazilShippingCalculator.DeliveryScopeModes.RadiusKm,
                MaxDeliveryDistanceKm = 500,
                DefaultPackageWeightGrams = 450
            });

            var orderService = new OrderAndShippingService(repo);

            // Cotação fora do raio (Manaus > 500 km) deve rejeitar pedido
            var manausQuote = await orderService.QuoteShippingForBuyerAsync(new ShippingQuoteRequest
            {
                DestinationCep = "69005-010"
            });
            Assert.IsTrue(manausQuote.DirectPixSalesReady);
            Assert.IsFalse(manausQuote.Eligibility.IsAllowed);

            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderService.PlacePixOrderAsync(new CreatePixOrderRequest
                {
                    ProductId = product.Id,
                    ProductVariantId = variant.Id,
                    Quantity = 1,
                    CustomerName = "Comprador Manaus",
                    CustomerWhatsapp = "92988887777",
                    Cep = "69005-010",
                    Street = "Av Eduardo Ribeiro",
                    Number = "100",
                    Neighborhood = "Centro",
                    City = "Manaus",
                    State = "AM"
                }));

            // Pedido dentro do raio (Rio de Janeiro ~358 km <= 500 km) passa e gera Pix apenas
            var order = await orderService.PlacePixOrderAsync(new CreatePixOrderRequest
            {
                ProductId = product.Id,
                ProductVariantId = variant.Id,
                Quantity = 2,
                CustomerName = "Ana Beatriz Costa",
                CustomerWhatsapp = "21988881122",
                Cep = "20040-020",
                Street = "Av Rio Branco",
                Number = "156",
                Complement = "Sala 402",
                Neighborhood = "Centro",
                City = "Rio de Janeiro",
                State = "RJ",
                CustomerNotes = "Mandar por SEDEX se possível"
            });

            Assert.AreEqual(StoreOrder.PaymentMethodPixOnly, order.PaymentMethod);
            Assert.AreEqual(BrazilShippingCalculator.OrderStatuses.AwaitingPix, order.Status);
            Assert.AreEqual(240.00m, order.TotalPixAmount);
            Assert.IsTrue(order.EstimatedCorreiosPac > 0);
            Assert.IsTrue(order.EstimatedCorreiosSedex > order.EstimatedCorreiosPac);
            Assert.IsTrue(order.DistanceKmFromSeller > 300 && order.DistanceKmFromSeller < 450);
            Assert.IsTrue(PixPayloadBuilder.VerifyCrc16Ccitt(order.PixBrCode));
            Assert.IsFalse(order.StockDeducted);

            // Verifica persistência em nova instância do repositório
            var reloadedRepo = new ClothingStoreRepository(new Pbkdf2PasswordHasher(), dbPath);
            var reloadedOrderService = new OrderAndShippingService(reloadedRepo);
            var persistedOrders = await reloadedOrderService.GetOrdersAsync();
            Assert.AreEqual(1, persistedOrders.Count);
            Assert.AreEqual(order.OrderNumber, persistedOrders[0].OrderNumber);

            // Vendedor confirma pagamento Pix -> estoque cai de 5 para 3
            var paidOrder = await reloadedOrderService.ConfirmPixPaymentAsync(order.Id, adminEmpId);
            Assert.AreEqual(BrazilShippingCalculator.OrderStatuses.PaidPreparing, paidOrder.Status);
            Assert.IsTrue(paidOrder.StockDeducted);

            var updatedVariant = await reloadedRepo.GetVariantByIdAsync(variant.Id);
            Assert.IsNotNull(updatedVariant);
            Assert.AreEqual(3, updatedVariant!.StockQuantity);

            // Vendedor escolhe forma de envio Correios SEDEX e marca como Enviado
            var shippedOrder = await reloadedOrderService.UpdateOrderFulfillmentAsync(order.Id, new UpdateOrderFulfillmentRequest
            {
                FulfillmentMethod = BrazilShippingCalculator.FulfillmentMethods.CorreiosSedex,
                TrackingCode = "BR987654321BR",
                AgreedShippingCost = order.EstimatedCorreiosSedex,
                SellerNotes = "Postado na agência central",
                MarkAsShipped = true
            });

            Assert.AreEqual(BrazilShippingCalculator.OrderStatuses.Shipped, shippedOrder.Status);
            Assert.AreEqual("Correios SEDEX", shippedOrder.FulfillmentMethod);
            Assert.AreEqual("BR987654321BR", shippedOrder.TrackingCode);
            Assert.IsNotNull(shippedOrder.ShippedAt);

            // Cancelar pedido após baixa de estoque devolve as 2 peças para o estoque (3 -> 5)
            var cancelledOrder = await reloadedOrderService.CancelOrderAsync(order.Id, adminEmpId, "Cliente desistiu");
            Assert.AreEqual(BrazilShippingCalculator.OrderStatuses.Cancelled, cancelledOrder.Status);
            Assert.IsFalse(cancelledOrder.StockDeducted);

            var restockedVariant = await reloadedRepo.GetVariantByIdAsync(variant.Id);
            Assert.AreEqual(5, restockedVariant!.StockQuantity);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }
}
