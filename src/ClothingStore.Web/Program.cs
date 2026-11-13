using System.Security.Cryptography;
using System.Text;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using ClothingStore.Web.Components;
using ClothingStore.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var cloudPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(cloudPort) && int.TryParse(cloudPort, out var parsedPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
    ?? Environment.GetEnvironmentVariable("API_BASE_URL")
    ?? "http://localhost:5180";

builder.Services.AddScoped(sp => new HttpClient(new SocketsHttpHandler
{
    ConnectTimeout = TimeSpan.FromMilliseconds(300)
})
{
    BaseAddress = new Uri(apiBaseUrl),
    Timeout = TimeSpan.FromMilliseconds(800)
});
builder.Services.AddScoped<CatalogApiClient>();

var sharedDbPath = ResolveDatabasePath(builder.Configuration, builder.Environment.ContentRootPath);
var outboxPath = Path.Combine(Path.GetDirectoryName(sharedDbPath) ?? builder.Environment.ContentRootPath, "email_outbox.txt");

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IEmailService>(sp =>
    new SmtpEmailService(sp.GetRequiredService<IConfiguration>(), outboxPath));
builder.Services.AddSingleton<IClothingStoreRepository>(sp =>
    new ClothingStoreRepository(sp.GetRequiredService<IPasswordHasher>(), sharedDbPath));
builder.Services.AddSingleton<IImageStorageService>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var config = sp.GetRequiredService<IConfiguration>();
    var webRoot = builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
    return new SupabaseImageStorageService(httpClient, config, webRoot);
});

builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ProductManagementService>();
builder.Services.AddScoped<EmployeeAndSettingsService>();
builder.Services.AddScoped<OrderAndShippingService>();
builder.Services.AddScoped<CatalogDataGateway>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "ClothingStore.Web",
    timestamp = DateTimeOffset.UtcNow
}));

app.MapGet("/api/public/store-info", async (CatalogService catalogService, CancellationToken ct) =>
    Results.Ok(await catalogService.GetPublicStoreSettingsAsync(ct)));

app.MapGet("/api/cloud/status", async (IClothingStoreRepository repository, CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    var hasEnvSecret = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"));
    return Results.Ok(new
    {
        status = "online",
        provider = settings.OwnServerProvider,
        hostingMode = settings.ServerHostingMode,
        storeName = settings.StoreName,
        service = "ClothingStore.Web",
        requiresSyncKey = hasEnvSecret || !string.IsNullOrWhiteSpace(settings.OwnServerSyncSecret),
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapPost("/api/cloud/sync-state", async (
    HttpContext httpContext,
    CloudCatalogSyncPayload payload,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    ProductManagementService productService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
    {
        return Results.Unauthorized();
    }

    if (!string.IsNullOrWhiteSpace(payload.Settings.StoreName))
    {
        await settingsService.UpdateStoreSettingsAsync(null, payload.Settings, ct);
    }

    foreach (var cat in payload.Categories)
    {
        await productService.CreateCategoryAsync(cat, ct);
    }

    foreach (var prod in payload.Products)
    {
        await productService.CreateProductAsync(prod, null, ct);
    }

    return Results.Ok(new
    {
        message = "Catálogo online 24h sincronizado com sucesso!",
        syncedAt = DateTimeOffset.UtcNow,
        categoriesCount = payload.Categories.Count,
        productsCount = payload.Products.Count
    });
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ResolveDatabasePath(IConfiguration configuration, string contentRootPath)
{
    var configured = configuration["Database:FilePath"]
        ?? Environment.GetEnvironmentVariable("DATABASE_PATH");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured);
    }

    if (Directory.Exists(@"C:\Dev\appmaui.blazor"))
    {
        return @"C:\Dev\appmaui.blazor\App_Data\clothingstore_db.json";
    }

    var parentSrcDir = Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "src"));
    var candidate = Directory.Exists(parentSrcDir)
        ? Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "App_Data", "clothingstore_db.json"))
        : Path.Combine(contentRootPath, "App_Data", "clothingstore_db.json");

    if (EnsureWritableDirectory(candidate))
    {
        return candidate;
    }

    // Contêineres sem permissão de escrita em /app usam diretório temporário gravável
    var tempFallback = Path.Combine(Path.GetTempPath(), "youlink", "App_Data", "clothingstore_db.json");
    EnsureWritableDirectory(tempFallback);
    return tempFallback;
}

static bool EnsureWritableDirectory(string filePath)
{
    try
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            var probeFile = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
        }
        return true;
    }
    catch
    {
        return false;
    }
}

static async Task<bool> IsAuthorizedSyncRequestAsync(
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CancellationToken ct)
{
    var providedKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(providedKey))
    {
        return false;
    }

    var settings = await repository.GetStoreSettingsAsync(ct);
    var expectedKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"))
        ? Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET")!
        : settings.OwnServerSyncSecret;

    if (string.IsNullOrWhiteSpace(expectedKey))
    {
        return false;
    }

    var expectedBytes = Encoding.UTF8.GetBytes(expectedKey.Trim());
    var providedBytes = Encoding.UTF8.GetBytes(providedKey.Trim());
    return expectedBytes.Length == providedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}
