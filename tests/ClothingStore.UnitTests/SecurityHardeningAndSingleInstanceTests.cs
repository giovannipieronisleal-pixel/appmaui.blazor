using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Common;
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
public class SecurityHardeningAndSingleInstanceTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório.");
    }

    private static string CreateSafeTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static int ReserveFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static byte[] CreateMinimalValidPngBytes() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53
    ];

    [TestMethod]
    public void Windows_SingleInstanceProtection_MustBeEnforcedInBothLauncherAndWinUiApp()
    {
        var root = FindRepoRoot();
        var launcherCpp = File.ReadAllText(Path.Combine(root, "installer", "launcher", "YouLinkLauncher.cpp"), Encoding.UTF8);
        var winAppCs = File.ReadAllText(Path.Combine(root, "Platforms", "Windows", "App.xaml.cs"), Encoding.UTF8);

        Assert.IsTrue(launcherCpp.Contains(@"Local\\YouLink_AppWindow_Mutex_v1", StringComparison.Ordinal));
        Assert.IsTrue(launcherCpp.Contains(@"Local\\YouLink_ActivateWindow_Event_v1", StringComparison.Ordinal));
        Assert.IsTrue(launcherCpp.Contains(@"Local\\YouLink_Launcher_Guard_v1", StringComparison.Ordinal));
        Assert.IsTrue(launcherCpp.Contains("EnumWindows", StringComparison.Ordinal));
        Assert.IsTrue(launcherCpp.Contains("AllowSetForegroundWindow", StringComparison.Ordinal));
        Assert.IsTrue(launcherCpp.Contains("SetForegroundWindow", StringComparison.Ordinal));

        Assert.IsTrue(winAppCs.Contains(@"Local\YouLink_AppWindow_Mutex_v1", StringComparison.Ordinal));
        Assert.IsTrue(winAppCs.Contains(@"Local\YouLink_ActivateWindow_Event_v1", StringComparison.Ordinal));
        Assert.IsTrue(winAppCs.Contains("OnLaunched", StringComparison.Ordinal));
        Assert.IsTrue(winAppCs.Contains("TryAcquireSingleInstance", StringComparison.Ordinal));
        Assert.IsTrue(winAppCs.Contains("ActivateExistingYouLinkWindow", StringComparison.Ordinal));
        Assert.IsTrue(winAppCs.Contains("Environment.Exit(0)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void JwtSecurity_ShouldRejectHardcodedSecret_AlgNone_WrongAudience_And_2FaTokenReuse()
    {
        var tempDirA = CreateSafeTempDir("youlink_jwt_a");
        var tempDirB = CreateSafeTempDir("youlink_jwt_b");
        try
        {
            var cfgA = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:FilePath"] = Path.Combine(tempDirA, "db.json")
                })
                .Build();
            var cfgB = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:FilePath"] = Path.Combine(tempDirB, "db.json")
                })
                .Build();

            var jwtA = new JwtTokenService(cfgA);
            var jwtB = new JwtTokenService(cfgB);

            var role = new Role(Guid.NewGuid(), RoleNames.Admin, "Administrador");
            var user = new User("dona@loja.com", "hash", role.Id, "dona") { Role = role };
            var employee = new Employee(user.Id, "Dona", "Admin", null, true, true);

            var (tokenFromA, _) = jwtA.GenerateToken(user, employee, RoleNames.Admin);
            Assert.IsNotNull(jwtA.ValidateToken(tokenFromA), "A própria instância A deve validar seu token.");
            Assert.IsNull(jwtB.ValidateToken(tokenFromA), "Instâncias distintas nunca podem compartilhar chave padrão.");

            // Token assinado com a antiga chave padrão conhecida no código deve ser rejeitado
            var forgedHeader = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
            var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            var forgedPayload = Base64Url($"{{\"sub\":\"{user.Id}\",\"role\":\"Admin\",\"iss\":\"ClothingStore.Api\",\"aud\":\"ClothingStore.Clients\",\"exp\":{exp}}}");
            var legacySecret = Encoding.UTF8.GetBytes("ClothingStore_Default_Dev_Secret_Key_2026_ChangeInProduction_998877!");
            using var legacyHmac = new HMACSHA256(legacySecret);
            var legacySig = Base64UrlBytes(legacyHmac.ComputeHash(Encoding.UTF8.GetBytes($"{forgedHeader}.{forgedPayload}")));
            Assert.IsNull(jwtA.ValidateToken($"{forgedHeader}.{forgedPayload}.{legacySig}"));

            // Algoritmo "none" precisa ser rejeitado
            var algNoneHeader = Base64Url("""{"alg":"none","typ":"JWT"}""");
            Assert.IsNull(jwtA.ValidateToken($"{algNoneHeader}.{forgedPayload}."));

            // Token de desafio 2FA NÃO pode ser aceito como Bearer token de acesso nem para outro usuário
            var twoFaToken = jwtA.GenerateTwoFactorChallengeToken(user.Id, user.Email);
            Assert.IsNull(jwtA.ValidateToken(twoFaToken));
            Assert.IsTrue(jwtA.ValidateTwoFactorChallengeToken(twoFaToken, user.Id));
            Assert.IsFalse(jwtA.ValidateTwoFactorChallengeToken(twoFaToken, Guid.NewGuid()));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDirA);
            DeleteDirectoryIfExists(tempDirB);
        }
    }

    [TestMethod]
    public async Task UploadAndMediaSecurity_ShouldBlockSvgXss_MagicSpoofing_Oversize_PathTraversal_And_JavascriptUrls()
    {
        var tempDir = CreateSafeTempDir("youlink_upload_sec");
        try
        {
            var webRoot = Path.Combine(tempDir, "wwwroot");
            var outsideFile = Path.Combine(tempDir, "secret.txt");
            await File.WriteAllTextAsync(outsideFile, "nao-apagar");

            var config = new ConfigurationBuilder().Build();
            using var http = new HttpClient();
            var storage = new SupabaseImageStorageService(http, config, webRoot);

            // 1. SVG (extensão ou mimetype) bloqueado
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
            {
                using var svgMs = new MemoryStream(Encoding.UTF8.GetBytes("<svg onload=alert(1)></svg>"));
                await storage.UploadImageAsync("products", "xss.svg", svgMs, "image/svg+xml");
            });

            // 2. HTML/SVG mascarado com extensão .png bloqueado por validação de magic bytes
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
            {
                using var spoofMs = new MemoryStream(Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
                await storage.UploadImageAsync("products", "fake.png", spoofMs, "image/png");
            });

            // 3. Limite máximo de 8 MB imposto
            await Assert.ThrowsExceptionAsync<DomainException>(async () =>
            {
                var huge = new byte[(8 * 1024 * 1024) + 128];
                Array.Copy(CreateMinimalValidPngBytes(), huge, 8);
                using var hugeMs = new MemoryStream(huge);
                await storage.UploadImageAsync("products", "huge.png", hugeMs, "image/png");
            });

            // 4. Path traversal em DeleteImageAsync não apaga arquivo fora de uploads/
            await storage.DeleteImageAsync("../../secret.txt");
            Assert.IsTrue(File.Exists(outsideFile), "Arquivo externo não pode ser atingido via path traversal.");

            // 5. PNG legítimo funciona e sanitiza nome da pasta
            using var validMs = new MemoryStream(CreateMinimalValidPngBytes());
            var (storedPath, publicUrl) = await storage.UploadImageAsync("../../products", "foto.png", validMs, "image/png");
            Assert.IsFalse(storedPath.Contains("..", StringComparison.Ordinal));
            Assert.IsTrue(publicUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal));

            // 6. Bloqueia URLs maliciosas e SSRF de metadados em mídia
            Assert.IsFalse(WhatsAppLinkBuilder.IsSafeMediaUrl("javascript:alert(document.cookie)"));
            Assert.IsFalse(WhatsAppLinkBuilder.IsSafeMediaUrl("data:text/html,<script>alert(1)</script>"));
            Assert.IsFalse(WhatsAppLinkBuilder.IsSafeMediaUrl("data:image/svg+xml;base64,PHN2Zz4="));
            Assert.IsFalse(WhatsAppLinkBuilder.IsSafeMediaUrl("http://169.254.169.254/latest/meta-data/"));
            Assert.IsTrue(WhatsAppLinkBuilder.IsSafeMediaUrl("https://cdn.exemplo.com/vestido.jpg"));
            Assert.IsTrue(WhatsAppLinkBuilder.IsSafeMediaUrl("/uploads/products/vestido.png"));

            // 7. StoreDesignCssHelper bloqueia CSS injection via DisplayFont/BodyFont/PresetKey
            var poisonedDesign = new StoreDesignDto
            {
                PresetKey = "evil; } </style><script>alert(1)</script>",
                DisplayFont = "Arial'; } body{display:none} /*",
                BodyFont = "Evil</style><script>",
                PrimaryColor = "#111111; color:red",
                HeroHeadline = "Oferta\n<br/>Moda"
            };
            var safeCss = StoreDesignCssHelper.BuildRootCssBlock(poisonedDesign);
            Assert.IsFalse(safeCss.Contains("</style>", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(safeCss.Contains("<script>", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(safeCss.Contains("display:none", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
        }
    }

    [TestMethod]
    public async Task AuthSecurity_LockoutInvalidatesOtpTokens_And_InactiveAccountCannotBypass2Fa()
    {
        var tempDir = CreateSafeTempDir("youlink_auth_sec");
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, Path.Combine(tempDir, "db.json"));
            var jwt = new JwtTokenService(new ConfigurationBuilder().Build());
            var auth = new AuthService(repo, hasher, jwt);

            var reg = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Dona Segurança", "sec@youlink.app", "SenhaForte@99", Username: "sec.owner"));

            var setup = await auth.GenerateTwoFactorSetupAsync(reg.Employee.UserId);
            var validTotp = TwoFactorHelper.GenerateCurrentTotp(setup.SecretKey);
            await auth.EnableTwoFactorAsync(reg.Employee.UserId, new EnableTwoFactorRequest(setup.SecretKey, validTotp, setup.BackupCodes));

            // 1. Lockout deve invalidar código de redefinição/login por e-mail ativo
            var user = await repo.GetUserByEmailAsync("sec.owner");
            Assert.IsNotNull(user);
            user!.GeneratePasswordResetToken(hasher.HashToken("123456"), TimeSpan.FromMinutes(30));
            user.GenerateEmailLoginCode(hasher.HashToken("654321"), TimeSpan.FromMinutes(10));
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < User.MaxFailedAttemptsBeforeLockout; i++)
            {
                user.RecordFailedAccessAttempt(now);
            }
            Assert.IsTrue(user.IsLockedOut(now));
            Assert.IsNull(user.ResetTokenHash, "ResetTokenHash precisa ser invalidado após bloqueio.");
            Assert.IsNull(user.EmailLoginCodeHash, "EmailLoginCodeHash precisa ser invalidado após bloqueio.");

            // 2. Desbloquear e desativar conta -> VerifyTwoFactorLoginAsync nunca pode logar conta desativada
            user.ResetFailedAccessAttempts();
            user.IsActive = false;
            var emp = await repo.GetEmployeeByUserIdAsync(user.Id);
            await repo.UpdateEmployeeAsync(emp!, user);

            await Assert.ThrowsExceptionAsync<DomainException>(() =>
                auth.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("sec.owner", TwoFactorHelper.GenerateCurrentTotp(setup.SecretKey))));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
        }
    }

    [TestMethod]
    public async Task LiveApiSecurityAudit_ShouldBlock2FaBypass_EnforceOrderRbac_RejectPublicRadiusBypass_And_RateLimit()
    {
        var root = FindRepoRoot();
        var preferredConfig = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var fallbackConfig = preferredConfig == "Release" ? "Debug" : "Release";

        var apiDir = new[] { preferredConfig, fallbackConfig }
            .Select(cfg => Path.Combine(root, "src", "ClothingStore.Api", "bin", cfg, "net10.0"))
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "ClothingStore.Api.dll")));

        Assert.IsNotNull(apiDir, "ClothingStore.Api compilada não encontrada.");

        var exePath = Path.Combine(apiDir!, "ClothingStore.Api.exe");
        var dllPath = Path.Combine(apiDir!, "ClothingStore.Api.dll");
        var useNativeWinExe = OperatingSystem.IsWindows() && File.Exists(exePath);

        var tempDir = CreateSafeTempDir("youlink_api_sec");
        var dbFilePath = Path.Combine(tempDir, "api_sec_db.json");
        var hasher = new Pbkdf2PasswordHasher();
        var seedRepo = new ClothingStoreRepository(hasher, dbFilePath);
        var seedAuth = new AuthService(seedRepo, hasher, new JwtTokenService(new ConfigurationBuilder().Build()));

        var adminRegSeed = await seedAuth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
            "Dona API", "admin.sec@loja.com", "SenhaForte@1", Username: "admin.sec"));
        var twoFaSetup = await seedAuth.GenerateTwoFactorSetupAsync(adminRegSeed.Employee.UserId);
        await seedAuth.EnableTwoFactorAsync(adminRegSeed.Employee.UserId,
            new EnableTwoFactorRequest(twoFaSetup.SecretKey, TwoFactorHelper.GenerateCurrentTotp(twoFaSetup.SecretKey), twoFaSetup.BackupCodes));

        var empRegSeed = await seedAuth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
            "Operador Comum", "emp.sec@loja.com", "SenhaEmp@1", Username: "emp.sec"));

        var seedStorage = new SupabaseImageStorageService(new HttpClient(), new ConfigurationBuilder().Build(), Path.Combine(tempDir, "wwwroot"));
        var seedSettings = new EmployeeAndSettingsService(seedRepo, hasher, seedStorage);
        await seedSettings.UpdateStoreSettingsAsync(adminRegSeed.Employee.EmployeeId, new UpdateStoreSettingsRequest
        {
            StoreName = "Loja Audit Sec",
            WhatsappNumber = "11999998888",
            StoreDescription = "Teste de segurança",
            IsSiteActive = true,
            PlanTier = StoreSettings.PlanTierPremium,
            DirectPixSalesEnabled = true,
            StorePixKey = "pix@audit.com",
            OriginCep = "01310-100",
            OriginCity = "São Paulo",
            OriginState = "SP",
            DeliveryScopeMode = BrazilShippingCalculator.DeliveryScopeModes.SameCity
        });

        var seedProdService = new ProductManagementService(seedRepo, seedStorage);
        var seedCat = await seedProdService.CreateCategoryAsync(new SaveCategoryRequest { Name = "Vestidos" });
        var seedProd = new Product(seedCat.Id, "Vestido Audit", "Peça teste", 250m, true);
        var seedVariant = seedProd.AddVariant("Preto", "M", "#111111", "AUD-01", 2);
        var stockIn = seedVariant.ApplyStockMovement(adminRegSeed.Employee.EmployeeId, StockMovementType.In, 10, "Estoque audit");
        await seedRepo.AddProductAsync(seedProd);
        await seedRepo.RecordStockMovementAsync(seedVariant, stockIn);
        var seedVariantId = seedVariant.Id;

        var port = ReserveFreeLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo
        {
            FileName = useNativeWinExe ? exePath : "dotnet",
            Arguments = useNativeWinExe ? string.Empty : $"\"{dllPath}\"",
            WorkingDirectory = tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["ASPNETCORE_URLS"] = baseUrl;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["Database__FilePath"] = dbFilePath;
        psi.Environment["Jwt__SecretKey"] = "IntegrationSecurityAuditSecretKey_1234567890!";
        psi.Environment["DOTNET_PRINT_TELEMETRY_MESSAGE"] = "false";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Falha ao iniciar ClothingStore.Api para auditoria.");

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };

            HttpResponseMessage? statusResp = null;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (process.HasExited)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"ClothingStore.Api encerrou ({process.ExitCode}): {stderr}");
                }

                try
                {
                    statusResp = await http.GetAsync("/api/cloud/status");
                    if (statusResp.IsSuccessStatusCode)
                        break;
                }
                catch
                {
                }
                await Task.Delay(150);
            }

            Assert.IsNotNull(statusResp);
            Assert.IsTrue(statusResp!.Headers.TryGetValues("X-Frame-Options", out var xfo) && xfo.Contains("DENY"));
            Assert.IsTrue(statusResp.Headers.TryGetValues("X-Content-Type-Options", out var xcto) && xcto.Contains("nosniff"));

            // 1. Anônimo não pode usar /api/cloud/check-domain como SSRF/DNS probe
            var anonDomainProbe = await http.PostAsJsonAsync("/api/cloud/check-domain", new CheckDomainAvailabilityRequest { Domain = "exemplo.com" });
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonDomainProbe.StatusCode);

            // 2. Fluxo 2FA via HTTP bloqueia /verify-2fa sem ChallengeToken (bypass sem senha) e aceita fluxo válido
            var bypass2Fa = await http.PostAsJsonAsync("/api/auth/verify-2fa",
                new VerifyTwoFactorLoginRequest("admin.sec", TwoFactorHelper.GenerateCurrentTotp(twoFaSetup.SecretKey)));
            Assert.AreEqual(HttpStatusCode.BadRequest, bypass2Fa.StatusCode);

            var challengeResp = await http.PostAsJsonAsync("/api/auth/login-challenge", new LoginRequest("admin.sec", "SenhaForte@1"));
            Assert.AreEqual(HttpStatusCode.OK, challengeResp.StatusCode);
            var challenge = (await challengeResp.Content.ReadFromJsonAsync<LoginChallengeResponse>())!;
            Assert.IsTrue(challenge.RequiresTwoFactor);
            Assert.IsFalse(string.IsNullOrWhiteSpace(challenge.ChallengeToken));

            var legit2Fa = await http.PostAsJsonAsync("/api/auth/verify-2fa",
                new VerifyTwoFactorLoginRequest("admin.sec", TwoFactorHelper.GenerateCurrentTotp(twoFaSetup.SecretKey), challenge.ChallengeToken));
            Assert.AreEqual(HttpStatusCode.OK, legit2Fa.StatusCode);
            var adminLogin = (await legit2Fa.Content.ReadFromJsonAsync<LoginResponse>())!;

            // Login do operador comum de baixo privilégio
            var empLoginResp = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("emp.sec", "SenhaEmp@1"));
            Assert.AreEqual(HttpStatusCode.OK, empLoginResp.StatusCode);
            var empLogin = (await empLoginResp.Content.ReadFromJsonAsync<LoginResponse>())!;

            // 3. Colaborador sem permissão CanManageStock não pode acessar pedidos, PII de compradores nem relatório financeiro
            using (var empOrdersReq = new HttpRequestMessage(HttpMethod.Get, "/api/orders"))
            {
                empOrdersReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", empLogin.AccessToken);
                var empOrdersResp = await http.SendAsync(empOrdersReq);
                Assert.AreEqual(HttpStatusCode.Forbidden, empOrdersResp.StatusCode);
            }
            using (var empSalesReq = new HttpRequestMessage(HttpMethod.Get, "/api/orders/sales-report"))
            {
                empSalesReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", empLogin.AccessToken);
                var empSalesResp = await http.SendAsync(empSalesReq);
                Assert.AreEqual(HttpStatusCode.Forbidden, empSalesResp.StatusCode);
            }
            using (var empFulfillReq = new HttpRequestMessage(HttpMethod.Put, $"/api/orders/{Guid.NewGuid()}/fulfillment"))
            {
                empFulfillReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", empLogin.AccessToken);
                empFulfillReq.Content = JsonContent.Create(new UpdateOrderFulfillmentRequest
                {
                    FulfillmentMethod = "Correios",
                    TrackingCode = "BR123",
                    AgreedShippingCost = 10m,
                    MarkAsShipped = true,
                    MarkAsDelivered = true
                });
                var empFulfillResp = await http.SendAsync(empFulfillReq);
                Assert.AreEqual(HttpStatusCode.Forbidden, empFulfillResp.StatusCode);
            }

            // 3b. Endpoint público de pedido Pix força ExpressMobileMode = false (não aceita pular CEP/raio)
            var bypassRadiusOrder = await http.PostAsJsonAsync("/api/public/orders/pix", new CreatePixOrderRequest
            {
                ProductId = seedProd.Id,
                ProductVariantId = seedVariantId,
                Quantity = 1,
                CustomerName = "Comprador Externo",
                CustomerWhatsapp = "11999998888",
                ExpressMobileMode = true
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, bypassRadiusOrder.StatusCode);

            // Admin pode acessar pedidos e relatório
            using (var adminOrdersReq = new HttpRequestMessage(HttpMethod.Get, "/api/orders"))
            {
                adminOrdersReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);
                var adminOrdersResp = await http.SendAsync(adminOrdersReq);
                Assert.AreEqual(HttpStatusCode.OK, adminOrdersResp.StatusCode);
            }

            // 4. Rate limiter bloqueia burst excessivo em /api/auth/login retornando 429 TooManyRequests
            var hit429 = false;
            for (var i = 0; i < 35; i++)
            {
                var burstResp = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("inexistente@loja.com", "errada"));
                if (burstResp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    hit429 = true;
                    break;
                }
            }
            Assert.IsTrue(hit429, "Rate limiter precisa retornar HTTP 429 em rajadas repetidas de login.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
            }

            DeleteDirectoryIfExists(tempDir);
        }
    }

    private static string Base64Url(string text) => Base64UrlBytes(Encoding.UTF8.GetBytes(text));

    private static string Base64UrlBytes(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
