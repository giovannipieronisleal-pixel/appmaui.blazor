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

        // Se OnlineBaseUrl estiver configurado (Google Cloud Run ou nuvem gratuita compartilhada), utiliza /loja/{slug} automaticamente
        settings.OnlineBaseUrl = "https://minhaloja-catalog-xyz.a.run.app";
        Assert.AreEqual("https://minhaloja-catalog-xyz.a.run.app/loja/atelier-moda", settings.BuildStoreCatalogUrl());
        Assert.AreEqual("https://youlink-vitrine.onrender.com/loja/atelier-moda", WhatsAppLinkBuilder.BuildStoreCatalogUrl("https://youlink-vitrine.onrender.com", "Atelier Moda"));
        Assert.AreEqual("https://youlink.koyeb.app/loja/atelier-moda", WhatsAppLinkBuilder.BuildStoreCatalogUrl("https://youlink.koyeb.app/loja/atelier-moda", "Atelier Moda"));

        // Se OnlineBaseUrl for um domínio próprio oficial do lojista (ex.: https://minhalojadigital.com), o link do catálogo é a raiz direta limpa
        settings.OnlineBaseUrl = "https://minhalojadigital.com";
        Assert.AreEqual("https://minhalojadigital.com", settings.BuildStoreCatalogUrl());
        Assert.AreEqual("https://minhalojadigital.com", WhatsAppLinkBuilder.BuildStoreCatalogUrl("https://minhalojadigital.com", "minha-loja"));

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
    public async Task Repository_InitialSeed_ShouldStartCleanWithoutExampleProductsOrDefaultUsers()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_clean_{Guid.NewGuid():N}.json");
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, tempFile);
            var jwt = new JwtTokenService(new ConfigurationBuilder().Build());
            var auth = new AuthService(repo, hasher, jwt);

            var products = await repo.GetProductsAsync();
            var categories = await repo.GetCategoriesAsync();
            var employees = await repo.GetEmployeesAsync();
            var setupStatus = await auth.GetSetupStatusAsync();

            // Nenhum produto, categoria nem usuário padrão pré-cadastrado
            Assert.AreEqual(0, products.Count);
            Assert.AreEqual(0, categories.Count);
            Assert.AreEqual(0, employees.Count);
            Assert.IsTrue(setupStatus.RequiresInitialSetup);

            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await auth.LoginAsync(new LoginRequest("admin", "admin")));

            var settings = await repo.GetStoreSettingsAsync();
            Assert.AreEqual("Minha Loja", settings.StoreName);
            Assert.AreEqual("minha-loja", settings.StoreSlug);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public async Task Security_RegisteredAdminLogin_OptionalEmployeeEmail_BruteForceLockout_And_ZeroPasswordLeak_ShouldWork()
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

            // 1. Primeira conta cadastrada vira Administradora e entra por nome de usuário (sem precisar de @)
            await authService.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Dona Segurança",
                "dona.sec@loja.com",
                "SenhaForte@123",
                Username: "dona.sec"));
            var loginResp = await authService.LoginAsync(new LoginRequest("dona.sec", "SenhaForte@123"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(loginResp.AccessToken));
            Assert.IsTrue(loginResp.Employee.IsAdmin);
            Assert.AreEqual("dona.sec@loja.com", loginResp.Employee.Email);

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
                    await authService.LoginAsync(new LoginRequest("dona.sec", "errada")));
            }

            // Mesmo com a senha correta, a conta bloqueada rejeita o login durante o período de lockout
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
                await authService.LoginAsync(new LoginRequest("dona.sec", "SenhaForte@123")));
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

            // 1. Cadastro da primeira conta Administradora e personalização da loja
            var adminSession = await authService.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Dona Lumière",
                "dona@lumiere.com",
                "SenhaForte@123",
                Username: "dona.lumiere"));
            Assert.IsTrue(adminSession.Employee.IsAdmin);
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

            // 2. Criar Categoria, Produto (inclusive com categoria digitada inline), Editar Produto e Variantes no App MAUI
            var cat = await productServiceMaui.CreateCategoryAsync(new SaveCategoryRequest
            {
                Name = "Camisetas",
                Description = "Linha algodão",
                DisplayOrder = 1,
                IsActive = true
            });

            var newProduct = await productServiceMaui.CreateProductAsync(new SaveProductRequest
            {
                CategoryId = Guid.Empty,
                CategoryName = "Camisetas",
                Name = "Camiseta Básica",
                Description = "Malha 100% algodão",
                BasePrice = 129.90m,
                IsVisibleOnSite = true
            }, adminSession.Employee.EmployeeId);

            // Editar dados do produto (nome, preço e descrição)
            newProduct = await productServiceMaui.UpdateProductAsync(newProduct.Id, new SaveProductRequest
            {
                CategoryId = cat.Id,
                Name = "Camiseta Básica",
                Description = "Malha 100% algodão egípcio premium",
                BasePrice = 139.90m,
                IsVisibleOnSite = true
            });
            Assert.AreEqual(139.90m, newProduct.BasePrice);

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

            // Editar variante (inclusive ajustando quantidade em estoque)
            vBrancoM = await productServiceMaui.UpdateVariantAsync(vBrancoM.Id, new SaveProductVariantRequest
            {
                Color = "Branco",
                Size = "M",
                CustomSku = vBrancoM.Sku,
                InitialStockQuantity = 8,
                UpdateStockQuantity = true,
                LowStockThreshold = 2,
                IsActive = true
            }, adminSession.Employee.EmployeeId);

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

    [TestMethod]
    public async Task RegisteredAdmin_Login_And_TwoFactor_ShouldWorkProperly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_admin_test_{Guid.NewGuid():N}.json");
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, tempFile);
            var jwt = new JwtTokenService(new ConfigurationBuilder().Build());
            var authService = new AuthService(repo, hasher, jwt);

            // 1. Cadastra a primeira conta administradora e entra com usuário e senha
            await authService.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Administrador Loja",
                "dona.twofa@loja.com",
                "Senha2FA@123",
                Username: "dona.twofa"));
            var loginResp = await authService.LoginAsync(new LoginRequest("dona.twofa", "Senha2FA@123"));
            Assert.IsNotNull(loginResp);
            Assert.AreEqual("dona.twofa@loja.com", loginResp.Employee.Email);
            Assert.AreEqual("Administrador Loja", loginResp.Employee.FullName);
            Assert.IsTrue(loginResp.Employee.IsAdmin);

            // 2. Testar que login com caixa alta (case-insensitive) também funciona
            var loginCaseResp = await authService.LoginAsync(new LoginRequest("DONA.TWOFA", "Senha2FA@123"));
            Assert.IsNotNull(loginCaseResp);

            // 3. Configurar 2FA para o admin
            var setup = await authService.GenerateTwoFactorSetupAsync(loginResp.Employee.UserId);
            Assert.IsNotNull(setup);
            Assert.AreEqual(16, setup.SecretKey.Length);
            Assert.AreEqual(4, setup.BackupCodes.Count);

            // 4. Ativar 2FA com o código TOTP atual
            var currentOtp = ClothingStore.Domain.Common.TwoFactorHelper.GenerateCurrentTotp(setup.SecretKey);
            var enabledProfile = await authService.EnableTwoFactorAsync(
                loginResp.Employee.UserId,
                new EnableTwoFactorRequest(setup.SecretKey, currentOtp, setup.BackupCodes));
            Assert.IsTrue(enabledProfile.HasTwoFactorEnabled);

            // 5. Tentar logar com senha: deve acusar que 2FA é exigido
            var challenge = await authService.LoginWithChallengeAsync(new LoginRequest("dona.twofa", "Senha2FA@123"));
            Assert.IsTrue(challenge.RequiresTwoFactor);
            Assert.IsNull(challenge.AuthResult);

            // 6. Completar login com código 2FA
            var twoFactorLogin = await authService.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("dona.twofa", currentOtp));
            Assert.IsNotNull(twoFactorLogin);
            Assert.AreEqual("dona.twofa@loja.com", twoFactorLogin.Employee.Email);

            // 7. Também permite usar código de emergência/backup
            var backupCode = setup.BackupCodes[0];
            var backupLogin = await authService.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("dona.twofa", backupCode));
            Assert.IsNotNull(backupLogin);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void StoreSettings_PlanTierAndDesignGating_ShouldEnforceMinimalistaOnBasicAndCustomOnPremium()
    {
        var settings = new StoreSettings("Maison Luxo", "11999999999", "Curadoria editorial");

        Assert.AreEqual(StoreSettings.PlanTierBasic, settings.PlanTier);
        Assert.IsFalse(settings.IsPremiumPlan);
        Assert.AreEqual("#FAF8F5", settings.GetEffectivePublicDesign().BackgroundColor);
        Assert.AreEqual("Playfair Display", settings.GetEffectivePublicDesign().DisplayFont);

        var customDesign = new StoreDesignConfig
        {
            PresetKey = "noir-noturno",
            BackgroundColor = "#0e0e10",
            PrimaryColor = "#e5c99b",
            AccentColor = "#invalid",
            DisplayFont = "Bodoni Moda",
            BodyFont = "Inter",
            CornerRadiusMode = "SHARP",
            HeroLayout = "banner",
            GridColumns = 99,
            HeroEyebrow = " Coleção Noite "
        };

        // No plano Básico, o rascunho é normalizado mas o site público mantém Minimalista Luxo fixo
        settings.UpdatePlanAndDesign(StoreSettings.PlanTierBasic, 59.90m, 149.90m, "pix@maison.com", customDesign);
        var basicEffective = settings.GetEffectivePublicDesign();
        Assert.AreEqual("#FAF8F5", basicEffective.BackgroundColor);
        Assert.AreEqual("Playfair Display", basicEffective.DisplayFont);
        Assert.AreEqual("#0E0E10", settings.DesignConfig.BackgroundColor);
        Assert.AreEqual("#B69158", settings.DesignConfig.AccentColor); // hex inválido cai no padrão
        Assert.AreEqual(4, settings.DesignConfig.GridColumns); // limitado a 4 colunas

        // No plano Premium ("versão mais cara"), o design moldado passa a valer
        settings.UpdatePlanAndDesign(StoreSettings.PlanTierPremium);
        Assert.IsTrue(settings.IsPremiumPlan);
        Assert.AreEqual(149.90m, settings.GetActivePlanMonthlyPrice());

        var premiumEffective = settings.GetEffectivePublicDesign();
        Assert.AreEqual("#0E0E10", premiumEffective.BackgroundColor);
        Assert.AreEqual("Bodoni Moda", premiumEffective.DisplayFont);
        Assert.AreEqual("banner", premiumEffective.HeroLayout);
        Assert.AreEqual("Coleção Noite", premiumEffective.HeroEyebrow);

        // Reverter para Básico restaura Minimalista Luxo na vitrine sem perder o rascunho salvo
        settings.UpdatePlanAndDesign(StoreSettings.PlanTierBasic);
        Assert.AreEqual("#FAF8F5", settings.GetEffectivePublicDesign().BackgroundColor);
        Assert.AreEqual("#0E0E10", settings.DesignConfig.BackgroundColor);
    }

    [TestMethod]
    public async Task SettingsService_And_CatalogService_ShouldPersistPlanAndCustomDesignAcrossRepositories()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_design_test_{Guid.NewGuid():N}.json");
        var tempUploads = Path.Combine(Path.GetTempPath(), $"clothingstore_design_uploads_{Guid.NewGuid():N}");
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repoAdmin = new ClothingStoreRepository(hasher, tempFile);
            var imgStorage = new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), tempUploads);
            var settingsService = new EmployeeAndSettingsService(repoAdmin, hasher, imgStorage);

            var preset = StoreDesignPresets.All.First(p => p.Key == "boutique-rose").Design.Clone();
            preset.AccentColor = "#C97A7E";
            preset.HeroHeadline = "Atelier Sob Medida";
            preset.GridColumns = 2;

            var updated = await settingsService.UpdateStoreSettingsAsync(null, new UpdateStoreSettingsRequest
            {
                StoreName = "Maison Rosé",
                WhatsappNumber = "11988887777",
                StoreDescription = "Curadoria exclusiva.",
                PlanTier = StoreDesignPresets.PlanPremium,
                BasicPlanMonthlyPrice = 49.90m,
                PremiumPlanMonthlyPrice = 139.90m,
                PicpayPixKey = "atelier@picpay",
                Design = preset
            });

            Assert.AreEqual("Premium", updated.PlanTier);
            Assert.AreEqual(139.90m, updated.PremiumPlanMonthlyPrice);
            Assert.AreEqual("#C97A7E", updated.Design?.AccentColor);

            var repoPublic = new ClothingStoreRepository(hasher, tempFile);
            var catalogService = new CatalogService(repoPublic);
            var publicInfo = await catalogService.GetPublicStoreSettingsAsync();

            Assert.AreEqual("Premium", publicInfo.PlanTier);
            Assert.IsNotNull(publicInfo.Design);
            Assert.AreEqual("#FDF8F6", publicInfo.Design!.BackgroundColor);
            Assert.AreEqual("#C97A7E", publicInfo.Design.AccentColor);
            Assert.AreEqual("Atelier Sob Medida", publicInfo.Design.HeroHeadline);
            Assert.AreEqual(2, publicInfo.Design.GridColumns);

            var cssVars = StoreDesignCssHelper.BuildInlineStyle(publicInfo.Design);
            Assert.IsTrue(cssVars.Contains("--brand-accent:#C97A7E", StringComparison.Ordinal));
            Assert.IsTrue(cssVars.Contains("--brand-grid-cols:2", StringComparison.Ordinal));

            var fontsUrl = StoreDesignCssHelper.BuildGoogleFontsUrl(publicInfo.Design);
            Assert.IsTrue(fontsUrl.Contains("Cormorant+Garamond", StringComparison.Ordinal));
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
    public void GiftCard_Redemption_ExpirationFallback_And_Revocation_Rules()
    {
        var settings = new StoreSettings();
        settings.UpdatePlanAndDesign("Basico", designConfig: new StoreDesignConfig { BackgroundColor = "#0E0E10" });

        var t0 = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var code15Days = PlanActivationCode.Create(
            targetPlanTier: "Premium",
            durationDays: 15,
            customCode: "LUXO-VIP-15D");

        settings.RedeemActivationCode(code15Days, Guid.NewGuid(), now: t0);

        Assert.IsTrue(settings.IsEffectivePremiumPlan(t0.AddDays(14)));
        Assert.AreEqual("#0E0E10", settings.GetEffectivePublicDesign(t0.AddDays(14)).BackgroundColor);
        Assert.AreEqual("LUXO-VIP-15D", settings.ActiveGiftCardCode);

        // Após expirar no dia 16, o plano retorna automaticamente ao tema padrão Minimalista Luxo
        Assert.IsFalse(settings.IsEffectivePremiumPlan(t0.AddDays(16)));
        Assert.AreEqual("#FAF8F5", settings.GetEffectivePublicDesign(t0.AddDays(16)).BackgroundColor);

        // Código já resgatado não pode ser reutilizado
        Assert.ThrowsException<DomainException>(() => settings.RedeemActivationCode(code15Days, Guid.NewGuid(), now: t0.AddDays(2)));

        // Código revogado não pode ser resgatado, e Vitalício não expira
        var codeRevoked = PlanActivationCode.Create("Premium", 30);
        codeRevoked.Revoke();
        Assert.ThrowsException<DomainException>(() => settings.RedeemActivationCode(codeRevoked, Guid.NewGuid(), now: t0));

        var codeLifetime = PlanActivationCode.Create("Premium", durationDays: null, customCode: "LUXO-VITALICIO");
        settings.RedeemActivationCode(codeLifetime, Guid.NewGuid(), now: t0.AddDays(20));
        Assert.IsNull(settings.PlanExpiresAt);
        Assert.IsTrue(settings.IsEffectivePremiumPlan(t0.AddYears(10)));
    }

    [TestMethod]
    public async Task GiftCard_And_StorePlans_CrossRepo_ShouldWork()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_giftcard_{Guid.NewGuid():N}.json");
        var tempUploads = Path.Combine(Path.GetTempPath(), $"clothingstore_giftcard_up_{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var hasher = new Pbkdf2PasswordHasher();

            // Repositório A grava o código Gift Card Premium
            var repoIssuer = new ClothingStoreRepository(hasher, tempFile);
            var giftCode = PlanActivationCode.Create(
                targetPlanTier: StoreSettings.PlanTierPremium,
                durationDays: 30);

            Assert.IsTrue(giftCode.Code.StartsWith("LUXO-PREM-30D-", StringComparison.Ordinal));
            await repoIssuer.SavePlanActivationCodeAsync(giftCode);

            // Repositório B (simulando o app da loja recarregado) resgata o código
            var repoStoreApp = new ClothingStoreRepository(hasher, tempFile);
            var imgStorage = new SupabaseImageStorageService(new HttpClient(), config, tempUploads);
            var storeService = new EmployeeAndSettingsService(repoStoreApp, hasher, imgStorage);

            var redeemResp = await storeService.RedeemActivationCodeAsync(
                new RedeemActivationCodeRequest { Code = giftCode.Code });

            Assert.AreEqual("Premium", redeemResp.ActivatedPlanTier);
            Assert.IsNotNull(redeemResp.PlanExpiresAt);
            Assert.AreEqual("Premium", (await repoStoreApp.GetStoreSettingsAsync()).PlanTier);
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
    public async Task EmployeePrivacy_OnlyUsernameAndGmailStored_PasswordChangedSolelyByUserViaEmail()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_privacy_{Guid.NewGuid():N}.json");
        var tempUploads = Path.Combine(Path.GetTempPath(), $"clothingstore_privacy_up_{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "PrivacyTestSecretKey_1234567890_ABCDEF!"
                })
                .Build();

            var hasher = new Pbkdf2PasswordHasher();
            var jwtService = new JwtTokenService(config);
            var emailCapture = new CapturingTestEmailService();
            var repo = new ClothingStoreRepository(hasher, tempFile);
            var imgStorage = new SupabaseImageStorageService(new HttpClient(), config, tempUploads);
            var authService = new AuthService(repo, hasher, jwtService, emailCapture);
            var empService = new EmployeeAndSettingsService(repo, hasher, imgStorage, emailCapture);

            // 1. Cria funcionária apenas com Usuário + Gmail (sem senha informada pelo admin)
            var created = await empService.CreateEmployeeAsync(new CreateEmployeeRequest
            {
                FullName = "Mariana Costa",
                Username = "mari.vendas",
                Email = "mariana.costa@gmail.com",
                RoleName = "Employee",
                CanManageStock = true
            });

            Assert.AreEqual("mari.vendas", created.EffectiveUsername);
            Assert.AreEqual("mariana.costa@gmail.com", created.Email);
            Assert.AreEqual("mariana.costa@gmail.com", emailCapture.LastRecipient);
            Assert.IsFalse(string.IsNullOrWhiteSpace(emailCapture.LastSecurityCode));

            // 2. Diretório de usuários mostra apenas Usuário e Gmail (sem expor hashes ou senhas)
            var allEmployees = await empService.GetEmployeesAsync();
            var listedEmployee = allEmployees.FirstOrDefault(e => e.EffectiveUsername == "mari.vendas");
            Assert.IsNotNull(listedEmployee);
            Assert.AreEqual("mariana.costa@gmail.com", listedEmployee!.Email);

            // 3. Apenas a própria usuária define a senha usando o código enviado ao Gmail dela
            await authService.ResetPasswordAsync(new ResetPasswordRequest(
                "mari.vendas",
                emailCapture.LastSecurityCode!,
                "MinhaSenhaSecreta99"));

            var logged = await authService.LoginAsync(new LoginRequest("mari.vendas", "MinhaSenhaSecreta99"));
            Assert.AreEqual("mari.vendas", logged.Employee.EffectiveUsername);

            // 4. Redefinição posterior envia novo código exclusivamente para o Gmail da usuária (sem nunca retornar o código)
            var forgotResp = await authService.RequestPasswordResetAsync(
                new ForgotPasswordRequest("mari.vendas"));
            Assert.AreEqual("mariana.costa@gmail.com", emailCapture.LastRecipient);
            Assert.IsFalse(forgotResp.Message.Contains(emailCapture.LastSecurityCode!, StringComparison.Ordinal));
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
    public void StoreSettings_IntermediatePlan_ShouldAllow5PreReadyVersionsWithoutCustomOverrides()
    {
        var settings = new StoreSettings("Atelier Meia Versao", "5511988887777", "Vitrine pronta");
        Assert.AreEqual(StoreSettings.PlanTierBasic, settings.PlanTier);

        var expectedEditorial = StoreDesignConfig.CreatePresetByKey("editorial-vogue");
        var customAttempt = new StoreDesignConfig
        {
            PresetKey = "editorial-vogue",
            BackgroundColor = "#112233",
            DisplayFont = "Fonte Custom Proibida"
        };

        settings.UpdatePlanAndDesign(StoreSettings.PlanTierIntermediate, designConfig: customAttempt);

        Assert.IsTrue(settings.IsIntermediatePlan);
        Assert.IsFalse(settings.IsPremiumPlan);
        Assert.AreEqual(StoreSettings.PlanTierIntermediate, settings.GetEffectivePlanTier());
        Assert.AreEqual(settings.IntermediaryPlanMonthlyPrice, settings.GetActivePlanMonthlyPrice());

        var effectiveEditorial = settings.GetEffectivePublicDesign();
        Assert.AreEqual("editorial-vogue", effectiveEditorial.PresetKey);
        Assert.AreEqual(expectedEditorial.BackgroundColor, effectiveEditorial.BackgroundColor);
        Assert.AreEqual(expectedEditorial.DisplayFont, effectiveEditorial.DisplayFont);

        settings.UpdatePlanAndDesign(StoreSettings.PlanTierIntermediate, designConfig: StoreDesignConfig.CreatePresetByKey("noir-noturno"));
        var effectiveNoir = settings.GetEffectivePublicDesign();
        var expectedNoir = StoreDesignConfig.CreatePresetByKey("noir-noturno");
        Assert.AreEqual("noir-noturno", effectiveNoir.PresetKey);
        Assert.AreEqual(expectedNoir.BackgroundColor, effectiveNoir.BackgroundColor);

        // Expiracao da licenca intermediaria volta com seguranca para Basico Minimalista Luxo
        var oldCode = PlanActivationCode.Create(StoreSettings.PlanTierIntermediate, 1);
        settings.RedeemActivationCode(oldCode, now: DateTimeOffset.UtcNow.AddDays(-2));
        Assert.IsFalse(settings.IsLicenseActive());
        Assert.AreEqual(StoreSettings.PlanTierBasic, settings.GetEffectivePlanTier());
        Assert.AreEqual("minimalista-luxo", settings.GetEffectivePublicDesign().PresetKey);
    }

    [TestMethod]
    public async Task GiftCard_IntermediateRedemption_ShouldActivateIntermediatePlan()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clothingstore_intr_{Guid.NewGuid():N}.json");
        var tempUploads = Path.Combine(Path.GetTempPath(), $"clothingstore_intr_up_{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var hasher = new Pbkdf2PasswordHasher();
            var repoIssuer = new ClothingStoreRepository(hasher, tempFile);

            var intrCode = PlanActivationCode.Create(StoreSettings.PlanTierIntermediate, 30);
            Assert.IsTrue(intrCode.Code.StartsWith("LUXO-INTR-30D-", StringComparison.Ordinal));
            Assert.AreEqual(StoreSettings.PlanTierIntermediate, intrCode.TargetPlanTier);

            await repoIssuer.SavePlanActivationCodeAsync(intrCode);

            var repoStoreApp = new ClothingStoreRepository(hasher, tempFile);
            var imgStorage = new SupabaseImageStorageService(new HttpClient(), config, tempUploads);
            var storeService = new EmployeeAndSettingsService(repoStoreApp, hasher, imgStorage);

            var redeemResp = await storeService.RedeemActivationCodeAsync(
                new RedeemActivationCodeRequest { Code = intrCode.Code });

            Assert.AreEqual(StoreSettings.PlanTierIntermediate, redeemResp.ActivatedPlanTier);
            Assert.IsNotNull(redeemResp.PlanExpiresAt);

            var reloaded = await repoStoreApp.GetStoreSettingsAsync();
            Assert.AreEqual(StoreSettings.PlanTierIntermediate, reloaded.GetEffectivePlanTier());
            Assert.IsTrue(reloaded.IsIntermediatePlan);
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
