using System.Text.Json;
using System.Xml.Linq;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

[TestClass]
public class PwaAndAndroidHardeningTests
{
    private static string ResolveRepoRoot()
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

    [TestMethod]
    public void PwaManifestBuilder_ShouldGenerateValidInstallableManifest_FromStoreSettings()
    {
        var customSettings = new PublicStoreSettingsDto(
            StoreName: "Atelier Verano Boutique Exclusiva",
            StoreSlug: "atelier-verano",
            WhatsappNumber: "5511999998888",
            StoreDescription: "Moda autoral em linho e seda.",
            LogoPublicUrl: null,
            UpdatedAt: DateTimeOffset.UtcNow);

        var customJson = PwaManifestBuilder.BuildJson(customSettings);
        using var customDoc = JsonDocument.Parse(customJson);
        var root = customDoc.RootElement;

        Assert.AreEqual("Atelier Verano Boutique Exclusiva", root.GetProperty("name").GetString());
        Assert.AreEqual("Atelier Verano…", root.GetProperty("short_name").GetString());
        Assert.AreEqual("Moda autoral em linho e seda.", root.GetProperty("description").GetString());
        Assert.AreEqual("/", root.GetProperty("start_url").GetString());
        Assert.AreEqual("/", root.GetProperty("scope").GetString());
        Assert.AreEqual("standalone", root.GetProperty("display").GetString());
        Assert.AreEqual("#14151B", root.GetProperty("theme_color").GetString());
        Assert.AreEqual("#14151B", root.GetProperty("background_color").GetString());

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.IsTrue(icons.Any(i => i.GetProperty("sizes").GetString() == "192x192" && i.GetProperty("src").GetString() == "/icons/icon-192.png"));
        Assert.IsTrue(icons.Any(i => i.GetProperty("sizes").GetString() == "512x512" && i.GetProperty("purpose").GetString() == "any"));
        Assert.IsTrue(icons.Any(i => i.GetProperty("sizes").GetString() == "512x512" && i.GetProperty("purpose").GetString() == "maskable"));

        var fallbackJson = PwaManifestBuilder.BuildJson(null);
        using var fallbackDoc = JsonDocument.Parse(fallbackJson);
        Assert.AreEqual("YouLink Loja", fallbackDoc.RootElement.GetProperty("name").GetString());
        Assert.AreEqual("YouLink Loja", fallbackDoc.RootElement.GetProperty("short_name").GetString());
    }

    [TestMethod]
    public void ClothingStoreWeb_PwaAssetsAndRouting_ShouldBeConfiguredCleanly()
    {
        var root = ResolveRepoRoot();
        var webRoot = Path.Combine(root, "src", "ClothingStore.Web");
        var wwwroot = Path.Combine(webRoot, "wwwroot");

        var programCs = File.ReadAllText(Path.Combine(webRoot, "Program.cs"));
        Assert.IsTrue(programCs.Contains("\"/manifest.webmanifest\"", StringComparison.Ordinal));
        Assert.IsTrue(programCs.Contains("application/manifest+json", StringComparison.Ordinal));
        Assert.IsTrue(programCs.Contains("PwaManifestBuilder.BuildJson", StringComparison.Ordinal));

        var appRazor = File.ReadAllText(Path.Combine(webRoot, "Components", "App.razor"));
        Assert.IsTrue(appRazor.Contains("<link rel=\"manifest\" href=\"manifest.webmanifest\" />", StringComparison.Ordinal));
        Assert.IsTrue(appRazor.Contains("<link rel=\"apple-touch-icon\" href=\"icons/apple-touch-icon.png\" />", StringComparison.Ordinal));
        Assert.IsTrue(appRazor.Contains("<script src=\"pwa-install.js\" defer></script>", StringComparison.Ordinal));
        Assert.IsFalse(appRazor.Contains("navigator.serviceWorker", StringComparison.Ordinal),
            "O registro de Service Worker deve ficar em arquivo externo pwa-install.js, sem script inline.");

        var layoutRazor = File.ReadAllText(Path.Combine(webRoot, "Components", "Layout", "MainLayout.razor"));
        Assert.IsTrue(layoutRazor.Contains("data-pwa-install", StringComparison.Ordinal));

        var appCss = File.ReadAllText(Path.Combine(wwwroot, "app.css"));
        Assert.IsTrue(appCss.Contains("html.pwa-can-install .lux-pwa-install-btn", StringComparison.Ordinal));

        var swJs = File.ReadAllText(Path.Combine(wwwroot, "sw.js"));
        Assert.IsTrue(swJs.Contains("\"/_blazor\"", StringComparison.Ordinal));
        Assert.IsTrue(swJs.Contains("\"/api/\"", StringComparison.Ordinal));
        Assert.IsTrue(swJs.Contains("event.respondWith", StringComparison.Ordinal));

        Assert.IsTrue(File.Exists(Path.Combine(wwwroot, "pwa-install.js")));
        Assert.IsTrue(File.Exists(Path.Combine(wwwroot, "offline.html")));

        foreach (var iconFile in new[] { "icon-192.png", "icon-512.png", "icon-maskable-512.png", "apple-touch-icon.png" })
        {
            var iconPath = Path.Combine(wwwroot, "icons", iconFile);
            Assert.IsTrue(File.Exists(iconPath), $"Ícone PWA ausente: {iconFile}");
            Assert.IsTrue(new FileInfo(iconPath).Length > 1024, $"Ícone PWA inválido/vazio: {iconFile}");
        }
    }

    [TestMethod]
    public void AndroidManifest_NetworkSecurityConfig_ShouldEnforceHttpsForExternalTraffic()
    {
        var root = ResolveRepoRoot();
        var manifestPath = Path.Combine(root, "Platforms", "Android", "AndroidManifest.xml");
        var netConfigPath = Path.Combine(root, "Platforms", "Android", "Resources", "xml", "network_security_config.xml");

        var manifestXml = File.ReadAllText(manifestPath);
        Assert.IsTrue(manifestXml.Contains("android:usesCleartextTraffic=\"false\"", StringComparison.Ordinal),
            "AndroidManifest.xml não deve liberar HTTP plano globalmente.");
        Assert.IsTrue(manifestXml.Contains("android:networkSecurityConfig=\"@xml/network_security_config\"", StringComparison.Ordinal));

        Assert.IsTrue(File.Exists(netConfigPath), "network_security_config.xml precisa existir para loopback local.");
        var doc = XDocument.Load(netConfigPath);
        var baseConfig = doc.Root?.Element("base-config");
        Assert.AreEqual("false", baseConfig?.Attribute("cleartextTrafficPermitted")?.Value);

        var domains = doc.Root?.Elements("domain-config")
            .Where(e => e.Attribute("cleartextTrafficPermitted")?.Value == "true")
            .SelectMany(e => e.Elements("domain"))
            .Select(d => d.Value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsNotNull(domains);
        Assert.IsTrue(domains!.Contains("localhost"));
        Assert.IsTrue(domains.Contains("127.0.0.1"));
        Assert.IsTrue(domains.Contains("10.0.2.2"));
        Assert.AreEqual(3, domains.Count, "Apenas loopback local deve permitir HTTP sem criptografia.");
    }
}
