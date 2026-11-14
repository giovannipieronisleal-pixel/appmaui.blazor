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

    [TestMethod]
    public void ReleaseWorkflow_ShouldAutoPublishOnMaster_AndDeleteOldReleases()
    {
        var root = ResolveRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-youlink.yml"));
        var rule = File.ReadAllText(Path.Combine(root, ".agents", "rules", "youlink-release-policy.md"));

        Assert.IsTrue(workflow.Contains("github.ref == 'refs/heads/master'", StringComparison.Ordinal),
            "Workflow precisa publicar release automaticamente a cada push em master.");
        Assert.IsTrue(workflow.Contains("ApplicationDisplayVersion", StringComparison.Ordinal),
            "Workflow deve ler ApplicationDisplayVersion do csproj para nomear o release.");
        Assert.IsTrue(workflow.Contains("gh release delete \"$OLD_TAG\" --yes", StringComparison.Ordinal),
            "Workflow deve apagar automaticamente releases antigos após publicar o atual.");
        Assert.IsTrue(workflow.Contains("YouLink-Setup-x64.exe", StringComparison.Ordinal),
            "Workflow deve gerar e enviar o instalador YouLink-Setup-x64.exe.");
        Assert.IsTrue(workflow.Contains("build-windows-package.ps1", StringComparison.Ordinal),
            "Workflow deve usar build-windows-package.ps1 para empacotar YouLink.exe e subpasta App.");
        Assert.IsTrue(rule.Contains("apagar os releases antigos", StringComparison.Ordinal),
            "Regra do workspace precisa documentar o push e limpeza automática dos releases antigos.");

        Assert.IsTrue(File.Exists(Path.Combine(root, "installer", "YouLink.iss")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "installer", "build-windows-package.ps1")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "installer", "launcher", "YouLinkLauncher.cpp")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "installer", "launcher", "YouLinkLauncher.rc")));
    }

    [TestMethod]
    public void AppUpdateCatalog_WindowsSetupAsset_And_InstalledDetection_ShouldWorkAccurately()
    {
        const string releaseWithSetupJson = """
        {
          "tag_name": "v1.0.12",
          "name": "YouLink v1.0.12",
          "body": "Inclui instalador YouLink-Setup-x64.exe.",
          "html_url": "https://github.com/giovannipieronisleal-pixel/yourlink/releases/tag/v1.0.12",
          "draft": false,
          "assets": [
            {
              "name": "YouLink-Setup-x64.exe",
              "size": 78400000,
              "browser_download_url": "https://github.com/giovannipieronisleal-pixel/yourlink/releases/download/v1.0.12/YouLink-Setup-x64.exe"
            },
            {
              "name": "YouLink-Windows-x64.zip",
              "size": 92400000,
              "browser_download_url": "https://github.com/giovannipieronisleal-pixel/yourlink/releases/download/v1.0.12/YouLink-Windows-x64.zip"
            },
            {
              "name": "YouLink-Android.apk",
              "size": 34500000,
              "browser_download_url": "https://github.com/giovannipieronisleal-pixel/yourlink/releases/download/v1.0.12/YouLink-Android.apk"
            }
          ]
        }
        """;

        var setupRelease = AppUpdateCatalog.ParseGitHubReleaseJson(
            releaseWithSetupJson,
            "1.0.11",
            isWindows: true,
            isAndroid: false,
            preferWindowsSetup: true);
        Assert.IsNotNull(setupRelease);
        Assert.AreEqual(AppUpdateCatalog.WindowsSetupAssetName, setupRelease!.SelectedAssetName);

        var portableRelease = AppUpdateCatalog.ParseGitHubReleaseJson(
            releaseWithSetupJson,
            "1.0.11",
            isWindows: true,
            isAndroid: false,
            preferWindowsSetup: false);
        Assert.IsNotNull(portableRelease);
        Assert.AreEqual(AppUpdateCatalog.WindowsAssetName, portableRelease!.SelectedAssetName);

        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink-setup-detect-{Guid.NewGuid():N}");
        try
        {
            var appSubDir = Path.Combine(tempDir, "App");
            Directory.CreateDirectory(appSubDir);
            Assert.IsFalse(AppUpdateCatalog.IsWindowsSetupInstallation(appSubDir));

            File.WriteAllText(Path.Combine(tempDir, "unins000.exe"), "MZ");
            Assert.IsTrue(AppUpdateCatalog.IsWindowsSetupInstallation(appSubDir));

            var installerScript = AppUpdateCatalog.BuildWindowsInstallerUpdateScript(
                1234,
                @"C:\Temp\YouLink-Setup-x64.exe",
                @"C:\Users\Cliente\AppData\Local\Programs\YouLink\YouLink.exe",
                Path.Combine(tempDir, "setup-update.log"));
            Assert.IsTrue(installerScript.Contains("/SILENT", StringComparison.Ordinal));
            Assert.IsFalse(installerScript.Contains("/VERYSILENT", StringComparison.Ordinal));
            Assert.IsTrue(installerScript.Contains("/FORCECLOSEAPPLICATIONS", StringComparison.Ordinal));
            Assert.IsTrue(installerScript.Contains("/LOG=", StringComparison.Ordinal));
            Assert.IsTrue(installerScript.Contains("taskkill.exe", StringComparison.Ordinal));
            Assert.IsTrue(installerScript.Contains("YouLink.exe", StringComparison.Ordinal));

            var root = ResolveRepoRoot();
            var iss = File.ReadAllText(Path.Combine(root, "installer", "YouLink.iss"));
            Assert.IsTrue(iss.Contains("PrepareToInstall", StringComparison.Ordinal));
            Assert.IsTrue(iss.Contains("taskkill.exe", StringComparison.Ordinal));
            Assert.IsTrue(iss.Contains(@"App\wwwroot\*", StringComparison.Ordinal));
            Assert.IsTrue(iss.Contains("ignoreversion recursesubdirs createallsubdirs", StringComparison.Ordinal));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task NoDefaultAdmin_InitialSeed_LegacyDbCleanup_And_LoginUi_ShouldBeEnforced()
    {
        var root = ResolveRepoRoot();

        // 1. UI de Login e Gateway não podem conter atalho nem pré-preenchimento "Admin Padrão / admin"
        var loginRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"));
        Assert.IsFalse(loginRazor.Contains("Admin Padrão", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(loginRazor.Contains("FillAdminCredentials", StringComparison.Ordinal));
        Assert.IsFalse(loginRazor.Contains("_login = \"admin\"", StringComparison.Ordinal));
        Assert.IsFalse(loginRazor.Contains("_password = \"admin\"", StringComparison.Ordinal));
        Assert.IsFalse(loginRazor.Contains("placeholder=\"admin", StringComparison.OrdinalIgnoreCase));

        var gatewayCs = File.ReadAllText(Path.Combine(root, "Services", "MauiAdminGateway.cs"));
        Assert.IsTrue(gatewayCs.Contains("IsLegacyDefaultAdminProfile", StringComparison.Ordinal),
            "Sessões persistidas antigas do admin padrão precisam ser descartadas na inicialização.");

        // 2. Migração automática deve apagar admin legado sem prejudicar loja já cadastrada
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink-no-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hasher = new Pbkdf2PasswordHasher();

            // Caso A: banco antigo tinha somente admin/admin
            var onlyLegacyDbPath = Path.Combine(tempDir, "legacy-only.json");
            var adminRole = new ClothingStore.Domain.Entities.Role(Guid.NewGuid(), "Admin", "Acesso total");
            var employeeRole = new ClothingStore.Domain.Entities.Role(Guid.NewGuid(), "Employee", "Operação");
            var legacyUser = new ClothingStore.Domain.Entities.User("admin", hasher.HashPassword("admin"), adminRole.Id)
            {
                Id = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111")
            };
            var legacyEmp = new ClothingStore.Domain.Entities.Employee(
                legacyUser.Id, "Administrador", "Proprietário(a)", "5511900000000", true, true)
            {
                Id = Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111")
            };
            var legacyState = new ClothingStoreDatabaseState();
            legacyState.Roles.AddRange([adminRole, employeeRole]);
            legacyState.Users.Add(legacyUser);
            legacyState.Employees.Add(legacyEmp);
            File.WriteAllText(onlyLegacyDbPath, System.Text.Json.JsonSerializer.Serialize(legacyState));

            var migratedRepo = new ClothingStoreRepository(hasher, onlyLegacyDbPath);
            Assert.AreEqual(0, (await migratedRepo.GetEmployeesAsync()).Count);
            Assert.IsNull(await migratedRepo.GetUserByEmailAsync("admin"));

            // Caso B: banco antigo tinha admin/admin E uma conta real cadastrada como Employee
            var withMerchantDbPath = Path.Combine(tempDir, "legacy-plus-merchant.json");
            var merchantUser = new ClothingStore.Domain.Entities.User("dona@minhaloja.com", hasher.HashPassword("SenhaBoa@1"), employeeRole.Id, "dona.loja");
            var merchantEmp = new ClothingStore.Domain.Entities.Employee(merchantUser.Id, "Dona Real", "Lojista", "5511999990000", false, true);
            legacyState.Users.Clear();
            legacyState.Employees.Clear();
            legacyState.Users.AddRange([legacyUser, merchantUser]);
            legacyState.Employees.AddRange([legacyEmp, merchantEmp]);
            File.WriteAllText(withMerchantDbPath, System.Text.Json.JsonSerializer.Serialize(legacyState));

            var promotedRepo = new ClothingStoreRepository(hasher, withMerchantDbPath);
            var remainingEmployees = await promotedRepo.GetEmployeesAsync();
            Assert.AreEqual(1, remainingEmployees.Count);
            Assert.AreEqual("dona.loja", remainingEmployees[0].User?.Username);
            Assert.AreEqual("Admin", remainingEmployees[0].User?.Role?.Name);
            Assert.IsTrue(remainingEmployees[0].CanManageProducts);
            Assert.IsNull(await promotedRepo.GetUserByEmailAsync("admin"));
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void AppUpdateCatalog_CustomManifest_PrivateApiAssetUrl_AndCustomLauncherScript_ShouldWorkAccurately()
    {
        const string manifestJson = """
        {
          "version": "1.0.25",
          "title": "Admin Privado v1.0.25",
          "notes": "Sincronizacao offline e melhorias.",
          "windowsUrl": "AdminApp-Windows-x64-v1.0.25.zip",
          "windowsSizeBytes": 81500000,
          "androidUrl": "https://private.example.invalid/AdminApp-Android-v1.0.25.apk",
          "androidSizeBytes": 35100000
        }
        """;

        var winManifest = AppUpdateCatalog.ParseCustomUpdateManifestJson(
            manifestJson,
            currentVersion: "1.0.18",
            isWindows: true,
            isAndroid: false,
            defaultTitlePrefix: "YouLink Admin",
            defaultFallbackNotes: "Notas padrão");
        Assert.IsNotNull(winManifest);
        Assert.IsTrue(winManifest!.IsUpdateAvailable);
        Assert.AreEqual("1.0.25", winManifest.LatestVersion);
        Assert.AreEqual("AdminApp-Windows-x64-v1.0.25.zip", winManifest.SelectedAssetName);
        Assert.AreEqual("AdminApp-Windows-x64-v1.0.25.zip", winManifest.SelectedDownloadUrl);
        Assert.AreEqual(81500000L, winManifest.SelectedAssetSizeBytes);

        var androidManifest = AppUpdateCatalog.ParseCustomUpdateManifestJson(
            manifestJson,
            currentVersion: "1.0.18",
            isWindows: false,
            isAndroid: true,
            defaultTitlePrefix: "YouLink Admin",
            defaultFallbackNotes: "Notas padrão");
        Assert.IsNotNull(androidManifest);
        Assert.AreEqual("AdminApp-Android-v1.0.25.apk", androidManifest!.SelectedAssetName);

        const string privateGitHubJson = """
        {
          "tag_name": "v1.0.26",
          "name": "Privado v1.0.26",
          "draft": false,
          "assets": [
            {
              "name": "CustomAdmin-Windows-x64.zip",
              "size": 79000000,
              "url": "https://api.github.com/repos/private-org/private-repo/releases/assets/998877",
              "browser_download_url": "https://github.com/private-org/private-repo/releases/download/v1.0.26/CustomAdmin-Windows-x64.zip"
            }
          ]
        }
        """;

        var privateRelease = AppUpdateCatalog.ParseGitHubReleaseJson(
            privateGitHubJson,
            currentVersion: "1.0.18",
            isWindows: true,
            isAndroid: false,
            preferWindowsSetup: false,
            preferApiAssetUrl: true,
            defaultTitlePrefix: "YouLink Admin",
            source: "GitHubPrivado");
        Assert.IsNotNull(privateRelease);
        Assert.AreEqual("CustomAdmin-Windows-x64.zip", privateRelease!.SelectedAssetName);
        Assert.AreEqual(
            "https://api.github.com/repos/private-org/private-repo/releases/assets/998877",
            privateRelease.SelectedApiDownloadUrl);

        var adminScript = AppUpdateCatalog.BuildWindowsApplyUpdateScript(
            targetProcessId: 9876,
            sourceAppDir: @"C:\Temp\AdminStaging",
            targetAppDir: @"C:\Users\Dono\AppData\Local\YouLinkAdmin",
            executableFileName: "ClothingStore.AdminApp.exe",
            logFilePath: @"C:\Temp\admin-update.log",
            launcherFileName: "ClothingStore.AdminApp.exe",
            appDisplayName: "YouLink Admin");
        Assert.IsTrue(adminScript.Contains("YouLink Admin", StringComparison.Ordinal));
        Assert.IsTrue(adminScript.Contains("ClothingStore.AdminApp.exe", StringComparison.Ordinal));
        Assert.IsTrue(adminScript.Contains("'App_Data'", StringComparison.Ordinal));
        Assert.IsTrue(adminScript.Contains("platform_admin_db.json", StringComparison.Ordinal));
        Assert.IsTrue(adminScript.Contains("platform_owner_settings.json", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AdminApp_AutoUpdateSystem_AndPrivacyBoundary_ShouldBeConfiguredProperly()
    {
        var root = ResolveRepoRoot();
        var adminRoot = Path.Combine(root, "src", "ClothingStore.AdminApp");
        if (!Directory.Exists(adminRoot))
            return;

        var modalRazor = File.ReadAllText(Path.Combine(adminRoot, "Components", "Shared", "AdminUpdateModal.razor"));
        var modalCss = File.ReadAllText(Path.Combine(adminRoot, "Components", "Shared", "AdminUpdateModal.razor.css"));
        var mainLayout = File.ReadAllText(Path.Combine(adminRoot, "Components", "Layout", "MainLayout.razor"));
        var loginRazor = File.ReadAllText(Path.Combine(adminRoot, "Components", "Pages", "AdminLogin.razor"));
        var settingsRazor = File.ReadAllText(Path.Combine(adminRoot, "Components", "Pages", "OwnerSettingsPage.razor"));
        var mauiProgram = File.ReadAllText(Path.Combine(adminRoot, "MauiProgram.cs"));
        var appXamlCs = File.ReadAllText(Path.Combine(adminRoot, "App.xaml.cs"));
        var manifestXml = File.ReadAllText(Path.Combine(adminRoot, "Platforms", "Android", "AndroidManifest.xml"));
        var updateService = File.ReadAllText(Path.Combine(adminRoot, "Services", "AdminUpdateService.cs"));

        Assert.IsFalse(modalRazor.Contains("style=\"", StringComparison.OrdinalIgnoreCase),
            "AdminUpdateModal.razor deve usar .razor.css sem estilos inline.");
        Assert.IsTrue(modalCss.Contains("var(--adm-", StringComparison.Ordinal));
        Assert.IsTrue(modalRazor.Contains("Há uma nova atualização disponível", StringComparison.Ordinal));

        var themeStart = mainLayout.IndexOf("<div class=\"@Theme.CurrentThemeClass\">", StringComparison.Ordinal);
        var updateModalPos = mainLayout.IndexOf("AdminUpdateModal", StringComparison.Ordinal);
        var codePos = mainLayout.IndexOf("@code", StringComparison.Ordinal);
        var lastDivBeforeCode = mainLayout.LastIndexOf("</div>", codePos, StringComparison.Ordinal);
        Assert.IsTrue(updateModalPos > themeStart && updateModalPos < lastDivBeforeCode,
            "AdminUpdateModal precisa ficar dentro do wrapper de tema em MainLayout.razor.");
        Assert.IsTrue(mainLayout.Contains("UpdateService.StartBackgroundMonitor()", StringComparison.Ordinal));
        Assert.IsTrue(loginRazor.Contains("Procurar Atualização", StringComparison.Ordinal));
        Assert.IsTrue(settingsRazor.Contains("Atualizações Automáticas do YouLink Admin", StringComparison.Ordinal));
        Assert.IsTrue(mauiProgram.Contains("AddSingleton<AdminUpdateService>()", StringComparison.Ordinal));
        Assert.IsTrue(appXamlCs.Contains("CheckForUpdatesOnResumeAsync", StringComparison.Ordinal));
        Assert.IsTrue(manifestXml.Contains("android.permission.REQUEST_INSTALL_PACKAGES", StringComparison.Ordinal));
        Assert.IsTrue(updateService.Contains("_embeddedSyncServer?.Stop()", StringComparison.Ordinal));
        Assert.IsTrue(updateService.Contains("AppUpdateCatalog.GitHubOwner", StringComparison.Ordinal),
            "AdminUpdateService deve bloquear o repositório público do YouLink cliente para evitar troca indevida de binários.");

        // Segurança: nunca colocar detalhes privados do YouLink Admin no AppUpdateCatalog público
        var publicCatalog = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Shared", "Helpers", "AppUpdateCatalog.cs"));
        Assert.IsFalse(publicCatalog.Contains("ClothingStore.AdminApp", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(publicCatalog.Contains("YouLinkAdmin", StringComparison.OrdinalIgnoreCase));
    }
}
