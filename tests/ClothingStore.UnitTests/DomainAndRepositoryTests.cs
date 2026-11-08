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
public class DomainAndRepositoryTests
{
    [TestMethod]
    public void ProductVariant_StockMovements_ShouldCalculateBalancesAndPreventNegativeStock()
    {
        var product = new Product(Guid.NewGuid(), "Camiseta Básica", "Malha 100% algodão", 129.90m);
        var variant = product.AddVariant("Preto", "M", "#000000", "CAM-BAS-PRE-M", lowStockThreshold: 3);
        var employeeId = Guid.NewGuid();

        Assert.AreEqual(0, variant.StockQuantity);
        Assert.IsTrue(variant.IsOutOfStock);

        // Entrada de 5 unidades
        var movIn = variant.ApplyStockMovement(employeeId, StockMovementType.In, 5, "Reposição inicial");
        Assert.AreEqual(0, movIn.PreviousQuantity);
        Assert.AreEqual(5, movIn.NewQuantity);
        Assert.AreEqual(5, variant.StockQuantity);
        Assert.IsFalse(variant.IsLowStock);
        Assert.IsFalse(variant.IsOutOfStock);

        // Saída de 3 unidades (resta 2 -> entra em alerta de estoque baixo <= 3)
        var movOut = variant.ApplyStockMovement(employeeId, StockMovementType.Out, 3, "Venda balcão");
        Assert.AreEqual(5, movOut.PreviousQuantity);
        Assert.AreEqual(2, movOut.NewQuantity);
        Assert.AreEqual(2, variant.StockQuantity);
        Assert.IsTrue(variant.IsLowStock);

        // Tentativa de saída maior que o saldo deve lançar DomainException
        Assert.ThrowsException<DomainException>(() =>
            variant.ApplyStockMovement(employeeId, StockMovementType.Out, 5, "Saída inválida"));

        // Ajuste de inventário para 10 unidades
        var movAdj = variant.ApplyStockMovement(employeeId, StockMovementType.Adjustment, 10, "Contagem de inventário");
        Assert.AreEqual(2, movAdj.PreviousQuantity);
        Assert.AreEqual(10, movAdj.NewQuantity);
        Assert.AreEqual(8, movAdj.QuantityChanged);
        Assert.AreEqual(10, variant.StockQuantity);
    }

    [TestMethod]
    public void Product_AddVariant_ShouldRejectDuplicateColorAndSizeCombination()
    {
        var product = new Product(Guid.NewGuid(), "Camiseta Básica", "Descrição", 99.90m);
        product.AddVariant("Preto", "P");

        Assert.ThrowsException<DomainException>(() =>
            product.AddVariant("preto", "p"));
    }

    [TestMethod]
    public void StoreSettings_And_WhatsAppLinkBuilder_ShouldFormatOfficialWaMeUrlCorrectly()
    {
        var settings = new StoreSettings(
            "Atelier Moda",
            "+55 (11) 98765-4321",
            "Loja de roupas minimalista");

        Assert.AreEqual("5511987654321", settings.WhatsappNumber);
        Assert.AreEqual("atelier-moda", settings.StoreSlug);
        Assert.AreEqual("http://localhost:5190/loja/atelier-moda", settings.BuildStoreCatalogUrl("http://localhost:5190"));

        var message = WhatsAppLinkBuilder.BuildProductConsultationMessage("Camiseta Básica", "Preta", "M");
        Assert.AreEqual(
            "Olá! Tenho interesse no produto Camiseta Básica, na cor preta e tamanho M. Gostaria de saber mais informações.",
            message);

        var url = settings.BuildWhatsAppConsultationUrl("Camiseta Básica", "Preta", "M");
        Assert.IsTrue(url.StartsWith("https://wa.me/5511987654321?text=", StringComparison.Ordinal));
        Assert.IsTrue(url.Contains("Camiseta%20B%C3%A1sica", StringComparison.Ordinal));
        Assert.IsTrue(url.Contains("cor%20preta%20e%20tamanho%20M", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Employee_Permissions_And_Deactivation_ShouldEnforceRules()
    {
        var user = new User("vendedor@loja.com", "hash123", Guid.NewGuid());
        var employee = new Employee(user.Id, "João Silva", "Vendedor", "5511999999999", canManageProducts: false, canManageStock: true)
        {
            User = user
        };

        Assert.IsFalse(employee.HasProductManagementPermission(RoleNames.Employee));
        Assert.IsTrue(employee.HasStockManagementPermission(RoleNames.Employee));
        Assert.IsTrue(employee.HasProductManagementPermission(RoleNames.Admin));

        employee.SetActiveStatus(false);
        Assert.IsFalse(employee.IsActive);
        Assert.IsFalse(user.IsActive);
        Assert.IsFalse(employee.HasStockManagementPermission(RoleNames.Admin));
    }

    [TestMethod]
    public async Task Repository_InitialSeed_ShouldIncludeCamisetaBasicaVariantsAndStoreSettings()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_test_{Guid.NewGuid():N}.json");
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, tempFile);

            var products = await repo.GetProductsAsync();
            var camiseta = products.FirstOrDefault(p => p.Name == "Camiseta Básica");
            Assert.IsNotNull(camiseta);
            Assert.AreEqual(4, camiseta!.Variants.Count);
            Assert.AreEqual(18, camiseta.TotalStockQuantity); // 3 + 5 + 2 + 8

            var settings = await repo.GetStoreSettingsAsync();
            Assert.IsFalse(string.IsNullOrWhiteSpace(settings.StoreName));
            Assert.AreEqual("5511999999999", settings.WhatsappNumber);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public async Task ApplicationServices_EndToEnd_Auth_Catalog_Stock_And_StoreSettingsSync_ShouldWork()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_e2e_{Guid.NewGuid():N}.json");
        var tempUploads = Path.Combine(Path.GetTempPath(), $"clothingstore_uploads_{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!"
                })
                .Build();

            var hasher = new Pbkdf2PasswordHasher();
            var jwtService = new JwtTokenService(config);
            var repo = new ClothingStoreRepository(hasher, tempFile);
            using var httpClient = new HttpClient();
            var imageStorage = new SupabaseImageStorageService(httpClient, config, tempUploads);

            var authService = new AuthService(repo, hasher, jwtService);
            var catalogService = new CatalogService(repo);
            var productService = new ProductManagementService(repo, imageStorage);
            var stockService = new StockManagementService(repo);
            var adminService = new EmployeeAndSettingsService(repo, hasher, imageStorage);

            // 1. Login de Administrador e validação do JWT
            var loginResp = await authService.LoginAsync(new LoginRequest("admin@loja.com", "Admin@123"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(loginResp.AccessToken));
            Assert.IsTrue(loginResp.Employee.IsAdmin);

            var claimsPrincipal = jwtService.ValidateToken(loginResp.AccessToken);
            Assert.IsNotNull(claimsPrincipal);
            Assert.IsTrue(claimsPrincipal!.IsInRole(RoleNames.Admin));

            // 2. Alteração de Configurações da Loja pelo proprietário reflete imediatamente no Catálogo Público
            await adminService.UpdateStoreSettingsAsync(
                loginResp.Employee.EmployeeId,
                new UpdateStoreSettingsRequest
                {
                    StoreName = "Maison Lumière",
                    WhatsappNumber = "+55 (21) 99887-6655",
                    StoreDescription = "Roupas autorais e minimalistas no Rio de Janeiro.",
                    LogoPublicUrl = "https://exemplo.com/logo-maison.png",
                    DefaultLowStockThreshold = 4
                });

            var publicSettings = await catalogService.GetPublicStoreSettingsAsync();
            Assert.AreEqual("Maison Lumière", publicSettings.StoreName);
            Assert.AreEqual("maison-lumiere", publicSettings.StoreSlug);
            Assert.AreEqual("5521998876655", publicSettings.WhatsappNumber);
            Assert.AreEqual("https://exemplo.com/logo-maison.png", publicSettings.LogoPublicUrl);

            // 3. Movimentação de estoque no app reflete imediatamente no catálogo público e no Dashboard
            var camisetaDetail = await catalogService.GetPublicProductDetailAsync("camiseta-basica");
            Assert.IsNotNull(camisetaDetail);
            var pretoP = camisetaDetail!.Variants.First(v => v.Color == "Preto" && v.Size == "P");
            Assert.AreEqual(3, pretoP.StockQuantity);

            var movDto = await stockService.RegisterMovementAsync(
                loginResp.Employee.EmployeeId,
                new CreateStockMovementRequest
                {
                    ProductVariantId = pretoP.Id,
                    MovementType = "IN",
                    Quantity = 7,
                    Reason = "Chegada de lote novo"
                });

            Assert.AreEqual(3, movDto.PreviousQuantity);
            Assert.AreEqual(10, movDto.NewQuantity);

            var updatedCamiseta = await catalogService.GetPublicProductDetailAsync("camiseta-basica");
            var updatedPretoP = updatedCamiseta!.Variants.First(v => v.Id == pretoP.Id);
            Assert.AreEqual(10, updatedPretoP.StockQuantity);

            // 4. Ocultar produto no app remove imediatamente do catálogo público
            await productService.ToggleVisibilityAsync(camisetaDetail.Id, false);
            var hiddenProduct = await catalogService.GetPublicProductDetailAsync("camiseta-basica");
            Assert.IsNull(hiddenProduct);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
            if (Directory.Exists(tempUploads))
                Directory.Delete(tempUploads, recursive: true);
        }
    }

    [TestMethod]
    public async Task CatalogFilters_ProductCrud_ImageUpload_PasswordReset_And_CrossProcessSync_ShouldWork()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_sync_{Guid.NewGuid():N}.json");
        var tempUploads = Path.Combine(Path.GetTempPath(), $"clothingstore_uploads_{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!"
                })
                .Build();

            var hasher = new Pbkdf2PasswordHasher();
            var jwtService = new JwtTokenService(config);

            // Instância 1: representa o App MAUI
            var repoMaui = new ClothingStoreRepository(hasher, tempFile);
            // Instância 2: representa o Site Blazor Web em outro processo lendo o mesmo arquivo
            var repoWeb = new ClothingStoreRepository(hasher, tempFile);

            using var httpClient = new HttpClient();
            var imageStorage = new SupabaseImageStorageService(httpClient, config, tempUploads);

            var authService = new AuthService(repoMaui, hasher, jwtService);
            var productServiceMaui = new ProductManagementService(repoMaui, imageStorage);
            var adminServiceMaui = new EmployeeAndSettingsService(repoMaui, hasher, imageStorage);
            var catalogServiceWeb = new CatalogService(repoWeb);

            // 1. Testar todos os filtros do catálogo público no site
            var allPublicProducts = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter());
            Assert.AreEqual(4, allPublicProducts.Count);

            var byCategory = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter(CategorySlug: "camisetas"));
            Assert.AreEqual(1, byCategory.Count);
            Assert.AreEqual("Camiseta Básica", byCategory[0].Name);

            var byColor = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter(Color: "Bege"));
            Assert.AreEqual(1, byColor.Count);
            Assert.AreEqual("Calça Alfaiataria Reta", byColor[0].Name);

            var bySize = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter(Size: "G"));
            Assert.AreEqual(3, bySize.Count);

            var bySearch = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter(Search: "Linho"));
            Assert.AreEqual(1, bySearch.Count);
            Assert.AreEqual("Camisa Puro Linho Relaxed", bySearch[0].Name);

            var onlyInStock = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter(OnlyInStock: true));
            Assert.AreEqual(4, onlyInStock.Count);

            // 2. Criar novo produto no MAUI, fazer upload de foto e adicionar variante -> verificar sincronização imediata no repoWeb
            var categories = await productServiceMaui.GetAllCategoriesAsync();
            var newProduct = await productServiceMaui.CreateProductAsync(new SaveProductRequest
            {
                CategoryId = categories[0].Id,
                Name = "Jaqueta Bomber Minimal",
                Description = "Jaqueta estruturada impermeável.",
                BasePrice = 349.90m,
                IsVisibleOnSite = true
            });

            using var fakeImageStream = new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10]);
            var uploadedImg = await productServiceMaui.UploadProductImageAsync(
                newProduct.Id,
                "jaqueta.png",
                fakeImageStream,
                "image/png",
                "Jaqueta Bomber",
                true);
            Assert.IsTrue(uploadedImg.PublicUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal));

            var addedVariant = await productServiceMaui.AddVariantAsync(newProduct.Id, Guid.NewGuid(), new SaveProductVariantRequest
            {
                Color = "Verde Militar",
                Size = "G",
                InitialStockQuantity = 6,
                LowStockThreshold = 2,
                IsActive = true
            });
            Assert.AreEqual(6, addedVariant.StockQuantity);

            // O site Web (repoWeb) deve enxergar imediatamente o novo produto, sua foto Data URI e sua variante!
            var webProduct = await catalogServiceWeb.GetPublicProductDetailAsync("jaqueta-bomber-minimal");
            Assert.IsNotNull(webProduct);
            Assert.AreEqual("Jaqueta Bomber Minimal", webProduct!.Name);
            Assert.AreEqual(6, webProduct.TotalStockQuantity);
            Assert.AreEqual(1, webProduct.Images.Count);
            Assert.IsTrue(webProduct.Images[0].PublicUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal));

            // 3. Testar recuperação de senha e login com nova senha
            var forgotResp = await authService.RequestPasswordResetAsync(new ForgotPasswordRequest("funcionario@loja.com"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(forgotResp.DemoResetCode));

            await authService.ResetPasswordAsync(new ResetPasswordRequest(
                "funcionario@loja.com",
                forgotResp.DemoResetCode!,
                "NovaSenha@2026"));

            var relogin = await authService.LoginAsync(new LoginRequest("funcionario@loja.com", "NovaSenha@2026"));
            Assert.IsNotNull(relogin.AccessToken);

            // 4. Testar QR Code SVG Data URI
            var qrSvg = WhatsAppLinkBuilder.GenerateQrCodeSvgDataUri("http://localhost:5190/loja/aura-vestuario");
            Assert.IsTrue(qrSvg.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
            if (Directory.Exists(tempUploads))
                Directory.Delete(tempUploads, recursive: true);
        }
    }
}
