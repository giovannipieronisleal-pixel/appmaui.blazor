using System.Security.Cryptography;
using System.Text;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IEmailService>(sp =>
    new SmtpEmailService(sp.GetRequiredService<IConfiguration>(), outboxPath));
builder.Services.AddSingleton<IClothingStoreRepository>(sp =>
    new ClothingStoreRepository(sp.GetRequiredService<IPasswordHasher>(), sharedDbPath));
builder.Services.AddSingleton<IMultiTenantStoreResolver>(sp =>
    new MultiTenantStoreManager(
        sp.GetRequiredService<IClothingStoreRepository>(),
        sp.GetRequiredService<IPasswordHasher>(),
        Path.GetDirectoryName(sharedDbPath)));
builder.Services.AddSingleton<IMercadoPagoPixService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var mpHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient("MercadoPago");
    return new MercadoPagoPixService(
        mpHttp,
        () => MercadoPagoPixService.ReadFromConfig(
            config["MercadoPago:ClientId"],
            config["MercadoPago:ClientSecret"],
            config["MercadoPago:WebhookSecret"]));
});
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
builder.Services.AddScoped<CloudSyncStateService>();
builder.Services.AddScoped<OrderAndShippingService>(sp =>
    new OrderAndShippingService(
        sp.GetRequiredService<IClothingStoreRepository>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<IMercadoPagoPixService>()));
builder.Services.AddScoped<MultiTenantGatewayService>(sp =>
    new MultiTenantGatewayService(
        sp.GetRequiredService<IMultiTenantStoreResolver>(),
        sp.GetRequiredService<IMercadoPagoPixService>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<IPasswordHasher>(),
        sp.GetRequiredService<IImageStorageService>(),
        sp.GetService<IEmailService>()));
builder.Services.AddScoped<PlatformSubscriptionService>();
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

app.MapGet("/manifest.webmanifest", async (
    string? storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    PublicStoreSettingsDto? settings = null;
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
        if (repo is not null)
            settings = await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct);
    }
    catch
    {
    }

    return Results.Text(PwaManifestBuilder.BuildJson(settings), "application/manifest+json; charset=utf-8");
});

app.MapGet("/loja/{storeSlug}/manifest.webmanifest", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    PublicStoreSettingsDto? settings = null;
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
        if (repo is not null)
            settings = await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct);
    }
    catch
    {
    }

    return Results.Text(PwaManifestBuilder.BuildJson(settings), "application/manifest+json; charset=utf-8");
});

app.MapGet("/api/public/store-info", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var slug = ResolveStoreSlug(ctx, null);
    var repo = await gateway.ResolveReadRepositoryAsync(slug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada neste servidor." });
    return Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

app.MapGet("/api/public/stores/{storeSlug}/store-info", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada neste servidor." });
    return Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

app.MapGet("/api/public/categories", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicCategoriesAsync(ct));
});

app.MapGet("/api/public/stores/{storeSlug}/categories", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicCategoriesAsync(ct));
});

app.MapGet("/api/public/filters", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogFiltersAsync(ct));
});

app.MapGet("/api/public/stores/{storeSlug}/filters", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogFiltersAsync(ct));
});

app.MapGet("/api/public/products", async (
    HttpContext ctx,
    string? search,
    Guid? categoryId,
    string? categorySlug,
    string? color,
    string? size,
    bool? onlyInStock,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var filter = new CatalogQueryFilter(search, categoryId, categorySlug, color, size, onlyInStock ?? false);
    return Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogProductsAsync(filter, ct));
});

app.MapGet("/api/public/stores/{storeSlug}/products", async (
    string storeSlug,
    string? search,
    Guid? categoryId,
    string? categorySlug,
    string? color,
    string? size,
    bool? onlyInStock,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var filter = new CatalogQueryFilter(search, categoryId, categorySlug, color, size, onlyInStock ?? false);
    return Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogProductsAsync(filter, ct));
});

app.MapGet("/api/public/products/{slugOrId}", async (
    HttpContext ctx,
    string slugOrId,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var product = await gateway.CreateCatalogService(repo).GetPublicProductDetailAsync(slugOrId, ct);
    return product is not null ? Results.Ok(product) : Results.NotFound(new { error = "Produto não encontrado." });
});

app.MapGet("/api/public/stores/{storeSlug}/products/{slugOrId}", async (
    string storeSlug,
    string slugOrId,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var product = await gateway.CreateCatalogService(repo).GetPublicProductDetailAsync(slugOrId, ct);
    return product is not null ? Results.Ok(product) : Results.NotFound(new { error = "Produto não encontrado." });
});

app.MapGet("/api/public/cep/{cep}", async (
    string cep,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await orderService.LookupCepAsync(cep, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/public/shipping-quote", async (
    HttpContext ctx,
    ShippingQuoteRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
        if (repo is null)
            return Results.NotFound(new { error = "Loja não encontrada." });
        return Results.Ok(await gateway.CreateOrderService(repo).QuoteShippingForBuyerAsync(request, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/public/stores/{storeSlug}/shipping-quote", async (
    string storeSlug,
    ShippingQuoteRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
        if (repo is null)
            return Results.NotFound(new { error = "Loja não encontrada." });
        return Results.Ok(await gateway.CreateOrderService(repo).QuoteShippingForBuyerAsync(request, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/public/orders/pix", async (
    HttpContext ctx,
    CreatePixOrderRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        request.ExpressMobileMode = false;
        var created = await gateway.PlacePixOrderForStoreAsync(
            ResolveStoreSlug(ctx, null),
            request,
            GetPublicOrigin(ctx),
            ct);
        return Results.Created($"/api/public/orders/{created.Id}/status", created);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/public/stores/{storeSlug}/orders/pix", async (
    string storeSlug,
    HttpContext ctx,
    CreatePixOrderRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        request.ExpressMobileMode = false;
        var created = await gateway.PlacePixOrderForStoreAsync(
            storeSlug,
            request,
            GetPublicOrigin(ctx),
            ct);
        return Results.Created($"/api/public/stores/{storeSlug}/orders/{created.Id}/status", created);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/public/orders/{orderId:guid}/status", async (
    Guid orderId,
    string? token,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var status = await gateway.GetPublicOrderStatusAsync(ResolveStoreSlug(ctx, null), orderId, token, ct);
        return status is null ? Results.NotFound(new { error = "Pedido não encontrado." }) : Results.Ok(status);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/public/stores/{storeSlug}/orders/{orderId:guid}/status", async (
    string storeSlug,
    Guid orderId,
    string? token,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var status = await gateway.GetPublicOrderStatusAsync(storeSlug, orderId, token, ct);
        return status is null ? Results.NotFound(new { error = "Pedido não encontrado." }) : Results.Ok(status);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapMethods("/api/public/webhooks/mercadopago", ["POST", "GET"], async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
    await HandleMercadoPagoWebhookAsync(ctx, null, gateway, ct)).DisableAntiforgery();

app.MapMethods("/api/public/webhooks/mercadopago/{storeSlug}", ["POST", "GET"], async (
    string storeSlug,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
    await HandleMercadoPagoWebhookAsync(ctx, storeSlug, gateway, ct)).DisableAntiforgery();

app.MapGet("/api/cloud/status", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var slug = ResolveStoreSlug(ctx, null);
    var repository = await gateway.ResolveReadRepositoryAsync(slug, ct) ?? gateway.StoreResolver.DefaultRepository;
    var settings = await repository.GetStoreSettingsAsync(ct);
    var categories = await repository.GetCategoriesAsync(onlyActive: false, ct);
    var products = await repository.GetProductsAsync(includeArchived: false, ct);
    var hasEnvSecret = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"));
    return Results.Ok(new
    {
        status = "online",
        provider = settings.OwnServerProvider,
        hostingMode = settings.ServerHostingMode,
        storeName = settings.StoreName,
        storeSlug = settings.StoreSlug,
        isSiteActive = settings.IsSiteActive,
        categoriesCount = categories.Count,
        productsCount = products.Count,
        service = "ClothingStore.Web",
        appVersion = AppUpdateCatalog.CurrentAppVersion,
        requiresSyncKey = hasEnvSecret || !string.IsNullOrWhiteSpace(settings.OwnServerSyncSecret),
        multiTenantEnabled = true,
        centralPublishReady = true,
        hasMercadoPagoAppConfigured = gateway.MercadoPago.GetConfiguredApp().IsOAuthConfigured,
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapGet("/api/app/compatibility", (string? clientVersion) => Results.Ok(BuildWebCompatibilityDto(clientVersion)));

app.MapGet("/api/v1/app/compatibility", (string? clientVersion) => Results.Ok(BuildWebCompatibilityDto(clientVersion)));

static ApiCompatibilityStatusDto BuildWebCompatibilityDto(string? clientVersion)
{
    var updateRecommended = AppUpdateCatalog.IsVersionNewer(AppUpdateCatalog.CurrentAppVersion, clientVersion);
    return new ApiCompatibilityStatusDto(
        ApiVersion: "v1",
        MinSupportedAppVersion: "1.0.0",
        LatestAppVersion: AppUpdateCatalog.CurrentAppVersion,
        UpdateRecommended: updateRecommended,
        ClientAppVersion: clientVersion,
        ReleaseUrl: AppUpdateCatalog.ReleasesPageUrl,
        Message: updateRecommended
            ? $"Há uma atualização v{AppUpdateCatalog.CurrentAppVersion} disponível para o YouLink."
            : "Seu aplicativo está atualizado.");
}

app.MapPost("/api/cloud/publish-store", async (
    HttpContext httpContext,
    CloudCatalogSyncPayload payload,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var providedSyncKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
        var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                              await IsAuthorizedSyncRequestAsync(httpContext, gateway.StoreResolver.DefaultRepository, ct);

        if (!string.IsNullOrWhiteSpace(providedSyncKey) && !hasValidSyncKey)
            return Results.Unauthorized();

        var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
        var ownerEmail = httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
        var slug = ResolveStoreSlug(httpContext, null);

        var response = await gateway.PublishStoreAsync(
            payload,
            slug,
            providedSyncKey,
            publishToken,
            hasValidSyncKey,
            ownerEmail,
            GetPublicOrigin(httpContext),
            ct);

        if (!response.Success)
            return Results.Conflict(new { error = response.Message });

        return Results.Ok(response);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/cloud/orders", async (
    HttpContext httpContext,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var providedSyncKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(httpContext, gateway.StoreResolver.DefaultRepository, ct);
    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
    var slug = ResolveStoreSlug(httpContext, null);

    var result = await gateway.GetStoreOrdersForSyncAsync(slug, publishToken, hasValidSyncKey, ownerEmail, ct);
    return result is null ? Results.Unauthorized() : Results.Ok(result);
});

app.MapGet("/api/payments/mercadopago/config", (MultiTenantGatewayService gateway) =>
{
    var cfg = gateway.MercadoPago.GetConfiguredApp();
    return Results.Ok(new
    {
        isOAuthConfigured = cfg.IsOAuthConfigured,
        message = cfg.IsOAuthConfigured
            ? "Mercado Pago pronto para conexão em 1 clique."
            : "Configure MP_CLIENT_ID e MP_CLIENT_SECRET nas variáveis do servidor central."
    });
});

app.MapPost("/api/payments/mercadopago/oauth/start", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var providedSyncKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
        var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                              await IsAuthorizedSyncRequestAsync(ctx, gateway.StoreResolver.DefaultRepository, ct);
        var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
        var ownerEmail = ctx.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
        var slug = ResolveStoreSlug(ctx, null);
        var callbackUri = $"{GetPublicOrigin(ctx).TrimEnd('/')}/api/payments/mercadopago/oauth/callback";

        var (startResp, issuedToken) = await gateway.StartMercadoPagoOAuthAsync(
            slug, providedSyncKey, publishToken, hasValidSyncKey, ownerEmail, callbackUri, ct);

        return Results.Ok(new
        {
            isConfigured = startResp.IsConfigured,
            authorizationUrl = startResp.AuthorizationUrl,
            stateToken = startResp.StateToken,
            publishToken = issuedToken,
            message = startResp.Message
        });
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/payments/mercadopago/oauth/callback", async (
    string? code,
    string? state,
    string? error,
    string? error_description,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var res = await gateway.CompleteMercadoPagoOAuthCallbackAsync(code, state, error, error_description, ct);
    var html = MercadoPagoPixService.BuildOAuthCallbackHtml(res);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/api/payments/mercadopago/status", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var slug = ResolveStoreSlug(ctx, null);
    return Results.Ok(await gateway.GetMercadoPagoStatusAsync(slug, ct));
});

app.MapPost("/api/payments/mercadopago/disconnect", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var providedSyncKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(ctx, gateway.StoreResolver.DefaultRepository, ct);
    var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = ctx.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
    var slug = ResolveStoreSlug(ctx, null);

    var ok = await gateway.DisconnectMercadoPagoAsync(slug, publishToken, hasValidSyncKey, ownerEmail, ct);
    return ok ? Results.Ok(new { disconnected = true }) : Results.Unauthorized();
}).DisableAntiforgery();

app.MapPost("/api/cloud/sync-state", async (
    HttpContext httpContext,
    CloudCatalogSyncPayload payload,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var providedSyncKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(httpContext, repository, ct);
    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();

    if (!hasValidSyncKey && string.IsNullOrWhiteSpace(publishToken))
    {
        return Results.Unauthorized();
    }

    try
    {
        var result = await gateway.PublishStoreAsync(
            payload,
            ResolveStoreSlug(httpContext, null),
            providedSyncKey,
            publishToken,
            hasValidSyncKey,
            ownerEmail,
            GetPublicOrigin(httpContext),
            ct);

        if (!result.Success)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            message = "Catálogo online 24h sincronizado com sucesso!",
            syncedAt = DateTimeOffset.UtcNow,
            categoriesCount = result.CategoriesCount,
            productsCount = result.ProductsCount,
            isSiteActive = result.IsSiteActive,
            storeSlug = result.StoreSlug,
            catalogUrl = result.PublicStoreUrl
        });
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
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

    var parentSrcDir = Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "src"));
    var workspaceCandidate = Directory.Exists(parentSrcDir)
        ? Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "App_Data", "clothingstore_db.json"))
        : null;
    if (!string.IsNullOrWhiteSpace(workspaceCandidate) && EnsureWritableDirectory(workspaceCandidate))
    {
        return workspaceCandidate;
    }

    foreach (var devRoot in new[] { @"C:\Dev\YouLink", @"C:\Dev\appmaui.blazor" })
    {
        if (Directory.Exists(devRoot))
        {
            var devCandidate = Path.Combine(devRoot, "App_Data", "clothingstore_db.json");
            if (EnsureWritableDirectory(devCandidate))
                return devCandidate;
        }
    }

    var candidate = Path.Combine(contentRootPath, "App_Data", "clothingstore_db.json");

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

static string? ResolveStoreSlug(HttpContext ctx, string? routeSlug)
{
    if (!string.IsNullOrWhiteSpace(routeSlug))
        return routeSlug.Trim();
    var querySlug = ctx.Request.Query["storeSlug"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(querySlug))
        return querySlug.Trim();
    var headerSlug = ctx.Request.Headers["X-YouLink-Store-Slug"].FirstOrDefault();
    return string.IsNullOrWhiteSpace(headerSlug) ? null : headerSlug.Trim();
}

static string GetPublicOrigin(HttpContext ctx)
{
    var proto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
    var host = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
    var scheme = string.IsNullOrWhiteSpace(proto) ? ctx.Request.Scheme : proto;
    var hostValue = string.IsNullOrWhiteSpace(host) ? ctx.Request.Host.Value : host;
    return string.IsNullOrWhiteSpace(hostValue) ? string.Empty : $"{scheme}://{hostValue}";
}

static async Task<IResult> HandleMercadoPagoWebhookAsync(
    HttpContext ctx,
    string? storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct)
{
    string rawBody = string.Empty;
    if (ctx.Request.Body.CanRead)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        rawBody = await reader.ReadToEndAsync(ct);
    }

    var queryDataId = ctx.Request.Query["data.id"].FirstOrDefault()
                      ?? ctx.Request.Query["id"].FirstOrDefault();
    var queryTopic = ctx.Request.Query["topic"].FirstOrDefault()
                     ?? ctx.Request.Query["type"].FirstOrDefault();
    var xSignature = ctx.Request.Headers["x-signature"].FirstOrDefault();
    var xRequestId = ctx.Request.Headers["x-request-id"].FirstOrDefault();

    var res = await gateway.ProcessMercadoPagoWebhookAsync(
        storeSlug, queryDataId, queryTopic, xSignature, xRequestId, rawBody, ct);

    if (res.IsUnauthorized)
        return Results.Unauthorized();

    return Results.Ok(new
    {
        received = true,
        confirmed = res.OrderConfirmed,
        message = res.Message
    });
}
