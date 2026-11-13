using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public sealed class CloudPublishOneClickTests
{
    private static (ClothingStoreRepository Repo, EmployeeAndSettingsService SettingsService, CatalogService CatalogService, CloudSyncStateService SyncService, string TempDir) CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink-cloud-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "db.json");
        var hasher = new Pbkdf2PasswordHasher();
        var repo = new ClothingStoreRepository(hasher, dbPath);
        var storage = new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), Path.Combine(tempDir, "wwwroot"));
        var settingsService = new EmployeeAndSettingsService(repo, hasher, storage);
        var catalogService = new CatalogService(repo);
        var syncService = new CloudSyncStateService(repo, settingsService);
        return (repo, settingsService, catalogService, syncService, tempDir);
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Não foi possível localizar a raiz do repositório YouLink.");
    }

    [TestMethod]
    public async Task CloudSyncStateService_RepeatedSync_ShouldUpsertWithoutDuplicates_KeepSiteActive_AndMirrorRemovals()
    {
        var (repo, _, catalogService, syncService, tempDir) = CreateTestContext();
        try
        {

            var dressId = Guid.NewGuid();
            var blouseId = Guid.NewGuid();

            var firstPayload = new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Atelier da Vila",
                    StoreSlug = "atelier-da-vila",
                    WhatsappNumber = "5511988887777",
                    StoreDescription = "Moda feminina autoral com envio rápido.",
                    OnlineBaseUrl = "https://atelier-da-vila.onrender.com",
                    AutoSyncOnlineOnSave = true,
                    IsSiteActive = true,
                    ServerHostingMode = "DedicatedOwnServer",
                    OwnServerProvider = "Render"
                },
                Categories =
                [
                    new SaveCategoryRequest { Name = "Vestidos", DisplayOrder = 1, IsActive = true },
                    new SaveCategoryRequest { Name = "Blusas", DisplayOrder = 2, IsActive = true }
                ],
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = dressId,
                        CategoryName = "Vestidos",
                        Name = "Vestido Midi Linho",
                        Slug = "vestido-midi-linho",
                        Description = "Linho natural confortável.",
                        BasePrice = 249.90m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Bege",
                                ColorHex = "#D6C7B2",
                                Size = "M",
                                Sku = "VESLIN-BEG-M",
                                StockQuantity = 5,
                                LowStockThreshold = 2,
                                IsActive = true
                            }
                        ],
                        Images =
                        [
                            new CloudProductImageSyncDto
                            {
                                PublicUrl = "data:image/png;base64,iVBORw0KGgo=",
                                AltText = "Vestido Midi Linho Bege",
                                DisplayOrder = 0,
                                IsPrimary = true
                            }
                        ]
                    },
                    new CloudProductSyncDto
                    {
                        SourceId = blouseId,
                        CategoryName = "Blusas",
                        Name = "Blusa Seda Areia",
                        Slug = "blusa-seda-areia",
                        BasePrice = 149.90m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Areia",
                                Size = "P",
                                StockQuantity = 3,
                                IsActive = true
                            }
                        ]
                    }
                ]
            };

            var firstResult = await syncService.ApplySyncPayloadAsync(firstPayload);
            Assert.IsTrue(firstResult.IsSiteActive, "A primeira publicação deve manter a loja ativa mesmo trocando de 'Minha Loja' para o nome real.");
            Assert.AreEqual("Atelier da Vila", firstResult.StoreName);
            Assert.AreEqual(2, firstResult.CategoriesCount);
            Assert.AreEqual(2, firstResult.ProductsCount);

            var secondPayload = new CloudCatalogSyncPayload
            {
                Settings = firstPayload.Settings,
                Categories = firstPayload.Categories,
                CatalogSnapshot =
                [
                    new CloudProductSyncDto
                    {
                        SourceId = dressId,
                        CategoryName = "Vestidos",
                        Name = "Vestido Midi Linho",
                        Slug = "vestido-midi-linho",
                        Description = "Edição atualizada",
                        BasePrice = 269.90m,
                        IsVisibleOnSite = true,
                        Variants =
                        [
                            new CloudProductVariantSyncDto
                            {
                                Color = "Bege",
                                ColorHex = "#D6C7B2",
                                Size = "M",
                                Sku = "VESLIN-BEG-M",
                                StockQuantity = 8,
                                LowStockThreshold = 2,
                                IsActive = true
                            },
                            new CloudProductVariantSyncDto
                            {
                                Color = "Preto",
                                ColorHex = "#18181B",
                                Size = "G",
                                StockQuantity = 4,
                                IsActive = true
                            }
                        ],
                        Images =
                        [
                            new CloudProductImageSyncDto
                            {
                                PublicUrl = "https://cdn.example.com/vestido-novo.jpg",
                                DisplayOrder = 0,
                                IsPrimary = true
                            }
                        ]
                    }
                ]
            };

            var secondResult = await syncService.ApplySyncPayloadAsync(secondPayload);
            Assert.AreEqual(2, secondResult.CategoriesCount);
            Assert.AreEqual(1, secondResult.ProductsCount);
            Assert.IsTrue(secondResult.IsSiteActive);

            var publicStore = await catalogService.GetPublicStoreSettingsAsync();
            Assert.IsTrue(publicStore.IsSiteActive);
            Assert.AreEqual("5511988887777", publicStore.WhatsappNumber);
            Assert.AreEqual("https://atelier-da-vila.onrender.com", publicStore.OnlineBaseUrl);

            var catalogProducts = await catalogService.GetCatalogProductsAsync(new CatalogQueryFilter());
            Assert.AreEqual(1, catalogProducts.Count);
            Assert.AreEqual(269.90m, catalogProducts[0].BasePrice);
            Assert.AreEqual(12, catalogProducts[0].TotalStockQuantity);
            Assert.AreEqual("https://cdn.example.com/vestido-novo.jpg", catalogProducts[0].PrimaryImageUrl);
            Assert.AreEqual("Vestidos", catalogProducts[0].CategoryName);

            var detail = await catalogService.GetPublicProductDetailAsync("vestido-midi-linho");
            Assert.IsNotNull(detail);
            Assert.AreEqual(2, detail!.Variants.Count);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task CloudSyncStateService_LegacyPayloadWithoutSnapshot_ShouldUpsertCategoriesAndProductsIdempotently()
    {
        var (repo, _, _, syncService, tempDir) = CreateTestContext();
        try
        {

            var legacyPayload = new CloudCatalogSyncPayload
            {
                Settings = new UpdateStoreSettingsRequest
                {
                    StoreName = "Loja Legacy",
                    WhatsappNumber = "5511977776666",
                    StoreDescription = "Teste legado idempotente.",
                    OnlineBaseUrl = "https://legacy-store.onrender.com",
                    IsSiteActive = true
                },
                Categories =
                [
                    new SaveCategoryRequest { Name = "Camisas", DisplayOrder = 1, IsActive = true }
                ],
                Products =
                [
                    new SaveProductRequest
                    {
                        CategoryName = "Camisas",
                        Name = "Camisa Social Slim",
                        BasePrice = 189.90m,
                        IsVisibleOnSite = true,
                        InitialColor = "Branco",
                        InitialSize = "M",
                        InitialStockQuantity = 6
                    }
                ]
            };

            var r1 = await syncService.ApplySyncPayloadAsync(legacyPayload);
            var r2 = await syncService.ApplySyncPayloadAsync(legacyPayload);

            Assert.AreEqual(1, r1.CategoriesCount);
            Assert.AreEqual(1, r2.CategoriesCount);
            Assert.AreEqual(1, r2.ProductsCount);

            var persisted = await repo.GetProductsAsync(includeArchived: false);
            Assert.AreEqual(1, persisted.Count);
            Assert.AreEqual(1, persisted[0].Variants.Count);
            Assert.AreEqual(6, persisted[0].TotalStockQuantity);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void WhatsAppLinkBuilder_And_QrCode_ShouldUpdateToRenderCatalogUrl()
    {
        const string renderBaseUrl = "https://atelier-luxo.onrender.com";
        const string storeSlug = "atelier-luxo";

        var officialUrl = WhatsAppLinkBuilder.BuildStoreCatalogUrl(renderBaseUrl, storeSlug);
        Assert.AreEqual("https://atelier-luxo.onrender.com/loja/atelier-luxo", officialUrl);

        var placeholderUrl = WhatsAppLinkBuilder.BuildStoreCatalogUrl("https://lojas.atelier-luxo.com", storeSlug);
        var renderQrUri = WhatsAppLinkBuilder.GenerateQrCodeSvgDataUri(officialUrl);
        var placeholderQrUri = WhatsAppLinkBuilder.GenerateQrCodeSvgDataUri(placeholderUrl);

        Assert.IsTrue(renderQrUri.StartsWith("data:image/svg+xml;", StringComparison.Ordinal));
        Assert.IsTrue(!string.Equals(placeholderQrUri, renderQrUri, StringComparison.Ordinal));

        var whatsappConsultation = WhatsAppLinkBuilder.BuildGeneralStoreLink("5511988887777", "Atelier Luxo");
        Assert.IsTrue(whatsappConsultation.StartsWith("https://wa.me/5511988887777?text=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StoreSettingsPage_And_CloudPublishWizard_ShouldExposeThreeStepWizard_WithoutHiddenDevCommands()
    {
        var root = ResolveRepoRoot();
        var settingsPage = File.ReadAllText(Path.Combine(root, "Components", "Pages", "StoreSettingsPage.razor"));
        var wizardRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "CloudPublishWizard.razor"));
        var wizardCss = File.ReadAllText(Path.Combine(root, "Components", "Pages", "CloudPublishWizard.razor.css"));

        Assert.IsTrue(settingsPage.Contains("<CloudPublishWizard", StringComparison.Ordinal),
            "Loja & Visual deve exibir o componente CloudPublishWizard visivelmente fora de seções ocultas.");
        Assert.IsFalse(settingsPage.Contains("deploy-google-cloud.ps1", StringComparison.OrdinalIgnoreCase),
            "StoreSettingsPage não deve expor comando de terminal deploy-google-cloud.ps1.");
        Assert.IsFalse(settingsPage.Contains("ClothingStore.Api", StringComparison.Ordinal),
            "StoreSettingsPage não deve instruir usuário final sobre projeto ClothingStore.Api.");

        Assert.IsTrue(wizardRazor.Contains("🌐 Criar Meu Site Grátis 24h", StringComparison.Ordinal));
        Assert.IsTrue(wizardRazor.Contains(
            "https://dashboard.render.com/blueprint/new?repo=https://github.com/giovannipieronisleal-pixel/yourlink",
            StringComparison.Ordinal));
        Assert.IsTrue(wizardRazor.Contains("YOULINK_SYNC_SECRET", StringComparison.Ordinal));
        Assert.IsTrue(wizardRazor.Contains("Testar, Sincronizar e Ativar Link", StringComparison.Ordinal));
        Assert.IsTrue(wizardRazor.Contains("📋 Colar", StringComparison.Ordinal));
        Assert.IsTrue(wizardRazor.Contains("Atualizar Site Agora", StringComparison.Ordinal));
        Assert.IsFalse(wizardRazor.Contains("/api/cloud/status", StringComparison.Ordinal),
            "Assistente simplificado não deve expor rotas técnicas /api/cloud/... ao lojista.");
        Assert.IsFalse(wizardRazor.Contains("style=\"", StringComparison.OrdinalIgnoreCase),
            "CloudPublishWizard.razor deve usar CSS isolado (.razor.css) e não estilos inline.");
        Assert.IsTrue(wizardCss.Contains("var(--adm-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AppUpdateCatalog_ParseGitHubRelease_AndPlatformSelection_ShouldWorkAccurately()
    {
        const string releaseJson = """
        {
          "tag_name": "v1.0.10",
          "name": "YouLink v1.0.10",
          "body": "Atualizacao direta no app + fluxo Render em 1 clique.",
          "html_url": "https://github.com/giovannipieronisleal-pixel/yourlink/releases/tag/v1.0.10",
          "draft": false,
          "published_at": "2026-11-13T21:00:00Z",
          "assets": [
            {
              "name": "YouLink-Android.apk",
              "size": 45123000,
              "browser_download_url": "https://github.com/giovannipieronisleal-pixel/yourlink/releases/download/v1.0.10/YouLink-Android.apk"
            },
            {
              "name": "YouLink-Windows-x64.zip",
              "size": 82450100,
              "browser_download_url": "https://github.com/giovannipieronisleal-pixel/yourlink/releases/download/v1.0.10/YouLink-Windows-x64.zip"
            }
          ]
        }
        """;

        var winInfo = AppUpdateCatalog.ParseGitHubReleaseJson(releaseJson, "1.0.9", isWindows: true, isAndroid: false);
        Assert.IsNotNull(winInfo);
        Assert.IsTrue(winInfo!.IsUpdateAvailable);
        Assert.AreEqual("1.0.10", winInfo.LatestVersion);
        Assert.AreEqual(AppUpdateCatalog.WindowsAssetName, winInfo.SelectedAssetName);
        Assert.IsTrue(winInfo.SelectedDownloadUrl!.EndsWith("YouLink-Windows-x64.zip", StringComparison.Ordinal));

        var androidInfo = AppUpdateCatalog.ParseGitHubReleaseJson(releaseJson, "1.0.9", isWindows: false, isAndroid: true);
        Assert.IsNotNull(androidInfo);
        Assert.IsTrue(androidInfo!.IsUpdateAvailable);
        Assert.AreEqual(AppUpdateCatalog.AndroidAssetName, androidInfo.SelectedAssetName);
        Assert.IsTrue(androidInfo.SelectedDownloadUrl!.EndsWith("YouLink-Android.apk", StringComparison.Ordinal));

        var sameVersion = AppUpdateCatalog.ParseGitHubReleaseJson(releaseJson, "1.0.10", isWindows: true, isAndroid: false);
        Assert.IsNotNull(sameVersion);
        Assert.IsFalse(sameVersion!.IsUpdateAvailable);

        var draftJson = releaseJson.Replace("\"draft\": false", "\"draft\": true", StringComparison.Ordinal);
        Assert.IsNull(AppUpdateCatalog.ParseGitHubReleaseJson(draftJson, "1.0.9", isWindows: true, isAndroid: false));
    }

    [TestMethod]
    public void AppUpdateCatalog_WindowsSelfUpdateScript_ShouldResolveNestedRootAndPreserveAppData()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink-updater-test-{Guid.NewGuid():N}");
        try
        {
            var nestedRoot = Path.Combine(tempDir, "YouLink-Windows");
            Directory.CreateDirectory(nestedRoot);
            var fakeExe = Path.Combine(nestedRoot, AppUpdateCatalog.WindowsExecutableName);
            File.WriteAllText(fakeExe, "MZ");

            var resolved = AppUpdateCatalog.ResolveExtractedAppRoot(tempDir);
            Assert.AreEqual(nestedRoot, resolved);

            Assert.IsTrue(AppUpdateCatalog.IsDevelopmentBuildDirectory(@"C:\Dev\youlink\bin\Debug\net10.0-windows10.0.19041.0\win-x64\"));
            Assert.IsFalse(AppUpdateCatalog.IsDevelopmentBuildDirectory(@"C:\Users\Cliente\Desktop\YouLink-Windows\"));

            var script = AppUpdateCatalog.BuildWindowsApplyUpdateScript(
                4321,
                nestedRoot,
                @"C:\Users\Cliente\Desktop\YouLink-Windows",
                AppUpdateCatalog.WindowsExecutableName,
                Path.Combine(tempDir, "update.log"));

            Assert.IsTrue(script.Contains("robocopy", StringComparison.Ordinal));
            Assert.IsTrue(script.Contains("'App_Data'", StringComparison.Ordinal), "Atualização Windows deve preservar a pasta App_Data do lojista.");
            Assert.IsTrue(script.Contains("Start-Process", StringComparison.Ordinal));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void InAppUpdatePopup_And_RenderWebStylesFix_ShouldBeConfiguredProperly()
    {
        var root = ResolveRepoRoot();

        var webAppRazor = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Web", "Components", "App.razor"));
        Assert.IsFalse(webAppRazor.Contains("ClothingStore.Web.styles.css", StringComparison.Ordinal),
            "ClothingStore.Web não possui CSS isolado; remover link quebrado evita erro de MIME/ERR_ABORTED no Render.");

        var modalRazor = File.ReadAllText(Path.Combine(root, "Components", "Shared", "AppUpdateModal.razor"));
        var modalCss = File.ReadAllText(Path.Combine(root, "Components", "Shared", "AppUpdateModal.razor.css"));
        var mainLayout = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"));
        var manifest = File.ReadAllText(Path.Combine(root, "Platforms", "Android", "AndroidManifest.xml"));

        Assert.IsFalse(modalRazor.Contains("style=\"", StringComparison.OrdinalIgnoreCase),
            "AppUpdateModal.razor deve usar .razor.css sem estilos inline.");
        Assert.IsTrue(modalCss.Contains("var(--adm-", StringComparison.Ordinal));
        Assert.IsTrue(modalRazor.Contains("Há uma nova atualização disponível", StringComparison.Ordinal));
        Assert.IsTrue(modalRazor.Contains("Atualizar Agora", StringComparison.Ordinal));

        var themeStart = mainLayout.IndexOf("<div class=\"@Theme.CurrentThemeClass\">", StringComparison.Ordinal);
        var updateModalPos = mainLayout.IndexOf("AppUpdateModal", StringComparison.Ordinal);
        var codePos = mainLayout.IndexOf("@code", StringComparison.Ordinal);
        var lastDivBeforeCode = mainLayout.LastIndexOf("</div>", codePos, StringComparison.Ordinal);
        Assert.IsTrue(updateModalPos > themeStart && updateModalPos < lastDivBeforeCode,
            "AppUpdateModal precisa estar dentro do wrapper de tema em MainLayout.razor.");

        Assert.IsTrue(manifest.Contains("android.permission.REQUEST_INSTALL_PACKAGES", StringComparison.Ordinal),
            "AndroidManifest precisa declarar REQUEST_INSTALL_PACKAGES para instalar atualização direto pelo app.");

        Assert.AreEqual("segredo-render-123",
            WhatsAppLinkBuilder.NormalizeSyncSecretInput(" YOULINK_SYNC_SECRET=\"segredo-render-123\" "));
        Assert.AreEqual("https://youlink-loja.onrender.com",
            WhatsAppLinkBuilder.NormalizePublicServerUrl("youlink-loja.onrender.com/api/cloud/status"));
    }
}
