using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

/// <summary>
/// Testes específicos para lançamento no Windows (Desktop WinUI / Blazor Hybrid):
/// manifesto e DPI/LongPath, empacotamento self-contained x64 + WebView2, NTFS com acentos e bloqueios,
/// servidor loopback local, boot do executável da API no Windows e validação do workflow CI.
/// </summary>
[TestClass]
public class WindowsPlatformTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appmaui.blazor.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório (appmaui.blazor.csproj).");
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
            {
                Directory.Delete(path, recursive: true);
            }
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

    [TestMethod]
    public void Windows_ProjectAndAppManifest_HaveRequiredYouLinkAndDpiConfiguration()
    {
        var root = FindRepoRoot();

        var csprojPath = Path.Combine(root, "appmaui.blazor.csproj");
        var csproj = XDocument.Load(csprojPath);
        var props = csproj.Descendants().ToList();

        Assert.AreEqual("YouLink", props.FirstOrDefault(e => e.Name.LocalName == "ApplicationTitle")?.Value);
        Assert.AreEqual("com.youlink.app", props.FirstOrDefault(e => e.Name.LocalName == "ApplicationId")?.Value);
        Assert.AreEqual("None", props.FirstOrDefault(e => e.Name.LocalName == "WindowsPackageType")?.Value);

        var targetFrameworks = string.Join(';', props
            .Where(e => e.Name.LocalName == "TargetFrameworks")
            .Select(e => e.Value));
        Assert.IsTrue(targetFrameworks.Contains("net10.0-windows10.0.19041.0", StringComparison.OrdinalIgnoreCase),
            "O .csproj precisa manter o target de Windows 10/11.");

        var winSupportedOs = props.FirstOrDefault(e =>
            e.Name.LocalName == "SupportedOSPlatformVersion" &&
            (e.Attribute("Condition")?.Value.Contains("windows", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.AreEqual("10.0.17763.0", winSupportedOs?.Value);

        var winManifestProp = props.FirstOrDefault(e =>
            e.Name.LocalName == "ApplicationManifest" &&
            (e.Attribute("Condition")?.Value.Contains("windows", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.AreEqual(@"Platforms\Windows\app.manifest", winManifestProp?.Value);

        var winRidProp = props.FirstOrDefault(e =>
            e.Name.LocalName == "RuntimeIdentifier" &&
            (e.Attribute("Condition")?.Value.Contains("windows", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.AreEqual("win-x64", winRidProp?.Value);

        var winSelfContainedProp = props.FirstOrDefault(e =>
            e.Name.LocalName == "SelfContained" &&
            (e.Attribute("Condition")?.Value.Contains("windows", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.AreEqual("true", winSelfContainedProp?.Value);

        var winSdkSelfContainedProp = props.FirstOrDefault(e =>
            e.Name.LocalName == "WindowsAppSDKSelfContained" &&
            (e.Attribute("Condition")?.Value.Contains("windows", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.AreEqual("true", winSdkSelfContainedProp?.Value);

        var manifestPath = Path.Combine(root, "Platforms", "Windows", "app.manifest");
        Assert.IsTrue(File.Exists(manifestPath), "Platforms/Windows/app.manifest ausente.");
        var appManifest = XDocument.Load(manifestPath);

        Assert.AreEqual("true/PM", appManifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "dpiAware")?.Value);
        Assert.AreEqual("PerMonitorV2, PerMonitor", appManifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "dpiAwareness")?.Value);
        Assert.AreEqual("true", appManifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "longPathAware")?.Value);

        var appxPath = Path.Combine(root, "Platforms", "Windows", "Package.appxmanifest");
        Assert.IsTrue(File.Exists(appxPath), "Platforms/Windows/Package.appxmanifest ausente.");
        var appx = XDocument.Load(appxPath);
        var desktopFamily = appx.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "TargetDeviceFamily" &&
            e.Attribute("Name")?.Value == "Windows.Desktop");
        Assert.AreEqual("10.0.17763.0", desktopFamily?.Attribute("MinVersion")?.Value);
        Assert.IsTrue(appx.Descendants().Any(e =>
            e.Name.LocalName == "Capability" &&
            e.Attribute("Name")?.Value == "runFullTrust"),
            "Package.appxmanifest precisa declarar runFullTrust.");
    }

    [TestMethod]
    public void Windows_ReleaseWorkflow_ConfiguresSelfContainedX64AndWindowsTests()
    {
        var root = FindRepoRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release-youlink.yml");
        Assert.IsTrue(File.Exists(workflowPath), "Workflow do GitHub Actions ausente.");

        var yaml = File.ReadAllText(workflowPath);
        Assert.IsTrue(yaml.Contains("runs-on: windows-latest"), "Workflow precisa de runner windows-latest.");
        Assert.IsTrue(yaml.Contains("-f net10.0-windows10.0.19041.0"), "Workflow precisa publicar para net10.0-windows10.0.19041.0.");
        Assert.IsTrue(yaml.Contains("WindowsPackageType=None"), "Build Windows deve gerar executável portável unpackaged.");
        Assert.IsTrue(yaml.Contains("YOULINK_WIN_PUBLISH_DIR"), "Workflow deve informar o diretório publicado aos testes Windows.");
        Assert.IsTrue(yaml.Contains("ClothingStore.UnitTests.csproj"), "Job Windows deve executar os testes automatizados no Windows.");
        Assert.IsTrue(yaml.Contains("YouLink-Windows-x64.zip"), "Workflow deve gerar o pacote YouLink-Windows-x64.zip.");
    }

    [TestMethod]
    public async Task Windows_NtfsPaths_WithSpacesAccentsAndDeepFolders_PersistCatalogAndImages()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual('\\', Path.DirectorySeparatorChar);
        }

        var tempRoot = CreateSafeTempDir("YouLink_Win_NTFS");
        try
        {
            var nestedBase = Path.Combine(
                tempRoot,
                "YouLink Windows (Loja São Paulo — Área Teste)",
                "Dados Locais & Catálogo",
                "Subpasta_Nível_01");
            Directory.CreateDirectory(nestedBase);

            var dbFile = Path.Combine(nestedBase, "clothingstore_db.json");
            var webRoot = Path.Combine(nestedBase, "wwwroot");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "WindowsTestSecretKey_1234567890_ABCDEF_!99"
                })
                .Build();

            using var http = new HttpClient();
            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, dbFile);
            var images = new SupabaseImageStorageService(http, config, webRoot);
            var auth = new AuthService(repo, hasher, new JwtTokenService(config));
            var products = new ProductManagementService(repo, images);
            var admin = new EmployeeAndSettingsService(repo, hasher, images);

            var owner = await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Ana Paula", "ana@youlink.app", "1234"));
            await admin.UpdateStoreSettingsAsync(owner.Employee.EmployeeId, new UpdateStoreSettingsRequest
            {
                StoreName = "Boutique São Paulo",
                StoreSlug = "boutique-sp",
                WhatsappNumber = "11999998888",
                StoreDescription = "Teste de caminhos Windows com espaços e acentos",
                DefaultLowStockThreshold = 4
            });

            var fakePng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13 };
            await using var ms = new MemoryStream(fakePng);
            var (storagePath, dataUri) = await images.UploadImageAsync("products", "Foto Camiseta Verão.png", ms, "image/png");

            Assert.IsTrue(dataUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal));
            var expectedPhysicalFile = Path.Combine(webRoot, "uploads", storagePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(expectedPhysicalFile), $"Imagem deveria existir no caminho Windows: {expectedPhysicalFile}");

            var created = await products.CreateProductAsync(new SaveProductRequest
            {
                CategoryName = "Lançamentos",
                Name = "Camiseta Windows Edition",
                Description = "Peça validada em NTFS",
                BasePrice = 89.90m,
                IsVisibleOnSite = true
            }, owner.Employee.EmployeeId);
            Assert.AreEqual("Camiseta Windows Edition", created.Name);

            var reloaded = new ClothingStoreRepository(hasher, dbFile);
            var reloadedSettings = await reloaded.GetStoreSettingsAsync();
            Assert.AreEqual("Boutique São Paulo", reloadedSettings.StoreName);

            await images.DeleteImageAsync(storagePath);
            Assert.IsFalse(File.Exists(expectedPhysicalFile), "DeleteImageAsync precisa remover a imagem física do disco no Windows.");
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [TestMethod]
    public async Task Windows_NtfsAtomicWritesAndSharedReads_SurviveConcurrentReadersAndOpenHandles()
    {
        var tempRoot = CreateSafeTempDir("YouLink_Win_Atomic");
        try
        {
            var dbFile = Path.Combine(tempRoot, "clothingstore_db.json");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "WindowsTestSecretKey_1234567890_ABCDEF_!99"
                })
                .Build();

            var hasher = new Pbkdf2PasswordHasher();
            var repo = new ClothingStoreRepository(hasher, dbFile);
            var auth = new AuthService(repo, hasher, new JwtTokenService(config));
            await auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Admin Loja", "dona@youlink.app", "1234"));

            // Mantém um leitor externo aberto com FileShare.ReadWrite | FileShare.Delete (cenário Windows NTFS/Defender):
            // sobrescrita direta sem escrita atômica falharia com IOException no Windows.
            using var heldReader = new FileStream(dbFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var cts = new CancellationTokenSource();

            var readerTasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var probeRepo = new ClothingStoreRepository(hasher, dbFile);
                    var roles = await probeRepo.GetRolesAsync();
                    Assert.IsTrue(roles.Count > 0);
                    await Task.Delay(10);
                }
            })).ToList();

            var writerTasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                auth.RegisterUserAccountAsync(new RegisterUserAccountRequest($"Operador {i}", $"op{i}@youlink.app", "1234"))
            )).ToList();

            await Task.WhenAll(writerTasks);
            cts.Cancel();
            await Task.WhenAll(readerTasks);

            var leftoverTmp = Directory.GetFiles(tempRoot, "*.tmp");
            Assert.AreEqual(0, leftoverTmp.Length, "Não podem sobrar arquivos .tmp após escritas atômicas.");

            var corruptBackups = Directory.GetFiles(tempRoot, "*.corrupt-*");
            Assert.AreEqual(0, corruptBackups.Length, "Nenhuma leitura/escrita simultânea pode corromper o banco JSON.");

            using var jsonDoc = JsonDocument.Parse(File.ReadAllText(dbFile));
            Assert.IsTrue(jsonDoc.RootElement.TryGetProperty("Users", out _) || jsonDoc.RootElement.TryGetProperty("users", out _));

            var finalRepo = new ClothingStoreRepository(hasher, dbFile);
            var employees = await finalRepo.GetEmployeesAsync();
            Assert.IsTrue(employees.Count >= 21, $"Esperado >= 21 colaboradores persistidos, encontrado {employees.Count}.");
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [TestMethod]
    public async Task Windows_LoopbackHttpServer_SupportsPortConflictFallbackAndUtf8CatalogResponse()
    {
        using var occupiedListener = new TcpListener(IPAddress.Loopback, 0);
        occupiedListener.Start();
        var busyPort = ((IPEndPoint)occupiedListener.LocalEndpoint).Port;

        Assert.ThrowsException<SocketException>(() =>
        {
            using var collision = new TcpListener(IPAddress.Loopback, busyPort);
            collision.Start();
        });

        using var fallbackListener = new TcpListener(IPAddress.Loopback, 0);
        fallbackListener.Start();
        var activePort = ((IPEndPoint)fallbackListener.LocalEndpoint).Port;
        Assert.IsTrue(activePort != busyPort);

        var serverTask = Task.Run(async () =>
        {
            using var client = await fallbackListener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var body = $"<!doctype html><html><body>YouLink — Catálogo Windows ({requestLine})</body></html>";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"http://127.0.0.1:{activePort}/loja/minha-loja");
        var html = await response.Content.ReadAsStringAsync();

        await serverTask;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(html.Contains("YouLink — Catálogo Windows"), html);
        Assert.IsTrue(html.Contains("/loja/minha-loja"), html);
    }

    [TestMethod]
    public async Task Windows_ApiExecutable_BootsOnLoopback_RespondsOnlineAndEnforcesAuth()
    {
        var root = FindRepoRoot();
        var preferredConfig = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var fallbackConfig = preferredConfig == "Release" ? "Debug" : "Release";

        var apiDir = new[] { preferredConfig, fallbackConfig }
            .Select(cfg => Path.Combine(root, "src", "ClothingStore.Api", "bin", cfg, "net10.0"))
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "ClothingStore.Api.dll")));

        Assert.IsNotNull(apiDir, "ClothingStore.Api compilada não foi encontrada para o teste de boot Windows.");

        var exePath = Path.Combine(apiDir!, "ClothingStore.Api.exe");
        var dllPath = Path.Combine(apiDir!, "ClothingStore.Api.dll");
        var useNativeWinExe = OperatingSystem.IsWindows() && File.Exists(exePath);

        var tempDir = CreateSafeTempDir("YouLink_Win_ApiBoot");
        var tempDb = Path.Combine(tempDir, "win_api_test_db.json");
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
        psi.Environment["Database__FilePath"] = tempDb;
        psi.Environment["Jwt__SecretKey"] = "WindowsIntegrationTestSecretKey_1234567890_!@#";
        psi.Environment["DOTNET_PRINT_TELEMETRY_MESSAGE"] = "false";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Falha ao iniciar o processo ClothingStore.Api.");

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };

            HttpResponseMessage? healthResponse = null;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (process.HasExited)
                {
                    var stdout = await process.StandardOutput.ReadToEndAsync();
                    var stderr = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException(
                        $"ClothingStore.Api encerrou inesperadamente (código {process.ExitCode}).\nSTDOUT: {stdout}\nSTDERR: {stderr}");
                }

                try
                {
                    healthResponse = await http.GetAsync("/api/cloud/status");
                    if (healthResponse.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch
                {
                }

                await Task.Delay(150);
            }

            Assert.IsNotNull(healthResponse, "A API Windows não respondeu dentro do tempo limite em loopback.");
            Assert.AreEqual(HttpStatusCode.OK, healthResponse!.StatusCode);
            Assert.IsTrue(healthResponse.Headers.TryGetValues("X-Content-Type-Options", out var nosniff) && nosniff.Contains("nosniff"));

            var statusJson = await healthResponse.Content.ReadAsStringAsync();
            Assert.IsTrue(statusJson.Contains("\"online\""), statusJson);

            var publicStore = await http.GetAsync("/api/public/store-info");
            Assert.AreEqual(HttpStatusCode.OK, publicStore.StatusCode);

            var unauthenticatedDashboard = await http.GetAsync("/api/dashboard/summary");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticatedDashboard.StatusCode);

            var registerResp = await http.PostAsJsonAsync("/api/auth/register-initial-admin", new RegisterInitialAdminRequest(
                "Lojista Windows",
                "windows@youlink.app",
                "1234",
                "Loja Windows Test"));
            Assert.AreEqual(HttpStatusCode.BadRequest, registerResp.StatusCode);

            var loginResp = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin"));
            Assert.AreEqual(HttpStatusCode.OK, loginResp.StatusCode);
            var authResult = await loginResp.Content.ReadFromJsonAsync<LoginResponse>();
            Assert.IsNotNull(authResult?.AccessToken);

            using var authedReq = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/summary");
            authedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult!.AccessToken);
            var dashboardResp = await http.SendAsync(authedReq);
            Assert.AreEqual(HttpStatusCode.OK, dashboardResp.StatusCode);

            Assert.IsTrue(File.Exists(tempDb), "O executável da API precisa persistir o banco isolado em tempDb.");
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

    [TestMethod]
    public void Windows_PublishedBundle_HasExeRuntimeWebView2WwwrootAndNoSensitiveLeaks()
    {
        var root = FindRepoRoot();
        var explicitPublishDir = Environment.GetEnvironmentVariable("YOULINK_WIN_PUBLISH_DIR");
        string? publishDir = null;

        if (!string.IsNullOrWhiteSpace(explicitPublishDir))
        {
            publishDir = Path.IsPathRooted(explicitPublishDir)
                ? explicitPublishDir
                : Path.Combine(root, explicitPublishDir);
            Assert.IsTrue(Directory.Exists(publishDir),
                $"YOULINK_WIN_PUBLISH_DIR aponta para pasta inexistente: {publishDir}");
        }
        else
        {
            var candidates = new[]
            {
                Path.Combine(root, "dist", "YouLink-Windows"),
                Path.Combine(root, "obj", "win-check")
            };
            publishDir = candidates.FirstOrDefault(Directory.Exists);
        }

        if (publishDir is null)
        {
            Console.WriteLine("[INFO] Nenhum diretório publicado do Windows encontrado (YOULINK_WIN_PUBLISH_DIR não definido).");
            return;
        }

        var exePath = Path.Combine(publishDir, "appmaui.blazor.exe");
        Assert.IsTrue(File.Exists(exePath), $"Executável principal não encontrado: {exePath}");

        var exeBytes = File.ReadAllBytes(exePath);
        Assert.IsTrue(exeBytes.Length > 100_000, "appmaui.blazor.exe está suspeitamente pequeno.");
        Assert.AreEqual((byte)'M', exeBytes[0], "Assinatura MZ inválida no executável Windows.");
        Assert.AreEqual((byte)'Z', exeBytes[1], "Assinatura MZ inválida no executável Windows.");

        var peHeaderOffset = BitConverter.ToInt32(exeBytes, 0x3C);
        Assert.IsTrue(peHeaderOffset > 0 && peHeaderOffset + 6 < exeBytes.Length);
        Assert.AreEqual((byte)'P', exeBytes[peHeaderOffset]);
        Assert.AreEqual((byte)'E', exeBytes[peHeaderOffset + 1]);
        var machine = BitConverter.ToUInt16(exeBytes, peHeaderOffset + 4);
        Assert.AreEqual((ushort)0x8664, machine, $"Executável Windows deve ser x64 (0x8664), encontrado 0x{machine:X4}.");

        var requiredBinaries = new[]
        {
            "appmaui.blazor.dll",
            "hostfxr.dll",
            "coreclr.dll",
            "clrjit.dll",
            "Microsoft.WindowsAppRuntime.dll",
            "Microsoft.Web.WebView2.Core.dll",
            "WebView2Loader.dll"
        };
        foreach (var bin in requiredBinaries)
        {
            var binPath = Path.Combine(publishDir, bin);
            Assert.IsTrue(File.Exists(binPath), $"Binário obrigatório para rodar em Windows limpo está ausente: {bin}");
        }

        using (JsonDocument.Parse(File.ReadAllText(Path.Combine(publishDir, "appmaui.blazor.deps.json")))) { }
        using (JsonDocument.Parse(File.ReadAllText(Path.Combine(publishDir, "appmaui.blazor.runtimeconfig.json")))) { }

        var requiredWebAssets = new[]
        {
            Path.Combine("wwwroot", "index.html"),
            Path.Combine("wwwroot", "app.css"),
            Path.Combine("wwwroot", "appmaui.blazor.styles.css"),
            Path.Combine("wwwroot", "favicon.svg"),
            Path.Combine("wwwroot", "images", "youlink-mark.svg"),
            Path.Combine("wwwroot", "_framework", "blazor.webview.js")
        };
        foreach (var asset in requiredWebAssets)
        {
            var assetPath = Path.Combine(publishDir, asset);
            Assert.IsTrue(File.Exists(assetPath), $"Asset Blazor Hybrid ausente no pacote Windows: {asset}");
            Assert.IsTrue(new FileInfo(assetPath).Length > 0, $"Asset vazio no pacote Windows: {asset}");
        }

        var indexHtml = File.ReadAllText(Path.Combine(publishDir, "wwwroot", "index.html"));
        Assert.IsTrue(indexHtml.Contains("YouLink", StringComparison.OrdinalIgnoreCase), "index.html deve exibir título YouLink.");
        Assert.IsTrue(indexHtml.Contains("_framework/blazor.webview.js", StringComparison.Ordinal), "index.html deve carregar blazor.webview.js.");

        Assert.IsFalse(Directory.Exists(Path.Combine(publishDir, "App_Data")), "App_Data local não pode vazar no zip Windows.");
        Assert.IsFalse(File.Exists(Path.Combine(publishDir, "clothingstore_db.json")), "Banco local não pode vazar no zip Windows.");
        Assert.IsFalse(File.Exists(Path.Combine(publishDir, "email_outbox.txt")), "Outbox local não pode vazar no zip Windows.");
        Assert.IsFalse(File.Exists(Path.Combine(publishDir, "ClothingStore.AdminApp.exe")), "AdminApp não pode vazar no zip Windows.");
    }
}
