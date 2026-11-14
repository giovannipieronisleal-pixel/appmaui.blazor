using System.Net;
using System.Net.Http;
using System.Text;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
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
public class MobileMenuSecurityAndRenderTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
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

        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório.");
    }

    [TestMethod]
    public void LoginRazor_ShouldNotExposeCentralServerOrAdminConnectionPanel()
    {
        var root = FindRepoRoot();
        var loginRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"), Encoding.UTF8);

        Assert.IsFalse(loginRazor.Contains("Conectar ao Servidor Central", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(loginRazor.Contains("ConnectServerFromLoginAsync", StringComparison.Ordinal));
        Assert.IsFalse(loginRazor.Contains("ConfigureServerUrlAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MobileNavMenu_ShouldUseDeepScopedAccessibleCards_AndLargerElderFriendlyLabels()
    {
        var root = FindRepoRoot();
        var navRazor = File.ReadAllText(Path.Combine(root, "Components", "Layout", "NavMenu.razor"), Encoding.UTF8);
        var navCss = File.ReadAllText(Path.Combine(root, "Components", "Layout", "NavMenu.razor.css"), Encoding.UTF8);
        var appCss = File.ReadAllText(Path.Combine(root, "wwwroot", "app.css"), Encoding.UTF8);

        // ::deep garante que os links gerados por <NavLink> herdem o CSS sem sublinhado azul padrão do navegador
        Assert.IsTrue(navCss.Contains(".adm-mobile-sheet-grid ::deep a.adm-mobile-sheet-link", StringComparison.Ordinal));
        Assert.IsTrue(navCss.Contains("text-decoration: none !important;", StringComparison.Ordinal));
        Assert.IsTrue(navCss.Contains("min-height: 64px;", StringComparison.Ordinal));
        Assert.IsTrue(navCss.Contains("font-size: 1.06rem;", StringComparison.Ordinal));
        Assert.IsTrue(navCss.Contains("width: 48px;", StringComparison.Ordinal));

        Assert.IsTrue(navRazor.Contains("adm-mobile-sheet-item-title", StringComparison.Ordinal));
        Assert.IsTrue(navRazor.Contains("adm-mobile-sheet-item-desc", StringComparison.Ordinal));
        Assert.IsTrue(navRazor.Contains("adm-sidebar-account-name-row", StringComparison.Ordinal));
        Assert.IsTrue(navRazor.Contains("adm-sidebar-role-badge", StringComparison.Ordinal));
        Assert.IsTrue(navCss.Contains(".adm-sidebar-acc-btn", StringComparison.Ordinal));
        Assert.IsTrue(navCss.Contains("appearance: none;", StringComparison.Ordinal));

        var depth = 0;
        foreach (var rawLine in navCss.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            {
                Assert.AreEqual(0, depth, $"@media não pode ficar aninhado dentro de outro bloco CSS: {trimmed}");
            }
            depth += trimmed.Count(c => c == '{') - trimmed.Count(c => c == '}');
        }

        Assert.IsTrue(appCss.Contains("min-height: 54px;", StringComparison.Ordinal));
        Assert.IsTrue(appCss.Contains("font-size: 0.82rem;", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnpaidGiftedPlan_Redemption_ShouldHostOnOwnerSharedServer_AndAllowFreeOrPurchasedDomainChoice()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"youlink_gifted_{Guid.NewGuid():N}.json");
        var tempUploads = Path.Combine(Path.GetTempPath(), $"youlink_gifted_up_{Guid.NewGuid():N}");

        try
        {
            var config = new ConfigurationBuilder().Build();
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, tempFile);

            var preSettings = await repo.GetStoreSettingsAsync();
            preSettings.OnlineBaseUrl = "https://servidor-antigo.onrender.com";
            preSettings.ConfigureServerHosting("DedicatedOwnServer", "Render", "segredo-antigo", false, null);
            await repo.UpdateStoreSettingsAsync(preSettings);

            var courtesyCode = PlanActivationCode.Create(
                targetPlanTier: StoreSettings.PlanTierPremium,
                durationDays: 30,
                isGiftedUnpaid: true,
                suggestedDomainName: "boutiqueluz.duckdns.org");

            Assert.IsTrue(courtesyCode.IsGiftedUnpaid);
            Assert.AreEqual(PlanActivationCode.BillingKindGiftedUnpaid, courtesyCode.PlanBillingKind);
            await repo.SavePlanActivationCodeAsync(courtesyCode);

            var imgStorage = new SupabaseImageStorageService(new HttpClient(), config, tempUploads);
            var service = new EmployeeAndSettingsService(repo, hasher, imgStorage);

            var redeemed = await service.RedeemActivationCodeAsync(new RedeemActivationCodeRequest { Code = courtesyCode.Code });

            Assert.AreEqual("PlatformShared", redeemed.UpdatedSettings.ServerHostingMode);
            Assert.AreEqual(PlanActivationCode.BillingKindGiftedUnpaid, redeemed.UpdatedSettings.ActivePlanBillingKind);
            Assert.AreEqual("https://boutiqueluz.duckdns.org", redeemed.UpdatedSettings.OnlineBaseUrl);
            Assert.AreEqual(StoreSettings.DomainModeFreeDomain, redeemed.UpdatedSettings.DomainAcquisitionMode);
            Assert.IsFalse(redeemed.UpdatedSettings.HasOwnServerSyncSecret);

            // Deve permitir trocar livremente para um domínio próprio comprado depois, mantendo hospedagem na conta do dono
            var switchedToPurchased = await service.UpdateStoreSettingsAsync(
                null,
                new UpdateStoreSettingsRequest
                {
                    StoreName = redeemed.UpdatedSettings.StoreName,
                    StoreSlug = redeemed.UpdatedSettings.StoreSlug,
                    WhatsappNumber = redeemed.UpdatedSettings.WhatsappNumber,
                    StoreDescription = redeemed.UpdatedSettings.StoreDescription,
                    DefaultLowStockThreshold = redeemed.UpdatedSettings.DefaultLowStockThreshold,
                    OnlineBaseUrl = "https://www.boutiqueluz.com.br",
                    DomainAcquisitionMode = StoreSettings.DomainModePurchasedDomain
                });

            Assert.AreEqual("PlatformShared", switchedToPurchased.ServerHostingMode);
            Assert.AreEqual(StoreSettings.DomainModePurchasedDomain, switchedToPurchased.DomainAcquisitionMode);
            Assert.AreEqual("https://www.boutiqueluz.com.br", switchedToPurchased.OnlineBaseUrl);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (Directory.Exists(tempUploads)) Directory.Delete(tempUploads, recursive: true);
        }
    }

    [TestMethod]
    public async Task RenderProvisioningHelper_ShouldDiscoverExistingService_OrCreateNewServiceViaApi()
    {
        // Cenário 1: Serviço já existe no Render -> encontra URL direto
        var existingHandler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/v1/services", StringComparison.Ordinal))
            {
                const string json = "[{\"cursor\":\"c1\",\"service\":{\"id\":\"srv-123\",\"name\":\"youlink-atelier\",\"type\":\"web_service\",\"repo\":\"https://github.com/giovannipieronisleal-pixel/yourlink\",\"serviceDetails\":{\"url\":\"https://youlink-atelier.onrender.com/\"}}}]";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var existingClient = new HttpClient(existingHandler);
        var discovered = await RenderProvisioningHelper.DiscoverOrCreateYouLinkServiceAsync(
            "rnd_testkey_1234567890",
            "Atelier Elegância",
            null,
            existingClient);

        Assert.IsTrue(discovered.Success);
        Assert.IsFalse(discovered.CreatedNewService);
        Assert.AreEqual("https://youlink-atelier.onrender.com", discovered.ServiceUrl);

        // Cenário 2: Nenhum serviço ainda -> consulta /owners e cria via POST /services
        var createHandler = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/v1/services", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Get && path.EndsWith("/v1/owners", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"cursor\":\"o1\",\"owner\":{\"id\":\"usr-999\",\"name\":\"Loja\",\"type\":\"user\"}}]", Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/v1/services", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"service\":{\"id\":\"srv-new\",\"name\":\"youlink-nova-loja\",\"serviceDetails\":{\"url\":\"https://youlink-nova-loja.onrender.com\"}}}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        using var createClient = new HttpClient(createHandler);
        var created = await RenderProvisioningHelper.DiscoverOrCreateYouLinkServiceAsync(
            "rnd_testkey_1234567890",
            "Nova Loja",
            null,
            createClient);

        Assert.IsTrue(created.Success);
        Assert.IsTrue(created.CreatedNewService);
        Assert.AreEqual("https://youlink-nova-loja.onrender.com", created.ServiceUrl);
        Assert.IsFalse(string.IsNullOrWhiteSpace(created.GeneratedSyncSecret));
    }

    [TestMethod]
    public async Task AdminApiClient_TestServerConnection_ShouldNotExposeRawOperationCanceledMessage()
    {
        var timeoutHandler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("OperationCanceled"));
        using var httpClient = new HttpClient(timeoutHandler);
        var apiClient = new AdminApiClient(httpClient);

        var result = await apiClient.TestServerConnectionAsync("https://servidor-offline.invalid");
        Assert.IsFalse(result.Reachable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.IsFalse(result.ErrorMessage!.Contains("OperationCanceled", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AppUpdate_RedirectFallback_VersionNormalization_And_ResumeHooks_ShouldWork()
    {
        // Normalização de versão unpackaged do Windows (1.0.12.0 -> 1.0.12) evita falsa diferença vs tag GitHub
        Assert.AreEqual("1.0.12", AppUpdateCatalog.NormalizeVersion("1.0.12.0"));
        Assert.AreEqual("1.0.13", AppUpdateCatalog.NormalizeVersion("v1.0.13-rc1"));
        Assert.IsFalse(AppUpdateCatalog.IsVersionNewer("1.0.12", "1.0.12.0"));
        Assert.IsFalse(AppUpdateCatalog.IsVersionNewer("1.0.12.0", "1.0.12"));
        Assert.IsTrue(AppUpdateCatalog.IsVersionNewer("1.0.13", "1.0.12.0"));

        var androidRedirect = AppUpdateCatalog.ParseGitHubRedirectLocation(
            "https://github.com/giovannipieronisleal-pixel/yourlink/releases/tag/v1.0.13",
            "1.0.11",
            isWindows: false,
            isAndroid: true);
        Assert.IsNotNull(androidRedirect);
        Assert.IsTrue(androidRedirect!.IsUpdateAvailable);
        Assert.AreEqual("1.0.13", androidRedirect.LatestVersion);
        Assert.AreEqual("YouLink-Android.apk", androidRedirect.SelectedAssetName);
        Assert.AreEqual("GitHubRedirect", androidRedirect.Source);

        var setupRedirect = AppUpdateCatalog.ParseGitHubRedirectLocation(
            "/giovannipieronisleal-pixel/yourlink/releases/tag/v1.0.13",
            "1.0.12.0",
            isWindows: true,
            isAndroid: false,
            preferWindowsSetup: true);
        Assert.IsNotNull(setupRedirect);
        Assert.IsTrue(setupRedirect!.IsUpdateAvailable);
        Assert.AreEqual("YouLink-Setup-x64.exe", setupRedirect.SelectedAssetName);

        var root = FindRepoRoot();
        var appXamlCs = File.ReadAllText(Path.Combine(root, "App.xaml.cs"), Encoding.UTF8);
        var updateServiceCs = File.ReadAllText(Path.Combine(root, "Services", "AppUpdateService.cs"), Encoding.UTF8);
        var mainLayoutRazor = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"), Encoding.UTF8);
        var loginRazor = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Login.razor"), Encoding.UTF8);

        Assert.IsTrue(appXamlCs.Contains("CheckForUpdatesOnResumeAsync", StringComparison.Ordinal));
        Assert.IsTrue(appXamlCs.Contains("Activated", StringComparison.Ordinal));
        Assert.IsTrue(appXamlCs.Contains("Resumed", StringComparison.Ordinal));
        Assert.IsTrue(updateServiceCs.Contains("TryResolveReleaseViaGitHubRedirectAsync", StringComparison.Ordinal));
        Assert.IsTrue(updateServiceCs.Contains("StartBackgroundMonitor", StringComparison.Ordinal));
        Assert.IsTrue(mainLayoutRazor.Contains("StartBackgroundMonitor", StringComparison.Ordinal));
        Assert.IsTrue(loginRazor.Contains("CheckUpdatesFromLoginAsync", StringComparison.Ordinal));
        Assert.IsTrue(loginRazor.Contains("UpdateService.OpenPopup", StringComparison.Ordinal));
    }
}
