using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
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
public class SyncCompatAndMobileCheckoutTests
{
    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastHeaders.Clear();
            foreach (var h in request.Headers)
                LastHeaders[h.Key] = string.Join(",", h.Value);
            return Task.FromResult(responder(request));
        }
    }

    private static (ClothingStoreRepository Repo, string TempDir, string DbPath, Pbkdf2PasswordHasher Hasher, SupabaseImageStorageService Storage) CreateIsolatedEnv()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"youlink_sync_compat_{Guid.NewGuid():N}");
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

    private static int ReserveFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<Guid> EnsureAdminEmployeeIdAsync(ClothingStoreRepository repo, Pbkdf2PasswordHasher hasher)
    {
        var employees = await repo.GetEmployeesAsync();
        if (employees.Count > 0)
            return employees[0].Id;

        var jwt = new JwtTokenService(new ConfigurationBuilder().Build());
        var auth = new AuthService(repo, hasher, jwt);
        var created = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
            "Dona Teste",
            "dona.sync@youlink.app",
            "1234",
            Username: "dona.sync"));
        return created.Employee.EmployeeId;
    }

    [TestMethod]
    public async Task ExpressMobileCheckout_ShouldCompletePixOrderWithoutCep_AndConfirmDirectlyInApp()
    {
        var (repo, tempDir, _, hasher, storage) = CreateIsolatedEnv();
        try
        {
            var adminEmpId = await EnsureAdminEmployeeIdAsync(repo, hasher);

            var catService = new ProductManagementService(repo, storage);
            var cat = await catService.CreateCategoryAsync(new SaveCategoryRequest { Name = "Vestidos" });
            var product = new Product(cat.Id, "Vestido Midi Solar", "Algodão leve", 189.90m, true);
            var variant = product.AddVariant("Terracota", "P", "#B45309", "VST-TER-P", 2);
            var stockIn = variant.ApplyStockMovement(adminEmpId, StockMovementType.In, 5, "Estoque inicial");
            await repo.AddProductAsync(product);
            await repo.RecordStockMovementAsync(variant, stockIn);

            var settingsService = new EmployeeAndSettingsService(repo, hasher, storage);
            await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = "Atelier Direto",
                StoreSlug = "atelier-direto",
                WhatsappNumber = "5511999990000",
                StoreDescription = "Venda direta pelo celular",
                PlanTier = StoreSettings.PlanTierIntermediate,
                DirectPixSalesEnabled = true,
                StorePixKey = "pix@atelierdireto.com.br",
                StorePixReceiverName = "Atelier Direto LTDA",
                OriginCep = "01310-100",
                OriginCity = "São Paulo",
                OriginState = "SP"
            });

            var orderService = new OrderAndShippingService(repo);
            var created = await orderService.PlacePixOrderAsync(new CreatePixOrderRequest
            {
                ProductId = product.Id,
                ProductVariantId = variant.Id,
                Quantity = 1,
                CustomerName = "Camila Santos",
                CustomerWhatsapp = "11988887777",
                ExpressMobileMode = true
            });

            Assert.IsTrue(created.OrderNumber.StartsWith("YL-", StringComparison.Ordinal));
            Assert.AreEqual(189.90m, created.TotalPixAmount);
            Assert.AreEqual("pix@atelierdireto.com.br", created.PixKey);
            Assert.IsTrue(PixPayloadBuilder.VerifyCrc16Ccitt(created.PixBrCode));
            Assert.IsFalse(created.PixConfirmedAt.HasValue);

            var confirmed = await orderService.ConfirmPixPaymentAsync(created.Id, adminEmpId);
            Assert.IsTrue(confirmed.PixConfirmedAt.HasValue);

            var productAfter = await repo.GetProductByIdAsync(product.Id);
            Assert.IsNotNull(productAfter);
            Assert.AreEqual(4, productAfter!.Variants.First().StockQuantity);

            var salesReport = await orderService.GetSalesReportAsync("all");
            Assert.AreEqual(189.90m, salesReport.TotalConfirmedRevenue);
            Assert.AreEqual(1, salesReport.ConfirmedOrdersCount);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task LegacyUpdateStoreSettings_NullOnlineBaseUrl_ShouldPreserveExistingOnlineBaseUrl()
    {
        var (repo, tempDir, _, hasher, storage) = CreateIsolatedEnv();
        try
        {
            var adminEmpId = await EnsureAdminEmployeeIdAsync(repo, hasher);
            var settingsService = new EmployeeAndSettingsService(repo, hasher, storage);

            await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = "Loja Nuvem",
                WhatsappNumber = "11999990000",
                StoreDescription = "Com URL configurada",
                OnlineBaseUrl = "https://minhaloja.run.app"
            });

            // Simula app antigo enviando UpdateStoreSettingsRequest sem o campo OnlineBaseUrl
            var legacyUpdated = await settingsService.UpdateStoreSettingsAsync(adminEmpId, new UpdateStoreSettingsRequest
            {
                StoreName = "Loja Nuvem Atualizada",
                WhatsappNumber = "11999990000",
                StoreDescription = "Atualizado por versão antiga",
                OnlineBaseUrl = null
            });

            Assert.AreEqual("https://minhaloja.run.app", legacyUpdated.OnlineBaseUrl);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task AuthService_RegisterUserAccount_ShouldKeepAdminActive_AndListNewAccount()
    {
        var (repo, tempDir, dbPath, hasher, _) = CreateIsolatedEnv();
        try
        {
            var jwt = new JwtTokenService(new ConfigurationBuilder().Build());
            var auth = new AuthService(repo, hasher, jwt);

            var adminReg = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                FullName: "Dona Modas",
                Email: "dona@modas.com.br",
                Password: "senha123",
                Username: "dona.modas"));
            Assert.IsTrue(adminReg.Employee.IsAdmin);

            var reg = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                FullName: "Giovanna Silva",
                Email: "giovanna@modas.com.br",
                Password: "senha123",
                Username: "giovanna.modas"));

            Assert.AreEqual("giovanna.modas", reg.Employee.Username);
            Assert.IsFalse(string.IsNullOrWhiteSpace(reg.AccessToken));

            var reloadedRepo = new ClothingStoreRepository(hasher, dbPath);
            var employees = await reloadedRepo.GetEmployeesAsync();

            var legacyAdminEmp = employees.FirstOrDefault(e => e.User?.Username == "admin");
            var ownerEmp = employees.FirstOrDefault(e => e.User?.Username == "dona.modas");
            var newEmp = employees.FirstOrDefault(e => e.User?.Username == "giovanna.modas");

            Assert.IsNull(legacyAdminEmp, "Não deve existir conta admin padrão pré-criada.");
            Assert.IsNotNull(ownerEmp?.User, "A primeira conta administradora deve permanecer cadastrada.");
            Assert.IsTrue(ownerEmp!.IsActive && ownerEmp.User!.IsActive);
            Assert.IsNotNull(newEmp?.User, "A conta criada no celular deve estar salva no repositório.");
            Assert.IsTrue(newEmp!.IsActive && newEmp.User!.IsActive);
            Assert.AreEqual("giovanna@modas.com.br", newEmp.User!.Email);
            Assert.IsTrue(employees.Count >= 2);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task AdminApiClient_RuntimeBaseUrl_ShouldRouteCompatAndPlatformSyncWithHeaders()
    {
        var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/app/compatibility")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ApiCompatibilityStatusDto(
                        "v1", "1.0.0", "1.0.8", true, "1.0.7",
                        "https://github.com/giovannipieronisleal-pixel/yourlink/releases/latest",
                        "Compatível"))
                };
            }

            if (path == "/api/platform-sync/accounts")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PlatformAccountsSyncEnvelopeDto(
                        "v1",
                        DateTimeOffset.UtcNow,
                        "Loja Teste",
                        "Basico",
                        null,
                        null,
                        new List<PlatformAccountSyncDto>
                        {
                            new(Guid.NewGuid(), Guid.NewGuid(), "Administrador", "admin", null, "Admin", "Dono", null, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Loja Teste", "loja-teste", null, "Basico", null),
                            new(Guid.NewGuid(), Guid.NewGuid(), "Usuario Celular", "celular1", "cel@teste.com", "Admin", "Dono", null, true, false, DateTimeOffset.UtcNow, null, "Loja Teste", "loja-teste", null, "Basico", null)
                        }))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://initial.invalid/") };
        var client = new AdminApiClient(http);

        Assert.IsTrue(client.UpdateBaseAddress("http://192.168.0.50:5000/"));
        Assert.AreEqual("http://192.168.0.50:5000", client.CurrentBaseAddress);

        var compat = await client.GetCompatibilityStatusAsync("1.0.7");
        Assert.IsNotNull(compat);
        Assert.IsTrue(compat!.UpdateRecommended);
        Assert.AreEqual("http://192.168.0.50:5000/api/app/compatibility?clientVersion=1.0.7", handler.LastRequestUri!.ToString());

        var envelope = await client.GetPlatformAccountsSyncAsync("sync-key-123");
        Assert.AreEqual(2, envelope.Accounts.Count);
        Assert.AreEqual("http://192.168.0.50:5000/api/platform-sync/accounts", handler.LastRequestUri!.ToString());
        Assert.IsTrue(handler.LastHeaders.TryGetValue("X-YouLink-Sync-Key", out var sentKey) && sentKey == "sync-key-123");
    }

    [TestMethod]
    public async Task LiveApi_BackwardCompatHeaders_PhoneRegister_And_AdminPlatformSync_WorkEndToEnd()
    {
        var root = FindRepoRoot();
        var preferredConfig = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var fallbackConfig = preferredConfig == "Release" ? "Debug" : "Release";

        var apiDir = new[] { preferredConfig, fallbackConfig }
            .Select(cfg => Path.Combine(root, "src", "ClothingStore.Api", "bin", cfg, "net10.0"))
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "ClothingStore.Api.dll")));

        Assert.IsNotNull(apiDir, "ClothingStore.Api compilada não foi encontrada para teste de integração.");

        var exePath = Path.Combine(apiDir!, "ClothingStore.Api.exe");
        var dllPath = Path.Combine(apiDir!, "ClothingStore.Api.dll");
        var useNativeWinExe = OperatingSystem.IsWindows() && File.Exists(exePath);

        var tempDir = Path.Combine(Path.GetTempPath(), $"YouLink_LiveSyncTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempDb = Path.Combine(tempDir, "sync_api_db.json");
        var port = ReserveFreeLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        const string syncSecret = "sync-e2e-test-secret-2026";

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
        psi.Environment["Database__FilePath"] = tempDb;
        psi.Environment["Jwt__SecretKey"] = "LiveSyncIntegrationTestKey_1234567890_!@#";
        psi.Environment["YOULINK_SYNC_SECRET"] = syncSecret;
        psi.Environment["DOTNET_PRINT_TELEMETRY_MESSAGE"] = "false";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Falha ao iniciar ClothingStore.Api.");

        try
        {
            using var rawHttp = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };

            HttpResponseMessage? compatResp = null;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (process.HasExited)
                {
                    var stdout = await process.StandardOutput.ReadToEndAsync();
                    var stderr = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"ClothingStore.Api encerrou ({process.ExitCode}).\n{stdout}\n{stderr}");
                }

                try
                {
                    compatResp = await rawHttp.GetAsync("/api/app/compatibility?clientVersion=1.0.7");
                    if (compatResp.IsSuccessStatusCode)
                        break;
                }
                catch
                {
                }

                await Task.Delay(150);
            }

            Assert.IsNotNull(compatResp);
            Assert.AreEqual(HttpStatusCode.OK, compatResp!.StatusCode);
            Assert.IsTrue(compatResp.Headers.TryGetValues("X-YouLink-Api-Version", out var apiVer) && apiVer.Contains("v1"));
            Assert.IsTrue(compatResp.Headers.TryGetValues("X-YouLink-Latest-App", out var latestHdr) && latestHdr.Contains(AppUpdateCatalog.CurrentAppVersion));

            var oldCompat = await compatResp.Content.ReadFromJsonAsync<ApiCompatibilityStatusDto>();
            Assert.IsNotNull(oldCompat);
            Assert.IsTrue(oldCompat!.UpdateRecommended);
            Assert.AreEqual("v1", oldCompat.ApiVersion);

            var v1Compat = await rawHttp.GetFromJsonAsync<ApiCompatibilityStatusDto>($"/api/v1/app/compatibility?clientVersion={AppUpdateCatalog.CurrentAppVersion}");
            Assert.IsNotNull(v1Compat);
            Assert.IsFalse(v1Compat!.UpdateRecommended);

            // Simula o celular apontando para o servidor central e criando uma conta nova
            using var phoneHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var phoneClient = new AdminApiClient(phoneHttp);
            Assert.IsTrue(phoneClient.UpdateBaseAddress(baseUrl));

            var regResult = await phoneClient.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                FullName: "João Celular",
                Email: "joao.celular@teste.com",
                Password: "1234",
                Username: "joao.celular"));
            Assert.AreEqual("joao.celular", regResult.Employee.Username);

            // Simula o YouLink Admin puxando contas via X-YouLink-Sync-Key
            using var adminHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var adminClient = new AdminApiClient(adminHttp);
            Assert.IsTrue(adminClient.UpdateBaseAddress(baseUrl));

            var accountsEnvelope = await adminClient.GetPlatformAccountsSyncAsync(syncSecret);
            var adminAcc = accountsEnvelope.Accounts.FirstOrDefault(a => a.Username == "admin");
            var phoneAcc = accountsEnvelope.Accounts.FirstOrDefault(a => a.Username == "joao.celular");

            Assert.IsNull(adminAcc, "Não deve existir conta admin padrão pré-criada.");
            Assert.IsNotNull(phoneAcc, "A conta criada no celular precisa aparecer imediatamente no YouLink Admin!");
            Assert.IsTrue(phoneAcc!.IsActive);
            Assert.AreEqual("Admin", phoneAcc.RoleName);
            Assert.AreEqual("joao.celular@teste.com", phoneAcc.Email);

            // Celular que havia criado contas offline antes de conectar envia o lote pendente
            var batchSyncResult = await phoneClient.SyncLocalAccountsAsync(new SyncLocalAccountsRequest([
                new SyncLocalAccountItemDto("João Repetido", "joao.celular", "joao.celular@teste.com", "11999990000"),
                new SyncLocalAccountItemDto("Maria Offline", "maria.offline", "maria.offline@teste.com", "11988887777", StoreName: "Atelier Maria")
            ]));
            Assert.AreEqual(1, batchSyncResult.ImportedCount);
            Assert.AreEqual(1, batchSyncResult.SkippedExistingCount);

            var afterBatchSync = await adminClient.GetPlatformAccountsSyncAsync(syncSecret);
            var syncedOfflineAcc = afterBatchSync.Accounts.FirstOrDefault(a => a.Username == "maria.offline");
            Assert.IsNotNull(syncedOfflineAcc, "Conta criada offline no celular deve aparecer no YouLink Admin após sincronizar!");
            Assert.AreEqual("Employee", syncedOfflineAcc!.RoleName, "Sincronização nunca deve elevar usuário adicional para Admin.");

            // Admin aplica VIP direto para a conta sincronizada e alterna status ativo
            var vipSettings = await adminClient.ApplyRemoteVipAsync(new RemoteApplyVipRequest(
                PlanTier: StoreSettings.PlanTierIntermediate,
                DurationDays: 30,
                ActivateSite: true,
                TargetUsername: "joao.celular"), syncSecret);
            Assert.AreEqual(StoreSettings.PlanTierIntermediate, vipSettings.PlanTier);

            var toggledOff = await adminClient.ToggleRemoteUserActiveAsync(phoneAcc.UserId, syncSecret);
            Assert.IsFalse(toggledOff.IsActive);

            var afterToggle = await adminClient.GetPlatformAccountsSyncAsync(syncSecret);
            var toggledPhoneAcc = afterToggle.Accounts.First(a => a.UserId == phoneAcc.UserId);
            Assert.IsFalse(toggledPhoneAcc.IsActive);
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

            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public void ThemeAndProfileModal_TokensAndMarkup_PreventTransparentDarkModalRegression()
    {
        var root = FindRepoRoot();

        var mainLayout = File.ReadAllText(Path.Combine(root, "Components", "Layout", "MainLayout.razor"), Encoding.UTF8);
        var themeStart = mainLayout.IndexOf("<div class=\"@Theme.CurrentThemeClass\">", StringComparison.Ordinal);
        var modalPos = mainLayout.IndexOf("UserProfileModal", StringComparison.Ordinal);
        var codePos = mainLayout.IndexOf("@code", StringComparison.Ordinal);
        Assert.IsTrue(themeStart >= 0 && modalPos > themeStart && modalPos < codePos, "UserProfileModal precisa ser renderizado antes de @code.");
        var lastClosingDivBeforeCode = mainLayout.LastIndexOf("</div>", codePos, StringComparison.Ordinal);
        Assert.IsTrue(modalPos < lastClosingDivBeforeCode, "UserProfileModal deve estar DENTRO do wrapper de tema em MainLayout.razor para herdar --adm-*.");

        var themeService = File.ReadAllText(Path.Combine(root, "Services", "ThemeService.cs"), Encoding.UTF8);
        Assert.IsTrue(themeService.Contains("_ => \"theme-system\"", StringComparison.Ordinal), "Modo sistema precisa retornar a classe theme-system.");

        var appCss = File.ReadAllText(Path.Combine(root, "wwwroot", "app.css"), Encoding.UTF8);
        var rootBlockEnd = appCss.IndexOf(".theme-light,", StringComparison.Ordinal);
        Assert.IsTrue(rootBlockEnd > 0);
        Assert.IsTrue(appCss[..rootBlockEnd].Contains("--adm-card:", StringComparison.Ordinal), ":root deve declarar --adm-card como fallback.");
        Assert.IsTrue(appCss[..rootBlockEnd].Contains("--adm-sidebar:", StringComparison.Ordinal), ":root deve declarar --adm-sidebar como fallback.");
        Assert.IsTrue(appCss.Contains("background-color: var(--adm-card, #FFFFFF);", StringComparison.Ordinal), ".adm-modal-card deve ter fallback explícito.");
        Assert.IsTrue(appCss.Contains("background-color: var(--adm-sidebar, #141416);", StringComparison.Ordinal), ".adm-sidebar deve ter fundo explícito na versão branca e escura.");
        Assert.IsTrue(appCss.Contains("color: var(--adm-sidebar-text, #FAF8F5);", StringComparison.Ordinal), ".adm-nav-item.active deve manter contraste.");
        Assert.IsTrue(appCss.Contains(".theme-system .pill-green", StringComparison.Ordinal), "@media dark para theme-system deve ajustar contraste das badges.");

        var navCss = File.ReadAllText(Path.Combine(root, "Components", "Layout", "NavMenu.razor.css"), Encoding.UTF8);
        Assert.IsTrue(navCss.Contains(".adm-nav-more-btn.active", StringComparison.Ordinal), "Botão Mais precisa preservar destaque quando ativo.");

        var adminAppThemePath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Services", "PlatformThemeService.cs");
        if (File.Exists(adminAppThemePath))
        {
            var adminTheme = File.ReadAllText(adminAppThemePath, Encoding.UTF8);
            Assert.IsTrue(adminTheme.Contains("_ => \"theme-system\"", StringComparison.Ordinal), "AdminApp também deve retornar theme-system.");
        }
    }

    [TestMethod]
    public async Task PlatformAccountSyncProcessor_AndCrossProcessRepoRefresh_ShouldSyncOfflineUsersIdempotentlyWithoutElevation()
    {
        var (_, tempDir, dbPath, hasher, _) = CreateIsolatedEnv();
        try
        {
            var repoAdmin = new ClothingStoreRepository(hasher, dbPath);
            var jwt = new JwtTokenService(new ConfigurationBuilder().Build());
            var authAdmin = new AuthService(repoAdmin, hasher, jwt);

            await authAdmin.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Giovanni Dono",
                "gio@youlink.app",
                "SenhaForte!1",
                Username: "gio"));

            var initialEmployees = await repoAdmin.GetEmployeesAsync();
            Assert.AreEqual(1, initialEmployees.Count);

            // Simula outro processo (YouLink Desktop) gravando um usuário diretamente no mesmo DB JSON
            var repoOtherProcess = new ClothingStoreRepository(hasher, dbPath);
            var authOtherProcess = new AuthService(repoOtherProcess, hasher, jwt);
            await authOtherProcess.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                "Maria Loja",
                "maria.loja@youlink.app",
                "SenhaForte!2",
                Username: "maria.loja"));
            File.SetLastWriteTimeUtc(dbPath, DateTime.UtcNow.AddSeconds(2));

            var reloadedEmployees = await repoAdmin.GetEmployeesAsync();
            Assert.AreEqual(2, reloadedEmployees.Count, "Repositório do YouLink Admin deve recarregar novo usuário salvo por outro processo.");
            Assert.IsTrue(reloadedEmployees.Any(e => e.User?.Username == "maria.loja"));

            // Simula lote vindo de um celular offline (tentativa de sobrescrever Admin + novo usuário)
            var syncResult = await PlatformAccountSyncProcessor.ProcessSyncBatchAsync(
                new SyncLocalAccountsRequest([
                    new SyncLocalAccountItemDto("Tentativa Sobrescrever Gio", "gio", "gio@youlink.app", "11999991111"),
                    new SyncLocalAccountItemDto("Carlos Celular", "carlos.cel", "carlos@youlink.app", "11988882222", StoreName: "Loja Carlos")
                ]),
                repoAdmin,
                hasher);

            Assert.AreEqual(1, syncResult.ImportedCount);
            Assert.AreEqual(1, syncResult.SkippedExistingCount);

            var syncedUser = await repoAdmin.GetUserByEmailAsync("carlos@youlink.app");
            Assert.IsNotNull(syncedUser);
            Assert.AreEqual(RoleNames.Employee, syncedUser!.Role?.Name);
            Assert.IsFalse(hasher.VerifyPassword("", syncedUser.PasswordHash), "Conta sincronizada não pode aceitar senha vazia.");

            var gioAfter = await repoAdmin.GetUserByEmailAsync("gio@youlink.app");
            Assert.IsNotNull(gioAfter);
            Assert.AreEqual(RoleNames.Admin, gioAfter!.Role?.Name);
            Assert.IsTrue(hasher.VerifyPassword("SenhaForte!1", gioAfter.PasswordHash), "Sync idempotente jamais pode alterar senha de usuário existente.");

            var duplicateSyncResult = await PlatformAccountSyncProcessor.ProcessSyncBatchAsync(
                new SyncLocalAccountsRequest([
                    new SyncLocalAccountItemDto("Carlos Celular", "carlos.cel", "carlos@youlink.app", "11988882222")
                ]),
                repoAdmin,
                hasher);
            Assert.AreEqual(0, duplicateSyncResult.ImportedCount);
            Assert.AreEqual(1, duplicateSyncResult.SkippedExistingCount);
        }
        finally
        {
            CleanupDir(tempDir);
        }
    }

    [TestMethod]
    public async Task AdminApiClient_TestServerConnection_ShouldReturnFriendlyPortugueseTimeout_NotRawOperationCanceled()
    {
        var handler = new StubHttpHandler(_ => throw new TaskCanceledException("OperationCanceled"));
        using var http = new HttpClient(handler);
        var client = new AdminApiClient(http);

        var probe = await client.TestServerConnectionAsync("http://192.168.0.50:5180");

        Assert.IsFalse(probe.Reachable);
        Assert.AreEqual("http://192.168.0.50:5180", probe.NormalizedUrl);
        Assert.IsNotNull(probe.ErrorMessage);
        Assert.IsFalse(probe.ErrorMessage!.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(probe.ErrorMessage.Contains("Tempo esgotado", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ClientAndAdminSyncArchitecture_ShouldAvoid900msTimeout_AndWireAutoRefresh()
    {
        var root = FindRepoRoot();

        var appMaui = File.ReadAllText(Path.Combine(root, "MauiProgram.cs"), Encoding.UTF8);
        Assert.IsFalse(appMaui.Contains("FromMilliseconds(900)", StringComparison.Ordinal), "ConnectTimeout de 900ms causa OperationCanceled em Wi-Fi/Render.");

        var gateway = File.ReadAllText(Path.Combine(root, "Services", "MauiAdminGateway.cs"), Encoding.UTF8);
        Assert.IsTrue(gateway.Contains("PushLocalAccountsToServerAsync", StringComparison.Ordinal));
        Assert.IsTrue(gateway.Contains("CreateEmployeeAsync", StringComparison.Ordinal) && gateway.Contains("TrySyncLocalAccountsQuietlyAsync", StringComparison.Ordinal));

        var apiProgram = File.ReadAllText(Path.Combine(root, "src", "ClothingStore.Api", "Program.cs"), Encoding.UTF8);
        Assert.IsTrue(apiProgram.Contains("/api/platform-sync/sync-accounts", StringComparison.Ordinal), "API deve expor endpoint de envio de contas offline.");

        var storeSettings = File.ReadAllText(Path.Combine(root, "Components", "Pages", "StoreSettingsPage.razor"), Encoding.UTF8);
        Assert.IsTrue(storeSettings.Contains("PushLocalAccountsToServerAsync", StringComparison.Ordinal), "StoreSettingsPage precisa oferecer botão manual de envio de contas.");

        // O projeto privado src/ClothingStore.AdminApp existe apenas localmente (é excluído do repositório público).
        var adminMauiPath = Path.Combine(root, "src", "ClothingStore.AdminApp", "MauiProgram.cs");
        if (File.Exists(adminMauiPath))
        {
            var adminMaui = File.ReadAllText(adminMauiPath, Encoding.UTF8);
            Assert.IsFalse(adminMaui.Contains("FromMilliseconds(900)", StringComparison.Ordinal), "AdminApp também não pode usar ConnectTimeout de 900ms.");
            Assert.IsTrue(adminMaui.Contains("EmbeddedAdminSyncServer", StringComparison.Ordinal), "YouLink Admin precisa iniciar o Receptor LAN embutido.");
        }

        var adminStoragePath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Services", "PlatformAdminStorage.cs");
        if (File.Exists(adminStoragePath))
        {
            var adminStorage = File.ReadAllText(adminStoragePath, Encoding.UTF8);
            Assert.IsTrue(adminStorage.Contains("RefreshFromDiskIfModified", StringComparison.Ordinal), "PlatformAdminStorage precisa recarregar dados gravados por outros processos.");
        }

        var usersPagePath = Path.Combine(root, "src", "ClothingStore.AdminApp", "Components", "Pages", "UsersAndVipPage.razor");
        if (File.Exists(usersPagePath))
        {
            var usersPage = File.ReadAllText(usersPagePath, Encoding.UTF8);
            Assert.IsTrue(usersPage.Contains("Gateway.DataUpdated", StringComparison.Ordinal), "UsersAndVipPage precisa atualizar ao vivo quando novas contas chegarem.");
            Assert.IsTrue(usersPage.Contains("CreatePlatformUserAsync", StringComparison.Ordinal), "UsersAndVipPage deve permitir criar usuários direto no Admin.");
        }
    }
}
