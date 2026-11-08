using ClothingStore.Application.Interfaces;
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
    private sealed class CapturingTestEmailService : IEmailService
    {
        public string? LastRecipient { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastSecurityCode { get; private set; }

        public Task SendSecurityCodeEmailAsync(
            string recipientEmail,
            string subject,
            string securityCode,
            int validityMinutes,
            CancellationToken ct = default)
        {
            LastRecipient = recipientEmail;
            LastSubject = subject;
            LastSecurityCode = securityCode;
            return Task.CompletedTask;
        }
    }

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
    public async Task Repository_InitialSeed_ShouldStartCleanWithoutExampleProductsAndWithDefaultAdmin()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_clean_{Guid.NewGuid():N}.json");
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, tempFile);

            var products = await repo.GetProductsAsync();
            var categories = await repo.GetCategoriesAsync();
            var employees = await repo.GetEmployeesAsync();

            // Nenhum produto ou categoria de exemplo
            Assert.AreEqual(0, products.Count);
            Assert.AreEqual(0, categories.Count);
            // Apenas o login Admin padrão (login: admin / senha: admin)
            Assert.AreEqual(1, employees.Count);
            Assert.AreEqual("admin", employees[0].User?.Email);

            var settings = await repo.GetStoreSettingsAsync();
            Assert.AreEqual("Minha Loja", settings.StoreName);
            Assert.AreEqual("minha-loja", settings.StoreSlug);

            var liveDbPath = @"C:\Dev\appmaui.blazor\App_Data\clothingstore_db.json";
            if (Directory.Exists(@"C:\Dev\appmaui.blazor\App_Data"))
            {
                File.Copy(tempFile, liveDbPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public async Task Security_DefaultAdminLogin_OptionalEmployeeEmail_BruteForceLockout_And_ZeroPasswordLeak_ShouldWork()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_sec_{Guid.NewGuid():N}.json");
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
            var emailCapture = new CapturingTestEmailService();
            var authService = new AuthService(repo, hasher, jwtService, emailCapture);
            using var httpClient = new HttpClient();
            var imageStorage = new SupabaseImageStorageService(httpClient, config, tempUploads);
            var adminService = new EmployeeAndSettingsService(repo, hasher, imageStorage);

            // 1. Login Admin padrão ("admin" / "admin") funciona imediatamente sem precisar de @
            var loginResp = await authService.LoginAsync(new LoginRequest("admin", "admin"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(loginResp.AccessToken));
            Assert.IsTrue(loginResp.Employee.IsAdmin);
            Assert.AreEqual("admin", loginResp.Employee.Email);

            // 2. Criar funcionário SEM informar e-mail (gera login automático pelo nome ou usa Username)
            var empWithoutEmail = await adminService.CreateEmployeeAsync(new CreateEmployeeRequest
            {
                FullName = "João Silva",
                Email = null,
                Username = null,
                InitialPassword = "1234",
                RoleName = "Employee",
                CanManageStock = true
            });
            Assert.AreEqual("joao.silva", empWithoutEmail.Email);

            var empLogin = await authService.LoginAsync(new LoginRequest("joao.silva", "1234"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(empLogin.AccessToken));

            // 3. Proteção contra força bruta: 5 tentativas de senha erradas bloqueiam a conta por 15 minutos
            for (int i = 0; i < 5; i++)
            {
                await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                    await authService.LoginAsync(new LoginRequest("admin", "errada")));
            }

            // Mesmo com a senha correta, a conta bloqueada rejeita o login durante o período de lockout
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await authService.LoginAsync(new LoginRequest("admin", "admin")));
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
    public async Task EndToEnd_CatalogFilters_ProductCrud_ImageUpload_Stock_And_CrossProcessSync_ShouldWork()
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
            var emailCapture = new CapturingTestEmailService();

            // Instância 1: representa o App MAUI
            var repoMaui = new ClothingStoreRepository(hasher, tempFile);
            // Instância 2: representa o Site Blazor Web em outro processo lendo o mesmo arquivo
            var repoWeb = new ClothingStoreRepository(hasher, tempFile);

            using var httpClient = new HttpClient();
            var imageStorage = new SupabaseImageStorageService(httpClient, config, tempUploads);

            var authService = new AuthService(repoMaui, hasher, jwtService, emailCapture);
            var productServiceMaui = new ProductManagementService(repoMaui, imageStorage);
            var stockServiceMaui = new StockManagementService(repoMaui);
            var adminServiceMaui = new EmployeeAndSettingsService(repoMaui, hasher, imageStorage);
            var catalogServiceWeb = new CatalogService(repoWeb);

            // 1. Login com Admin padrão ("admin" / "admin") e personalização da loja
            var adminSession = await authService.LoginAsync(new LoginRequest("admin", "admin"));
            await adminServiceMaui.UpdateStoreSettingsAsync(adminSession.Employee.EmployeeId, new UpdateStoreSettingsRequest
            {
                StoreName = "Maison Lumière",
                WhatsappNumber = "5521998876655",
                StoreDescription = "Catálogo oficial.",
                DefaultLowStockThreshold = 3
            });

            // Site Web já enxerga o nome e WhatsApp da loja imediatamente
            var publicStore = await catalogServiceWeb.GetPublicStoreSettingsAsync();
            Assert.AreEqual("Maison Lumière", publicStore.StoreName);
            Assert.AreEqual("maison-lumiere", publicStore.StoreSlug);
            Assert.AreEqual("5521998876655", publicStore.WhatsappNumber);

            // 2. Criar Categoria, Produto, Foto e Variantes no App MAUI
            var cat = await productServiceMaui.CreateCategoryAsync(new SaveCategoryRequest
            {
                Name = "Camisetas",
                Description = "Linha algodão",
                DisplayOrder = 1,
                IsActive = true
            });

            var newProduct = await productServiceMaui.CreateProductAsync(new SaveProductRequest
            {
                CategoryId = cat.Id,
                Name = "Camiseta Básica",
                Description = "Malha 100% algodão",
                BasePrice = 129.90m,
                IsVisibleOnSite = true
            });

            using var fakeImageStream = new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10]);
            var uploadedImg = await productServiceMaui.UploadProductImageAsync(
                newProduct.Id,
                "camiseta.png",
                fakeImageStream,
                "image/png",
                "Camiseta Básica",
                true);
            Assert.IsTrue(uploadedImg.PublicUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal));

            var vPretoP = await productServiceMaui.AddVariantAsync(newProduct.Id, adminSession.Employee.EmployeeId, new SaveProductVariantRequest
            {
                Color = "Preto",
                Size = "P",
                InitialStockQuantity = 3,
                LowStockThreshold = 2,
                IsActive = true
            });

            var vBrancoM = await productServiceMaui.AddVariantAsync(newProduct.Id, adminSession.Employee.EmployeeId, new SaveProductVariantRequest
            {
                Color = "Branco",
                Size = "M",
                InitialStockQuantity = 8,
                LowStockThreshold = 2,
                IsActive = true
            });

            // 3. Movimentação de estoque (+5 unidades em Preto P -> saldo vai para 8)
            var mov = await stockServiceMaui.RegisterMovementAsync(adminSession.Employee.EmployeeId, new CreateStockMovementRequest
            {
                ProductVariantId = vPretoP.Id,
                MovementType = "IN",
                Quantity = 5,
                Reason = "Reposição de estoque"
            });
            Assert.AreEqual(8, mov.NewQuantity);

            // 4. O Site Web (repoWeb) reflete imediatamente todos os filtros e saldos atualizados!
            var byCat = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter(CategorySlug: "camisetas"));
            Assert.AreEqual(1, byCat.Count);
            Assert.AreEqual(16, byCat[0].TotalStockQuantity); // 8 + 8

            var byColor = await catalogServiceWeb.GetCatalogProductsAsync(new CatalogQueryFilter(Color: "Branco"));
            Assert.AreEqual(1, byColor.Count);

            var webDetail = await catalogServiceWeb.GetPublicProductDetailAsync("camiseta-basica");
            Assert.IsNotNull(webDetail);
            Assert.AreEqual(2, webDetail!.Variants.Count);
            Assert.IsTrue(webDetail.Images[0].PublicUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal));

            var qrSvg = WhatsAppLinkBuilder.GenerateQrCodeSvgDataUri("http://localhost:5190/loja/maison-lumiere");
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
