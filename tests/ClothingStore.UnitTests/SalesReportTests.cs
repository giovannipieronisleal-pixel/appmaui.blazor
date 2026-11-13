using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class SalesReportTests
{
    private static (ClothingStoreRepository Repo, string TempDir, string DbPath, Pbkdf2PasswordHasher Hasher, SupabaseImageStorageService Storage) CreateIsolatedRepo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_sales_tests_{Guid.NewGuid():N}");
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

    private static async Task<(Guid AdminEmpId, Product ProductA, ProductVariant VarA, Product ProductB, ProductVariant VarB)> SeedTwoProductsAsync(
        ClothingStoreRepository repo,
        SupabaseImageStorageService storage)
    {
        var employees = await repo.GetEmployeesAsync();
        var adminEmpId = employees[0].Id;

        var catService = new ProductManagementService(repo, storage);
        var categories = await catService.GetAllCategoriesAsync();
        var catId = categories.Count > 0
            ? categories[0].Id
            : (await catService.CreateCategoryAsync(new SaveCategoryRequest { Name = "Roupas Premium" })).Id;

        var prodA = new Product(catId, "Vestido Linho Atelier", "Vestido longo leve", 199.90m, true);
        var varA = prodA.AddVariant("Bege", "M", "#D6C5B3", "VST-BGE-M", 2);
        var movA = varA.ApplyStockMovement(adminEmpId, StockMovementType.In, 10, "Estoque inicial A");
        await repo.AddProductAsync(prodA);
        await repo.RecordStockMovementAsync(varA, movA);

        var prodB = new Product(catId, "Camisa Oxford Clássica", "Algodão egípcio", 120.00m, true);
        var varB = prodB.AddVariant("Branco", "G", "#FFFFFF", "CAM-BRC-G", 2);
        var movB = varB.ApplyStockMovement(adminEmpId, StockMovementType.In, 8, "Estoque inicial B");
        await repo.AddProductAsync(prodB);
        await repo.RecordStockMovementAsync(varB, movB);

        return (adminEmpId, prodA, varA, prodB, varB);
    }

    private static async Task UpgradePlanAsync(
        ClothingStoreRepository repo,
        Pbkdf2PasswordHasher hasher,
        SupabaseImageStorageService storage,
        Guid adminEmpId,
        string planTier)
    {
        var settingsService = new EmployeeAndSettingsService(repo, hasher, storage);
        await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
        {
            StoreName = "Boutique YouLink",
            StoreSlug = "boutique-youlink",
            WhatsappNumber = "5511988887777",
            StoreDescription = "Moda autoral",
            PlanTier = planTier
        });
    }

    [TestMethod]
    public async Task BasicPlan_SalesReportIsLocked_ZeroLeak_AndBlocksManualSale()
    {
        var (repo, tempDir, _, _, storage) = CreateIsolatedRepo();
        try
        {
            var (adminEmpId, prodA, varA, _, _) = await SeedTwoProductsAsync(repo, storage);
            var settings = await repo.GetStoreSettingsAsync();
            Assert.IsFalse(settings.CanViewSalesAnalytics());

            // Mesmo existindo pedido salvo no banco, Plano Básico nunca vaza faturamento nem permite venda manual
            var confirmedOrder = StoreOrder.CreateManualPaidSale(
                Guid.NewGuid(),
                "VND-0001",
                prodA,
                varA,
                2,
                "Carla Dias");
            await repo.SaveOrderAsync(confirmedOrder);

            var orderService = new OrderAndShippingService(repo);
            var report = await orderService.GetSalesReportAsync("all");

            Assert.IsFalse(report.IsUnlocked);
            Assert.AreEqual(StoreSettings.PlanTierBasic, report.EffectivePlanTier);
            Assert.AreEqual(0m, report.TotalConfirmedRevenue);
            Assert.AreEqual(0m, report.TodayConfirmedRevenue);
            Assert.AreEqual(0, report.ConfirmedOrdersCount);
            Assert.AreEqual(0, report.TopProducts.Count);
            Assert.AreEqual(0, report.DailySeries.Count);
            Assert.AreEqual(0, report.RecentConfirmedSales.Count);

            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await orderService.RegisterManualSaleAsync(
                    adminEmpId,
                    new RegisterManualSaleRequest(prodA.Id, varA.Id, 1, "Cliente Teste")));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task IntermediatePlan_UnlocksSalesReport_CalculatesConfirmedExcludesAwaitingAndCancelled()
    {
        var (repo, tempDir, _, hasher, storage) = CreateIsolatedRepo();
        try
        {
            var (adminEmpId, prodA, varA, prodB, varB) = await SeedTwoProductsAsync(repo, storage);
            await UpgradePlanAsync(repo, hasher, storage, adminEmpId, StoreSettings.PlanTierIntermediate);

            var nowBrazil = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));

            // 1. Venda confirmada #1 (Vestido x1 = 199.90)
            var sale1 = StoreOrder.CreateManualPaidSale(
                Guid.NewGuid(),
                "VND-0001",
                prodA,
                varA,
                1,
                "Ana Clara",
                "11977771111",
                StoreOrder.PaymentMethodPixOnly,
                agreedShippingCost: null,
                sellerNotes: null,
                soldAt: nowBrazil);
            sale1.Status = StoreOrder.StatusPaidPreparing;
            await repo.SaveOrderAsync(sale1);

            // 2. Venda confirmada #2 (Camisa x2 = 240.00 + frete 20.00 = 260.00)
            var sale2 = StoreOrder.CreateManualPaidSale(
                Guid.NewGuid(),
                "VND-0002",
                prodB,
                varB,
                2,
                "Bruno Silva",
                "11966662222",
                StoreOrder.PaymentMethodWhatsapp,
                agreedShippingCost: 20.00m,
                sellerNotes: "Motoboy",
                soldAt: nowBrazil);
            await repo.SaveOrderAsync(sale2);

            // 3. Pedido aguardando Pix (199.90 — não deve somar no confirmado)
            var awaiting = StoreOrder.CreatePixOrder(
                Guid.NewGuid(),
                "YLK-0003",
                prodA,
                varA,
                1,
                "Lucas Mendes",
                "11955553333",
                "01310-100",
                "Av Paulista",
                "100",
                null,
                "Bela Vista",
                "São Paulo",
                "SP",
                null,
                3.2,
                "Mesma cidade",
                21.00m,
                2,
                4,
                32.00m,
                1,
                2,
                "pix@boutique.com",
                "000201...");
            await repo.SaveOrderAsync(awaiting);

            // 4. Pedido cancelado (120.00 — não deve somar no confirmado)
            var cancelled = StoreOrder.CreatePixOrder(
                Guid.NewGuid(),
                "YLK-0004",
                prodB,
                varB,
                1,
                "Fernanda Costa",
                "11944440000",
                "20040-020",
                "Rua do Ouvidor",
                "50",
                null,
                "Centro",
                "Rio de Janeiro",
                "RJ",
                null,
                410.0,
                "Nacional",
                28.00m,
                4,
                8,
                52.00m,
                1,
                3,
                "pix@boutique.com",
                "000201...");
            cancelled.Cancel("Desistência");
            await repo.SaveOrderAsync(cancelled);

            var orderService = new OrderAndShippingService(repo);
            var report = await orderService.GetSalesReportAsync("30d");

            Assert.IsTrue(report.IsUnlocked);
            Assert.AreEqual(StoreSettings.PlanTierIntermediate, report.EffectivePlanTier);
            Assert.AreEqual(459.90m, report.TotalConfirmedRevenue);
            Assert.AreEqual(459.90m, report.TodayConfirmedRevenue);
            Assert.AreEqual(459.90m, report.SelectedPeriodConfirmedRevenue);
            Assert.AreEqual(2, report.ConfirmedOrdersCount);
            Assert.AreEqual(2, report.SelectedPeriodOrdersCount);
            Assert.AreEqual(3, report.TotalUnitsSold);
            Assert.AreEqual(229.95m, report.AverageTicket);
            Assert.AreEqual(199.90m, report.AwaitingPixRevenue);
            Assert.AreEqual(1, report.AwaitingPixOrdersCount);
            Assert.AreEqual(120.00m, report.CancelledRevenue);
            Assert.AreEqual(1, report.CancelledOrdersCount);

            // Top ranking: Camisa Oxford teve 240.00 (> Vestido 199.90)
            Assert.AreEqual(2, report.TopProducts.Count);
            Assert.AreEqual(prodB.Id, report.TopProducts[0].ProductId);
            Assert.AreEqual(2, report.TopProducts[0].UnitsSold);
            Assert.AreEqual(240.00m, report.TopProducts[0].ConfirmedRevenue);
            Assert.AreEqual(prodA.Id, report.TopProducts[1].ProductId);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task PremiumPlan_PeriodFilters_SeparateToday_7d_30d_And_AllTime()
    {
        var (repo, tempDir, _, hasher, storage) = CreateIsolatedRepo();
        try
        {
            var (adminEmpId, _, _, prodB, varB) = await SeedTwoProductsAsync(repo, storage);
            await UpgradePlanAsync(repo, hasher, storage, adminEmpId, StoreSettings.PlanTierPremium);

            var nowBrazil = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));

            async Task SaveDatedSaleAsync(string num, int daysAgo)
            {
                var sale = StoreOrder.CreateManualPaidSale(
                    Guid.NewGuid(),
                    num,
                    prodB,
                    varB,
                    1,
                    $"Cliente {num}",
                    soldAt: nowBrazil.AddDays(-daysAgo));
                await repo.SaveOrderAsync(sale);
            }

            await SaveDatedSaleAsync("VND-0001", 0);   // Hoje: 120
            await SaveDatedSaleAsync("VND-0002", 4);   // 4d: 120
            await SaveDatedSaleAsync("VND-0003", 18);  // 18d: 120
            await SaveDatedSaleAsync("VND-0004", 45);  // 45d: 120

            var orderService = new OrderAndShippingService(repo);

            var today = await orderService.GetSalesReportAsync("today");
            Assert.IsTrue(today.IsUnlocked);
            Assert.AreEqual(120.00m, today.SelectedPeriodConfirmedRevenue);
            Assert.AreEqual(1, today.SelectedPeriodOrdersCount);
            Assert.AreEqual(1, today.DailySeries.Count);

            var sevenDays = await orderService.GetSalesReportAsync("7d");
            Assert.AreEqual(240.00m, sevenDays.SelectedPeriodConfirmedRevenue);
            Assert.AreEqual(2, sevenDays.SelectedPeriodOrdersCount);
            Assert.AreEqual(7, sevenDays.DailySeries.Count);

            var thirtyDays = await orderService.GetSalesReportAsync("30d");
            Assert.AreEqual(360.00m, thirtyDays.SelectedPeriodConfirmedRevenue);
            Assert.AreEqual(3, thirtyDays.SelectedPeriodOrdersCount);
            Assert.AreEqual(30, thirtyDays.DailySeries.Count);

            var all = await orderService.GetSalesReportAsync("all");
            Assert.AreEqual(480.00m, all.SelectedPeriodConfirmedRevenue);
            Assert.AreEqual(480.00m, all.TotalConfirmedRevenue);
            Assert.AreEqual(120.00m, all.TodayConfirmedRevenue);
            Assert.AreEqual(240.00m, all.Last7DaysConfirmedRevenue);
            Assert.AreEqual(360.00m, all.Last30DaysConfirmedRevenue);
            Assert.AreEqual(4, all.SelectedPeriodOrdersCount);
            Assert.AreEqual(4, all.TotalUnitsSold);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task RegisterManualSale_DeductsStock_PersistsAcrossReload_AndPreventsOversell()
    {
        var (repo, tempDir, dbPath, hasher, storage) = CreateIsolatedRepo();
        try
        {
            var (adminEmpId, prodA, varA, _, _) = await SeedTwoProductsAsync(repo, storage);
            await UpgradePlanAsync(repo, hasher, storage, adminEmpId, StoreSettings.PlanTierIntermediate);

            var orderService = new OrderAndShippingService(repo);
            var registered = await orderService.RegisterManualSaleAsync(
                adminEmpId,
                new RegisterManualSaleRequest(
                    ProductId: prodA.Id,
                    ProductVariantId: varA.Id,
                    Quantity: 3,
                    CustomerName: "Mariana Lopes",
                    CustomerWhatsapp: "(11) 98888-4444",
                    PaymentMethod: "WhatsApp",
                    AgreedShippingCost: 15.00m,
                    SellerNotes: "Retirada na loja"));

            Assert.AreEqual(StoreOrder.StatusDelivered, registered.Status);
            Assert.IsTrue(registered.StockDeducted);
            Assert.AreEqual("WhatsApp", registered.PaymentMethod);
            // 3 * 199.90 + 15.00 = 614.70
            Assert.AreEqual(614.70m, registered.TotalPixAmount);

            // Recarrega repositório do zero para comprovar persistência real em disco
            var reloadedRepo = new ClothingStoreRepository(hasher, dbPath);
            var reloadedProd = await reloadedRepo.GetProductBySlugOrIdAsync(prodA.Id.ToString());
            Assert.IsNotNull(reloadedProd);
            Assert.AreEqual(7, reloadedProd!.Variants.First(v => v.Id == varA.Id).StockQuantity);

            var reloadedOrderService = new OrderAndShippingService(reloadedRepo);
            var report = await reloadedOrderService.GetSalesReportAsync("today");
            Assert.IsTrue(report.IsUnlocked);
            Assert.AreEqual(614.70m, report.TodayConfirmedRevenue);
            Assert.AreEqual(3, report.TotalUnitsSold);
            Assert.AreEqual(1, report.RecentConfirmedSales.Count);

            // Impede vender acima do estoque restante (7 unidades)
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await reloadedOrderService.RegisterManualSaleAsync(
                    adminEmpId,
                    new RegisterManualSaleRequest(prodA.Id, varA.Id, 8, "Excesso")));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }
}
