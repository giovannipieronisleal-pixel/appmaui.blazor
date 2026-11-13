using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
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
public class GoogleAccountAndOwnServerTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
                RequestedUris.Add(request.RequestUri);

            return Task.FromResult(responder(request));
        }
    }

    private static (ClothingStoreRepository Repo, string TempDir, string DbPath, Pbkdf2PasswordHasher Hasher, SupabaseImageStorageService Storage) CreateIsolatedRepo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_google_tests_{Guid.NewGuid():N}");
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

    [TestMethod]
    public void ClassifyQuotaAndDomain_ShouldDistinguishFree15Gb_Basic100Gb_Pro2Tb_AndWorkspace()
    {
        var free15 = GoogleAccountTierVerifier.ClassifyQuotaAndDomain(
            "gratis@gmail.com",
            "Usuário Grátis",
            null,
            15L * 1024 * 1024 * 1024);
        Assert.AreEqual(GoogleAccountTierVerifier.TierFree15Gb, free15.DetectedTier);
        Assert.IsFalse(free15.IsPaidGoogleAccount);
        Assert.IsFalse(free15.QualifiesForDedicatedServerBenefit);

        var basic100 = GoogleAccountTierVerifier.ClassifyQuotaAndDomain(
            "one100@gmail.com",
            "Usuário 100GB",
            null,
            100L * 1024 * 1024 * 1024);
        Assert.AreEqual(GoogleAccountTierVerifier.TierGoogleOneBasic, basic100.DetectedTier);
        Assert.IsTrue(basic100.IsPaidGoogleAccount);
        Assert.IsFalse(basic100.QualifiesForDedicatedServerBenefit);

        var pro2Tb = GoogleAccountTierVerifier.ClassifyQuotaAndDomain(
            "aipro@gmail.com",
            "Usuário AI Pro",
            null,
            2L * 1024 * 1024 * 1024 * 1024);
        Assert.AreEqual(GoogleAccountTierVerifier.TierGoogleOneProOrHigher, pro2Tb.DetectedTier);
        Assert.IsTrue(pro2Tb.IsPaidGoogleAccount);
        Assert.IsTrue(pro2Tb.QualifiesForDedicatedServerBenefit);
        Assert.IsTrue(pro2Tb.QuotaSummary.Contains("2 TB", StringComparison.OrdinalIgnoreCase));

        var workspace = GoogleAccountTierVerifier.ClassifyQuotaAndDomain(
            "contato@minhaloja.com.br",
            "Loja Corporativa",
            "minhaloja.com.br",
            null);
        Assert.AreEqual(GoogleAccountTierVerifier.TierWorkspace, workspace.DetectedTier);
        Assert.IsTrue(workspace.IsPaidGoogleAccount);
        Assert.IsTrue(workspace.QualifiesForDedicatedServerBenefit);
        Assert.AreEqual("minhaloja.com.br", workspace.HostedDomain);
    }

    [TestMethod]
    public async Task VerifyWithAccessTokenAsync_ShouldParseUserinfoAndDriveQuota_AndHandleErrors()
    {
        using var okHandler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("oauth2/v3/userinfo", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"email":"giovani.pro@gmail.com","name":"Giovani Pro","email_verified":true}""", Encoding.UTF8, "application/json")
                };
            }

            if (url.Contains("drive/v3/about", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"storageQuota":{"limit":"2199023255552","usage":"1073741824"}}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var okClient = new HttpClient(okHandler);

        var okResult = await GoogleAccountTierVerifier.VerifyWithAccessTokenAsync("Bearer ya29.valid-test-token-123", okClient);
        Assert.IsTrue(okResult.Success);
        Assert.AreEqual("giovani.pro@gmail.com", okResult.Email);
        Assert.AreEqual(GoogleAccountTierVerifier.TierGoogleOneProOrHigher, okResult.DetectedTier);
        Assert.IsTrue(okResult.QualifiesForDedicatedServerBenefit);
        Assert.AreEqual(2199023255552L, okResult.DriveQuotaBytes);

        // Token inválido (401)
        using var badHandler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"Invalid Credentials"}}""", Encoding.UTF8, "application/json")
            });
        using var badClient = new HttpClient(badHandler);
        var badResult = await GoogleAccountTierVerifier.VerifyWithAccessTokenAsync("ya29.invalid-token-1234", badClient);
        Assert.IsFalse(badResult.Success);
        Assert.AreEqual(GoogleAccountTierVerifier.TierNone, badResult.DetectedTier);

        // Falta escopo drive.readonly sem Workspace (403)
        using var noScopeHandler = new StubHttpMessageHandler(req =>
            req.RequestUri!.ToString().Contains("userinfo", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"email":"user@gmail.com","name":"User"}""", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var noScopeClient = new HttpClient(noScopeHandler);
        var noScopeResult = await GoogleAccountTierVerifier.VerifyWithAccessTokenAsync("ya29.noscope-token-1234", noScopeClient);
        Assert.IsFalse(noScopeResult.Success);
        Assert.IsTrue(noScopeResult.Message.Contains("drive.readonly", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PkceAndAuthorizationUrl_ShouldGenerateS256ChallengeAndValidGoogleAuthUrl()
    {
        var pkce = GoogleAccountTierVerifier.CreatePkcePair();
        Assert.IsFalse(string.IsNullOrWhiteSpace(pkce.CodeVerifier));
        Assert.IsFalse(string.IsNullOrWhiteSpace(pkce.CodeChallenge));
        Assert.IsFalse(string.IsNullOrWhiteSpace(pkce.State));

        var expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes(pkce.CodeVerifier));
        var expectedChallenge = Convert.ToBase64String(expectedHash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(expectedChallenge, pkce.CodeChallenge);

        var authUrl = GoogleAccountTierVerifier.BuildAuthorizationUrl(
            "123456-abc.apps.googleusercontent.com",
            "http://127.0.0.1:54321/callback/",
            pkce.CodeChallenge,
            pkce.State);

        Assert.IsTrue(authUrl.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", StringComparison.Ordinal));
        Assert.IsTrue(authUrl.Contains("code_challenge_method=S256", StringComparison.Ordinal));
        Assert.IsTrue(authUrl.Contains("drive.readonly", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(authUrl.Contains($"state={Uri.EscapeDataString(pkce.State)}", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CalculatePlanBenefits_ShouldCombineFreeDomain_OwnServer_AndGooglePro_WithFloorCap()
    {
        var fullBenefits = GoogleAccountTierVerifier.CalculatePlanBenefits(
            129.90m,
            usesFreeDomain: true,
            GoogleAccountTierVerifier.TierGoogleOneProOrHigher,
            usesDedicatedOwnServer: true);

        Assert.AreEqual(10.00m, fullBenefits.FreeDomainDiscount);
        Assert.AreEqual(10.00m, fullBenefits.OwnServerDiscount);
        Assert.AreEqual(5.00m, fullBenefits.GoogleTierDiscount);
        Assert.AreEqual(25.00m, fullBenefits.TotalMonthlyDiscount);
        Assert.AreEqual(104.90m, fullBenefits.FinalMonthlyPrice);

        var basicoBenefits = GoogleAccountTierVerifier.CalculatePlanBenefits(
            49.90m,
            usesFreeDomain: true,
            GoogleAccountTierVerifier.TierWorkspace,
            usesDedicatedOwnServer: true);
        Assert.AreEqual(24.90m, basicoBenefits.FinalMonthlyPrice);
        Assert.AreEqual(25.00m, basicoBenefits.TotalMonthlyDiscount);
    }

    [TestMethod]
    public async Task StoreSettings_And_Service_ShouldPersistDedicatedOwnServer_AndPreserveSyncSecretAcrossUpdates()
    {
        var (repo, tempDir, dbPath, hasher, storage) = CreateIsolatedRepo();
        try
        {
            var service = new EmployeeAndSettingsService(repo, hasher, storage);

            await Assert.ThrowsExceptionAsync<DomainException>(() =>
                service.UpdateStoreSettingsAsync(null, new UpdateStoreSettingsRequest
                {
                    StoreName = "Boutique Teste",
                    WhatsappNumber = "5511999999999",
                    StoreDescription = "Loja de roupas teste",
                    OnlineBaseUrl = string.Empty,
                    ServerHostingMode = GoogleAccountTierVerifier.HostingModeDedicatedOwnServer,
                    OwnServerProvider = "GoogleCloudRun"
                }));

            var updated = await service.UpdateStoreSettingsAsync(null, new UpdateStoreSettingsRequest
            {
                StoreName = "Boutique Nuvem Própria",
                WhatsappNumber = "5511999999999",
                StoreDescription = "Boutique com servidor próprio",
                OnlineBaseUrl = "https://boutique.run.app",
                ServerHostingMode = GoogleAccountTierVerifier.HostingModeDedicatedOwnServer,
                OwnServerProvider = "Render",
                OwnServerSyncSecret = "chave-secreta-sync-2026",
                GoogleOAuthClientId = "999-test.apps.googleusercontent.com"
            });

            Assert.AreEqual("DedicatedOwnServer", updated.ServerHostingMode);
            Assert.AreEqual("Render", updated.OwnServerProvider);
            Assert.IsTrue(updated.HasOwnServerSyncSecret);
            Assert.AreEqual("999-test.apps.googleusercontent.com", updated.GoogleOAuthClientId);

            // Salvar outra configuração sem reenviar OwnServerSyncSecret deve preservar a chave existente
            var preserved = await service.UpdateStoreSettingsAsync(null, new UpdateStoreSettingsRequest
            {
                StoreName = "Boutique Nuvem Própria II",
                WhatsappNumber = "5511999999999",
                StoreDescription = "Boutique com servidor próprio",
                OnlineBaseUrl = "https://boutique.run.app"
            });
            Assert.IsTrue(preserved.HasOwnServerSyncSecret);

            // Recarregar repositório do zero a partir do JSON
            var reloadedRepo = new ClothingStoreRepository(hasher, dbPath);
            var reloadedSettings = await reloadedRepo.GetStoreSettingsAsync();
            Assert.AreEqual("DedicatedOwnServer", reloadedSettings.ServerHostingMode);
            Assert.AreEqual("Render", reloadedSettings.OwnServerProvider);
            Assert.AreEqual("chave-secreta-sync-2026", reloadedSettings.OwnServerSyncSecret);

            // Limpar chave explícita
            var cleared = await service.UpdateStoreSettingsAsync(null, new UpdateStoreSettingsRequest
            {
                StoreName = "Boutique Nuvem Própria II",
                WhatsappNumber = "5511999999999",
                StoreDescription = "Boutique com servidor próprio",
                OnlineBaseUrl = "https://boutique.run.app",
                ClearOwnServerSyncSecret = true
            });
            Assert.IsFalse(cleared.HasOwnServerSyncSecret);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task VerifyAndSaveGoogleAccountAsync_ShouldPersistVerifiedGoogleTierAcrossRepoReload_AndAllowClear()
    {
        var (repo, tempDir, dbPath, hasher, storage) = CreateIsolatedRepo();
        try
        {
            var service = new EmployeeAndSettingsService(repo, hasher, storage);

            using var handler = new StubHttpMessageHandler(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("oauth2/v3/userinfo", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"email":"dona.pro@gmail.com","name":"Dona Pro"}""", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"storageQuota":{"limit":"2199023255552","usage":"500000"}}""", Encoding.UTF8, "application/json")
                };
            });
            using var http = new HttpClient(handler);

            var verifyResult = await service.VerifyAndSaveGoogleAccountAsync(
                null,
                "ya29.test-google-pro-token",
                "123-client.apps.googleusercontent.com",
                http);

            Assert.IsTrue(verifyResult.Success);
            Assert.IsTrue(verifyResult.QualifiesForDedicatedServerBenefit);

            var reloadedRepo = new ClothingStoreRepository(hasher, dbPath);
            var reloadedSettings = await reloadedRepo.GetStoreSettingsAsync();
            Assert.AreEqual("dona.pro@gmail.com", reloadedSettings.GoogleAccountEmail);
            Assert.AreEqual(GoogleAccountTierVerifier.TierGoogleOneProOrHigher, reloadedSettings.GoogleAccountTier);
            Assert.AreEqual(2199023255552L, reloadedSettings.GoogleDriveQuotaBytes);
            Assert.IsTrue(reloadedSettings.QualifiesForDedicatedServerBenefit());

            var afterClear = await service.ClearGoogleAccountVerificationAsync(null);
            Assert.IsNull(afterClear.GoogleAccountEmail);
            Assert.AreEqual(GoogleAccountTierVerifier.TierNone, afterClear.GoogleAccountTier);
            Assert.IsFalse(afterClear.QualifiesForDedicatedServerBenefit);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }
}
