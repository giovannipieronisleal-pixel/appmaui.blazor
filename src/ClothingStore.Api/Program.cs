using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using ClothingStore.Api.Security;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Suporte automático à variável PORT exigida pelo Google Cloud Run
var cloudRunPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(cloudRunPort) && int.TryParse(cloudRunPort, out var parsedPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");
}

// Configuração de CORS para permitir chamadas do App Mobile / Clientes web sem credenciais de navegador
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var configuredOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
        if (!string.IsNullOrWhiteSpace(configuredOrigins))
        {
            var origins = configuredOrigins
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// Proteção contra força bruta e spam automatizado em autenticação e pedidos públicos
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Muitas tentativas em sequência. Aguarde um minuto e tente novamente." },
            token);
    };

    options.AddPolicy("auth-strict", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 25,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("public-orders", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

// Registro de Infraestrutura e Persistência
var sharedDbPath = builder.Configuration["Database:FilePath"]
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "App_Data", "clothingstore_db.json"));

var outboxPath = Path.Combine(Path.GetDirectoryName(sharedDbPath) ?? builder.Environment.ContentRootPath, "email_outbox.txt");

builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IEmailService>(sp =>
    new SmtpEmailService(sp.GetRequiredService<IConfiguration>(), outboxPath));
builder.Services.AddSingleton<IClothingStoreRepository>(sp =>
    new ClothingStoreRepository(sp.GetRequiredService<IPasswordHasher>(), sharedDbPath));

builder.Services.AddHttpClient();

builder.Services.AddSingleton<IMultiTenantStoreResolver>(sp =>
    new MultiTenantStoreManager(
        sp.GetRequiredService<IClothingStoreRepository>(),
        sp.GetRequiredService<IPasswordHasher>(),
        Path.GetDirectoryName(sharedDbPath)));
builder.Services.AddSingleton<IPlatformMercadoPagoTokenStore>(_ =>
    new PlatformMercadoPagoTokenStore(Path.GetDirectoryName(sharedDbPath)));

builder.Services.AddSingleton<IMercadoPagoPixService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new MercadoPagoPixService(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("MercadoPago"),
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

// Registro dos Serviços de Aplicação (Casos de Uso)
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ProductManagementService>();
builder.Services.AddScoped<StockManagementService>();
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
        sp.GetService<IEmailService>(),
        sp.GetRequiredService<IPlatformMercadoPagoTokenStore>()));
builder.Services.AddScoped<PlatformSubscriptionService>(sp =>
    new PlatformSubscriptionService(
        sp.GetRequiredService<IClothingStoreRepository>(),
        sp.GetRequiredService<IMercadoPagoPixService>(),
        sp.GetRequiredService<IPlatformMercadoPagoTokenStore>()));

// Autenticação JWT + Policies de Autorização verificadas no Backend
builder.Services.AddAuthentication(JwtBearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, JwtBearerAuthenticationHandler>(
        JwtBearerAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RoleNames.PolicyAdminOnly, policy =>
        policy.RequireRole(RoleNames.Admin))
    .AddPolicy(RoleNames.PolicyCanManageProducts, policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole(RoleNames.Admin) ||
            ctx.User.HasClaim("can_manage_products", "true")))
    .AddPolicy(RoleNames.PolicyCanManageStock, policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole(RoleNames.Admin) ||
            ctx.User.HasClaim("can_manage_stock", "true")));

var app = builder.Build();

// Tratamento Global de Erros e Validações de Domínio (sem vazamento de stacktrace ou detalhes internos)
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = feature?.Error;

        context.Response.ContentType = "application/json";
        if (exception is DomainException domainEx)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = domainEx.Message });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Ocorreu um erro interno ao processar a solicitação."
        });
    });
});

// Cabeçalhos de Segurança HTTP para prevenção contra vazamento de dados, clickjacking e sniffing
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

    if (context.Request.Path.StartsWithSegments("/api/auth") ||
        context.Request.Path.StartsWithSegments("/api/employees") ||
        context.Request.Path.StartsWithSegments("/api/settings"))
    {
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";
    }

    await next();
});

var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (!Directory.Exists(webRootPath))
{
    Directory.CreateDirectory(webRootPath);
}
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "ClothingStore.Api",
    timestamp = DateTimeOffset.UtcNow
}));

// =============================================================================
// 1. ENDPOINTS PÚBLICOS DO CATÁLOGO (/api/public/...) + MULTI-TENANT
// =============================================================================
var publicGroup = app.MapGroup("/api/public").AllowAnonymous();

static string? ResolveApiStoreSlug(HttpContext ctx, string? routeSlug = null)
{
    if (!string.IsNullOrWhiteSpace(routeSlug))
        return routeSlug.Trim();
    var q = ctx.Request.Query["storeSlug"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(q))
        return q.Trim();
    var h = ctx.Request.Headers["X-YouLink-Store-Slug"].FirstOrDefault();
    return string.IsNullOrWhiteSpace(h) ? null : h.Trim();
}

static string GetApiPublicOrigin(HttpContext ctx)
{
    var proto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
    var host = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
    var scheme = string.IsNullOrWhiteSpace(proto) ? ctx.Request.Scheme : proto;
    var hostValue = string.IsNullOrWhiteSpace(host) ? ctx.Request.Host.Value : host;
    return string.IsNullOrWhiteSpace(hostValue) ? string.Empty : $"{scheme}://{hostValue}";
}

publicGroup.MapGet("/store-info", async (HttpContext ctx, MultiTenantGatewayService gateway, CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveApiStoreSlug(ctx), ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

publicGroup.MapGet("/stores/{storeSlug}/store-info", async (string storeSlug, MultiTenantGatewayService gateway, CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

publicGroup.MapGet("/categories", async (HttpContext ctx, MultiTenantGatewayService gateway, CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveApiStoreSlug(ctx), ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicCategoriesAsync(ct));
});

publicGroup.MapGet("/stores/{storeSlug}/categories", async (string storeSlug, MultiTenantGatewayService gateway, CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicCategoriesAsync(ct));
});

publicGroup.MapGet("/filters", async (HttpContext ctx, MultiTenantGatewayService gateway, CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveApiStoreSlug(ctx), ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogFiltersAsync(ct));
});

publicGroup.MapGet("/stores/{storeSlug}/filters", async (string storeSlug, MultiTenantGatewayService gateway, CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogFiltersAsync(ct));
});

publicGroup.MapGet("/products", async (
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
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveApiStoreSlug(ctx), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });

    var filter = new CatalogQueryFilter(search, categoryId, categorySlug, color, size, onlyInStock ?? false);
    return Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogProductsAsync(filter, ct));
});

publicGroup.MapGet("/stores/{storeSlug}/products", async (
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

publicGroup.MapGet("/products/{slugOrId}", async (
    HttpContext ctx,
    string slugOrId,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveApiStoreSlug(ctx), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });

    var product = await gateway.CreateCatalogService(repo).GetPublicProductDetailAsync(slugOrId, ct);
    return product is not null
        ? Results.Ok(product)
        : Results.NotFound(new { error = "Produto não encontrado ou indisponível no catálogo." });
});

publicGroup.MapGet("/stores/{storeSlug}/products/{slugOrId}", async (
    string storeSlug,
    string slugOrId,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });

    var product = await gateway.CreateCatalogService(repo).GetPublicProductDetailAsync(slugOrId, ct);
    return product is not null
        ? Results.Ok(product)
        : Results.NotFound(new { error = "Produto não encontrado ou indisponível no catálogo." });
});

app.MapGet("/api/public/cep/{cep}", async (
    string cep,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    return Results.Ok(await orderService.LookupCepAsync(cep, ct));
});

app.MapPost("/api/public/shipping-quote", async (
    HttpContext ctx,
    ShippingQuoteRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveApiStoreSlug(ctx), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    return Results.Ok(await gateway.CreateOrderService(repo).QuoteShippingForBuyerAsync(request, ct));
});

app.MapPost("/api/public/stores/{storeSlug}/shipping-quote", async (
    string storeSlug,
    ShippingQuoteRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    return Results.Ok(await gateway.CreateOrderService(repo).QuoteShippingForBuyerAsync(request, ct));
});

app.MapPost("/api/public/orders/pix", async (
    HttpContext ctx,
    CreatePixOrderRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    // Em pedidos públicos anônimos na web, exige cálculo formal de CEP/área de entrega (impede bypass do raio)
    request.ExpressMobileMode = false;
    var created = await gateway.PlacePixOrderForStoreAsync(
        ResolveApiStoreSlug(ctx),
        request,
        GetApiPublicOrigin(ctx),
        ct);
    return Results.Created($"/api/public/orders/{created.Id}/status", created);
}).RequireRateLimiting("public-orders");

app.MapPost("/api/public/stores/{storeSlug}/orders/pix", async (
    string storeSlug,
    HttpContext ctx,
    CreatePixOrderRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    request.ExpressMobileMode = false;
    var created = await gateway.PlacePixOrderForStoreAsync(
        storeSlug,
        request,
        GetApiPublicOrigin(ctx),
        ct);
    return Results.Created($"/api/public/stores/{storeSlug}/orders/{created.Id}/status", created);
}).RequireRateLimiting("public-orders");

app.MapGet("/api/public/orders/{orderId:guid}/status", async (
    Guid orderId,
    string? token,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var status = await gateway.GetPublicOrderStatusAsync(ResolveApiStoreSlug(ctx), orderId, token, ct);
    return status is null ? Results.NotFound(new { error = "Pedido não encontrado." }) : Results.Ok(status);
}).AllowAnonymous();

app.MapGet("/api/public/stores/{storeSlug}/orders/{orderId:guid}/status", async (
    string storeSlug,
    Guid orderId,
    string? token,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var status = await gateway.GetPublicOrderStatusAsync(storeSlug, orderId, token, ct);
    return status is null ? Results.NotFound(new { error = "Pedido não encontrado." }) : Results.Ok(status);
}).AllowAnonymous();

app.MapMethods("/api/public/webhooks/mercadopago", ["POST", "GET"], async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
    var body = ctx.Request.Body.CanRead ? await reader.ReadToEndAsync(ct) : string.Empty;
    var res = await gateway.ProcessMercadoPagoWebhookAsync(
        null,
        ctx.Request.Query["data.id"].FirstOrDefault() ?? ctx.Request.Query["id"].FirstOrDefault(),
        ctx.Request.Query["topic"].FirstOrDefault() ?? ctx.Request.Query["type"].FirstOrDefault(),
        ctx.Request.Headers["x-signature"].FirstOrDefault(),
        ctx.Request.Headers["x-request-id"].FirstOrDefault(),
        body,
        ct);
    return res.IsUnauthorized ? Results.Unauthorized() : Results.Ok(new { received = true, confirmed = res.OrderConfirmed, message = res.Message });
}).AllowAnonymous();

app.MapMethods("/api/public/webhooks/mercadopago/{storeSlug}", ["POST", "GET"], async (
    string storeSlug,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
    var body = ctx.Request.Body.CanRead ? await reader.ReadToEndAsync(ct) : string.Empty;
    var res = await gateway.ProcessMercadoPagoWebhookAsync(
        storeSlug,
        ctx.Request.Query["data.id"].FirstOrDefault() ?? ctx.Request.Query["id"].FirstOrDefault(),
        ctx.Request.Query["topic"].FirstOrDefault() ?? ctx.Request.Query["type"].FirstOrDefault(),
        ctx.Request.Headers["x-signature"].FirstOrDefault(),
        ctx.Request.Headers["x-request-id"].FirstOrDefault(),
        body,
        ct);
    return res.IsUnauthorized ? Results.Unauthorized() : Results.Ok(new { received = true, confirmed = res.OrderConfirmed, message = res.Message });
}).AllowAnonymous();

// =============================================================================
// 2. ENDPOINTS DE AUTENTICAÇÃO E ENTRADA POR E-MAIL (/api/auth/...)
// =============================================================================
// Headers de versionamento e compatibilidade contínua com versões antigas do app (API v1)
const string LatestAppVersion = AppUpdateCatalog.CurrentAppVersion;

app.Use(async (context, next) =>
{
    context.Response.Headers["X-YouLink-Api-Version"] = "v1";
    context.Response.Headers["X-YouLink-Latest-App"] = LatestAppVersion;
    await next();
});

static bool IsAuthorizedPlatformSyncRequest(HttpContext httpContext, StoreSettings settings)
{
    if (httpContext.User.Identity?.IsAuthenticated == true &&
        httpContext.User.IsInRole(RoleNames.Admin))
    {
        return true;
    }

    var envSecret = Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET");
    var expectedSecret = !string.IsNullOrWhiteSpace(envSecret)
        ? envSecret
        : settings.OwnServerSyncSecret;

    if (string.IsNullOrWhiteSpace(expectedSecret))
        return false;

    var headerSecret = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(headerSecret))
        return false;

    var expectedBytes = Encoding.UTF8.GetBytes(expectedSecret.Trim());
    var providedBytes = Encoding.UTF8.GetBytes(headerSecret.Trim());
    return expectedBytes.Length == providedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}

static bool IsClientVersionOlder(string? clientVersion, string latestVersion)
{
    if (string.IsNullOrWhiteSpace(clientVersion))
        return false;

    var cleanClient = clientVersion.Trim().TrimStart('v', 'V');
    return Version.TryParse(cleanClient, out var parsedClient)
        && Version.TryParse(latestVersion, out var parsedLatest)
        && parsedClient < parsedLatest;
}

app.MapGet("/api/app/compatibility", (string? clientVersion) =>
{
    var updateRecommended = IsClientVersionOlder(clientVersion, LatestAppVersion);
    return Results.Ok(new ApiCompatibilityStatusDto(
        ApiVersion: "v1",
        MinSupportedAppVersion: "1.0.0",
        LatestAppVersion: LatestAppVersion,
        UpdateRecommended: updateRecommended,
        ClientAppVersion: clientVersion,
        ReleaseUrl: "https://github.com/giovannipieronisleal-pixel/yourlink/releases/latest",
        Message: updateRecommended
            ? $"Sua versão ({clientVersion}) continua funcionando normalmente na API v1. Quando quiser, atualize para a {LatestAppVersion} para vendas diretas no celular e melhorias visuais."
            : "Seu aplicativo está compatível e atualizado na API v1."));
}).AllowAnonymous();

app.MapGet("/api/v1/app/compatibility", (string? clientVersion) =>
{
    var updateRecommended = IsClientVersionOlder(clientVersion, LatestAppVersion);
    return Results.Ok(new ApiCompatibilityStatusDto(
        ApiVersion: "v1",
        MinSupportedAppVersion: "1.0.0",
        LatestAppVersion: LatestAppVersion,
        UpdateRecommended: updateRecommended,
        ClientAppVersion: clientVersion,
        ReleaseUrl: "https://github.com/giovannipieronisleal-pixel/yourlink/releases/latest",
        Message: updateRecommended
            ? $"Sua versão ({clientVersion}) continua funcionando normalmente na API v1. Quando quiser, atualize para a {LatestAppVersion}."
            : "Aplicativo compatível na API v1."));
}).AllowAnonymous();

var authGroup = app.MapGroup("/api/auth");

authGroup.MapGet("/setup-status", async (AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.GetSetupStatusAsync(ct))).AllowAnonymous();

authGroup.MapPost("/register-initial-admin", async (RegisterInitialAdminRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.RegisterInitialAdminByEmailAsync(request, ct))).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/register", async (RegisterUserAccountRequest request, AuthService authService, CancellationToken ct) =>
{
    var created = await authService.RegisterUserAccountAsync(request, ct);
    return Results.Created($"/api/employees/{created.Employee.EmployeeId}", created);
}).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/login", async (LoginRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.LoginAsync(request, ct))).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/login-challenge", async (LoginRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.LoginWithChallengeAsync(request, ct))).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/verify-2fa", async (VerifyTwoFactorLoginRequest request, AuthService authService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.ChallengeToken))
        throw new DomainException("Sessão de verificação em duas etapas ausente ou expirada. Faça login com senha novamente.");

    return Results.Ok(await authService.VerifyTwoFactorLoginAsync(request, ct));
}).AllowAnonymous().RequireRateLimiting("auth-strict");

// Endpoints de sincronização de contas & VIP entre Celular <-> Servidor Central <-> YouLink Admin
app.MapGet("/api/platform-sync/accounts", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    var employees = await repository.GetEmployeesAsync(ct);
    var effectiveTier = settings.GetEffectivePlanTier();
    var list = new List<PlatformAccountSyncDto>(employees.Count);

    foreach (var emp in employees)
    {
        var user = emp.User ?? await repository.GetUserByIdAsync(emp.UserId, ct);
        if (user is null)
            continue;

        var username = string.IsNullOrWhiteSpace(user.Username)
            ? User.DeriveDefaultUsername(user.Email)
            : user.Username;

        list.Add(new PlatformAccountSyncDto(
            UserId: user.Id,
            EmployeeId: emp.Id,
            FullName: emp.FullName,
            Username: username,
            Email: user.HasEmailAddress ? user.Email : null,
            RoleName: user.Role?.Name ?? RoleNames.Employee,
            JobTitle: emp.JobTitle,
            Phone: emp.Phone,
            IsActive: emp.IsActive && user.IsActive,
            TwoFactorEnabled: user.TwoFactorEnabled,
            CreatedAt: user.CreatedAt,
            LastLoginAt: user.LastLoginAt,
            StoreName: settings.StoreName,
            StoreSlug: settings.StoreSlug,
            OnlineBaseUrl: settings.OnlineBaseUrl,
            PlanTier: effectiveTier,
            PlanExpiresAt: settings.PlanExpiresAt));
    }

    return Results.Ok(new PlatformAccountsSyncEnvelopeDto(
        ServerVersion: LatestAppVersion,
        ServerTimeUtc: DateTimeOffset.UtcNow,
        StoreName: settings.StoreName,
        PlanTier: effectiveTier,
        PlanExpiresAt: settings.PlanExpiresAt,
        OnlineBaseUrl: settings.OnlineBaseUrl,
        Accounts: list));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/apply-vip", async (
    HttpContext httpContext,
    RemoteApplyVipRequest request,
    IClothingStoreRepository repository,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    var now = DateTimeOffset.UtcNow;
    var normalizedTier = StoreSettings.NormalizePlanTier(request.PlanTier);
    DateTimeOffset? nextExpiry = null;

    if (!string.Equals(normalizedTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase) &&
        request.DurationDays.HasValue && request.DurationDays.Value > 0)
    {
        var baseStart = request.ExtendExistingDuration &&
                        string.Equals(settings.PlanTier, normalizedTier, StringComparison.OrdinalIgnoreCase) &&
                        settings.PlanExpiresAt.HasValue &&
                        settings.PlanExpiresAt.Value > now
            ? settings.PlanExpiresAt.Value
            : now;
        nextExpiry = baseStart.AddDays(request.DurationDays.Value);
    }

    settings.UpdatePlanAndDesign(normalizedTier, designConfig: settings.DesignConfig);
    settings.PlanExpiresAt = nextExpiry;
    if (request.ActivateSite)
        settings.ActivateSite();
    await repository.UpdateStoreSettingsAsync(settings, ct);

    return Results.Ok(EmployeeAndSettingsService.MapToAdminSettingsDto(settings));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/toggle-user-active/{userId:guid}", async (
    Guid userId,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    var currentUserIdRaw = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub");
    if (Guid.TryParse(currentUserIdRaw, out var callerId) && callerId == userId)
        throw new DomainException("Você não pode desativar sua própria conta de administrador em uso.");

    var user = await repository.GetUserByIdAsync(userId, ct)
        ?? throw new DomainException("Usuário não encontrado no servidor.");
    var employee = await repository.GetEmployeeByUserIdAsync(user.Id, ct)
        ?? throw new DomainException("Registro de usuário vinculado não encontrado.");

    var nextActive = !(user.IsActive && employee.IsActive);
    user.IsActive = nextActive;
    employee.IsActive = nextActive;
    await repository.UpdateEmployeeAsync(employee, user, ct);

    return Results.Ok(new RemoteToggleUserActiveResponse(
        user.Id,
        employee.Id,
        nextActive,
        nextActive ? $"Conta '{employee.FullName}' reativada no servidor." : $"Conta '{employee.FullName}' suspensa no servidor."));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/sync-accounts", async (
    SyncLocalAccountsRequest request,
    IClothingStoreRepository repository,
    IPasswordHasher passwordHasher,
    CancellationToken ct) =>
    Results.Ok(await PlatformAccountSyncProcessor.ProcessSyncBatchAsync(request, repository, passwordHasher, ct)))
    .AllowAnonymous()
    .RequireRateLimiting("auth-strict");

authGroup.MapPost("/email-code/request", async (RequestEmailLoginCodeRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.RequestEmailLoginCodeAsync(request, ct))).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/email-code/verify", async (VerifyEmailLoginCodeRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.LoginWithEmailCodeAsync(request, ct))).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/forgot-password", async (ForgotPasswordRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.RequestPasswordResetAsync(request, ct))).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/reset-password", async (ResetPasswordRequest request, AuthService authService, CancellationToken ct) =>
{
    await authService.ResetPasswordAsync(request, ct);
    return Results.Ok(new { message = "Senha redefinida com sucesso." });
}).AllowAnonymous().RequireRateLimiting("auth-strict");

authGroup.MapPost("/change-password", async (
    ChangePasswordRequest request,
    ClaimsPrincipal user,
    AuthService authService,
    CancellationToken ct) =>
{
    var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    await authService.ChangePasswordAsync(userId, request, ct);
    return Results.Ok(new { message = "Senha alterada com sucesso." });
}).RequireAuthorization();

authGroup.MapGet("/me", async (ClaimsPrincipal user, AuthService authService, CancellationToken ct) =>
{
    var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return Results.Ok(await authService.GetCurrentProfileAsync(userId, ct));
}).RequireAuthorization();

// =============================================================================
// 3. ENDPOINT DE DASHBOARD (/api/dashboard/...)
// =============================================================================
app.MapGet("/api/dashboard/summary", async (StockManagementService stockService, CancellationToken ct) =>
    Results.Ok(await stockService.GetDashboardSummaryAsync(ct)))
    .RequireAuthorization();

// =============================================================================
// 4. ENDPOINTS DE CATEGORIAS E PRODUTOS (/api/categories, /api/products)
// =============================================================================
app.MapGet("/api/categories", async (ProductManagementService service, CancellationToken ct) =>
    Results.Ok(await service.GetAllCategoriesAsync(ct)))
    .RequireAuthorization();

app.MapPost("/api/categories", async (SaveCategoryRequest request, ProductManagementService service, CancellationToken ct) =>
{
    var created = await service.CreateCategoryAsync(request, ct);
    return Results.Created($"/api/categories/{created.Id}", created);
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapPut("/api/categories/{id:guid}", async (Guid id, SaveCategoryRequest request, ProductManagementService service, CancellationToken ct) =>
    Results.Ok(await service.UpdateCategoryAsync(id, request, ct)))
    .RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapGet("/api/products", async (
    string? search,
    Guid? categoryId,
    bool? includeArchived,
    ProductManagementService service,
    CancellationToken ct) =>
{
    return Results.Ok(await service.GetAdminProductsAsync(search, categoryId, includeArchived ?? false, ct));
}).RequireAuthorization();

app.MapGet("/api/products/{id:guid}", async (Guid id, ProductManagementService service, CancellationToken ct) =>
    Results.Ok(await service.GetProductByIdAsync(id, ct)))
    .RequireAuthorization();

app.MapPost("/api/products", async (
    SaveProductRequest request,
    ClaimsPrincipal user,
    ProductManagementService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    var created = await service.CreateProductAsync(request, employeeId, ct);
    return Results.Created($"/api/products/{created.Id}", created);
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapPut("/api/products/{id:guid}", async (Guid id, SaveProductRequest request, ProductManagementService service, CancellationToken ct) =>
    Results.Ok(await service.UpdateProductAsync(id, request, ct)))
    .RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapPatch("/api/products/{id:guid}/visibility", async (
    Guid id,
    bool isVisibleOnSite,
    ProductManagementService service,
    CancellationToken ct) =>
{
    return Results.Ok(await service.ToggleVisibilityAsync(id, isVisibleOnSite, ct));
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapDelete("/api/products/{id:guid}", async (Guid id, ProductManagementService service, CancellationToken ct) =>
{
    await service.DeleteOrArchiveProductAsync(id, ct);
    return Results.NoContent();
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

// =============================================================================
// 5. ENDPOINTS DE IMAGENS DOS PRODUTOS (/api/products/{productId}/images)
// =============================================================================
app.MapPost("/api/products/{productId:guid}/images/upload", async (
    Guid productId,
    HttpRequest httpRequest,
    ProductManagementService service,
    CancellationToken ct) =>
{
    if (!httpRequest.HasFormContentType)
        throw new DomainException("Envie o arquivo de imagem no formato multipart/form-data.");

    var form = await httpRequest.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault()
        ?? throw new DomainException("Nenhum arquivo de imagem foi enviado.");

    if (file.Length <= 0 || file.Length > SupabaseImageStorageService.MaxUploadSizeBytes)
        throw new DomainException($"A imagem deve ter entre 1 byte e {SupabaseImageStorageService.MaxUploadSizeBytes / (1024 * 1024)} MB.");

    var altText = form["altText"].FirstOrDefault();
    var isPrimary = bool.TryParse(form["isPrimary"].FirstOrDefault(), out var b) && b;

    await using var stream = file.OpenReadStream();
    var imageDto = await service.UploadProductImageAsync(
        productId,
        file.FileName,
        stream,
        file.ContentType,
        altText,
        isPrimary,
        ct);

    return Results.Created($"/api/products/{productId}/images/{imageDto.Id}", imageDto);
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapPost("/api/products/{productId:guid}/images", async (
    Guid productId,
    AddProductImageByUrlRequest request,
    ProductManagementService service,
    CancellationToken ct) =>
{
    var imageDto = await service.AddProductImageByUrlAsync(productId, request, ct);
    return Results.Created($"/api/products/{productId}/images/{imageDto.Id}", imageDto);
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapPatch("/api/products/{productId:guid}/images/{imageId:guid}/primary", async (
    Guid productId,
    Guid imageId,
    ProductManagementService service,
    CancellationToken ct) =>
{
    await service.SetPrimaryProductImageAsync(productId, imageId, ct);
    return Results.Ok(new { message = "Imagem principal definida com sucesso." });
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapDelete("/api/products/{productId:guid}/images/{imageId:guid}", async (
    Guid productId,
    Guid imageId,
    ProductManagementService service,
    CancellationToken ct) =>
{
    await service.DeleteProductImageAsync(productId, imageId, ct);
    return Results.NoContent();
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

// =============================================================================
// 6. ENDPOINTS DE VARIANTES (/api/products/{productId}/variants, /api/variants)
// =============================================================================
app.MapPost("/api/products/{productId:guid}/variants", async (
    Guid productId,
    SaveProductVariantRequest request,
    ClaimsPrincipal user,
    ProductManagementService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : Guid.Empty;
    var variant = await service.AddVariantAsync(productId, employeeId, request, ct);
    return Results.Created($"/api/variants/{variant.Id}", variant);
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapPut("/api/variants/{variantId:guid}", async (
    Guid variantId,
    SaveProductVariantRequest request,
    ClaimsPrincipal user,
    ProductManagementService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    return Results.Ok(await service.UpdateVariantAsync(variantId, request, employeeId, ct));
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

app.MapDelete("/api/variants/{variantId:guid}", async (
    Guid variantId,
    ProductManagementService service,
    CancellationToken ct) =>
{
    await service.DeleteVariantAsync(variantId, ct);
    return Results.NoContent();
}).RequireAuthorization(RoleNames.PolicyCanManageProducts);

// =============================================================================
// 7. ENDPOINTS DE CONTROLE DE ESTOQUE E MOVIMENTAÇÕES (/api/stock/...)
// =============================================================================
app.MapPost("/api/stock/movements", async (
    CreateStockMovementRequest request,
    ClaimsPrincipal user,
    StockManagementService stockService,
    CancellationToken ct) =>
{
    var employeeId = Guid.Parse(user.FindFirstValue("employee_id")!);
    var movement = await stockService.RegisterMovementAsync(employeeId, request, ct);
    return Results.Created($"/api/stock/movements/{movement.Id}", movement);
}).RequireAuthorization(RoleNames.PolicyCanManageStock);

app.MapGet("/api/stock/movements", async (
    Guid? productId,
    Guid? variantId,
    Guid? employeeId,
    int? limit,
    StockManagementService stockService,
    CancellationToken ct) =>
{
    return Results.Ok(await stockService.GetMovementsAsync(productId, variantId, employeeId, limit ?? 100, ct));
}).RequireAuthorization();

app.MapGet("/api/stock/alerts", async (StockManagementService stockService, CancellationToken ct) =>
    Results.Ok(await stockService.GetStockAlertsAsync(ct)))
    .RequireAuthorization();

// =============================================================================
// 8. ENDPOINTS DE FUNCIONÁRIOS E CONFIGURAÇÕES DA LOJA (/api/employees, /api/settings)
// =============================================================================
app.MapGet("/api/employees", async (EmployeeAndSettingsService service, CancellationToken ct) =>
    Results.Ok(await service.GetEmployeesAsync(ct)))
    .RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapPost("/api/employees", async (CreateEmployeeRequest request, EmployeeAndSettingsService service, CancellationToken ct) =>
{
    var created = await service.CreateEmployeeAsync(request, ct);
    return Results.Created($"/api/employees/{created.EmployeeId}", created);
}).RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapPut("/api/employees/{id:guid}", async (Guid id, UpdateEmployeeRequest request, EmployeeAndSettingsService service, CancellationToken ct) =>
    Results.Ok(await service.UpdateEmployeeAsync(id, request, ct)))
    .RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapPatch("/api/employees/{id:guid}/status", async (Guid id, bool isActive, EmployeeAndSettingsService service, CancellationToken ct) =>
    Results.Ok(await service.SetEmployeeActiveStatusAsync(id, isActive, ct)))
    .RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapGet("/api/settings", async (EmployeeAndSettingsService service, CancellationToken ct) =>
    Results.Ok(await service.GetAdminStoreSettingsAsync(ct)))
    .RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapPut("/api/settings", async (
    UpdateStoreSettingsRequest request,
    ClaimsPrincipal user,
    EmployeeAndSettingsService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    return Results.Ok(await service.UpdateStoreSettingsAsync(employeeId, request, ct));
}).RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapPost("/api/settings/logo", async (
    HttpRequest httpRequest,
    ClaimsPrincipal user,
    EmployeeAndSettingsService service,
    CancellationToken ct) =>
{
    if (!httpRequest.HasFormContentType)
        throw new DomainException("Envie o logotipo no formato multipart/form-data.");

    var form = await httpRequest.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault()
        ?? throw new DomainException("Nenhum arquivo de logotipo foi enviado.");

    if (file.Length <= 0 || file.Length > SupabaseImageStorageService.MaxUploadSizeBytes)
        throw new DomainException($"O logotipo deve ter entre 1 byte e {SupabaseImageStorageService.MaxUploadSizeBytes / (1024 * 1024)} MB.");

    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    await using var stream = file.OpenReadStream();
    var updated = await service.UploadStoreLogoAsync(employeeId, file.FileName, stream, file.ContentType, ct);
    return Results.Ok(updated);
}).RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapDelete("/api/settings/logo", async (
    ClaimsPrincipal user,
    EmployeeAndSettingsService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    return Results.Ok(await service.RemoveStoreLogoAsync(employeeId, ct));
}).RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapPost("/api/settings/redeem-code", async (
    RedeemActivationCodeRequest request,
    ClaimsPrincipal user,
    EmployeeAndSettingsService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    return Results.Ok(await service.RedeemActivationCodeAsync(request, employeeId, ct));
}).RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapPost("/api/settings/verify-google-account", async (
    VerifyGoogleAccountTokenRequest request,
    ClaimsPrincipal user,
    IHttpClientFactory httpFactory,
    EmployeeAndSettingsService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    var httpClient = httpFactory.CreateClient();
    return Results.Ok(await service.VerifyAndSaveGoogleAccountAsync(
        employeeId,
        request.AccessToken,
        request.OAuthClientId,
        httpClient,
        ct));
}).RequireAuthorization(RoleNames.PolicyAdminOnly);

app.MapDelete("/api/settings/verify-google-account", async (
    ClaimsPrincipal user,
    EmployeeAndSettingsService service,
    CancellationToken ct) =>
{
    var employeeId = Guid.TryParse(user.FindFirstValue("employee_id"), out var empId) ? empId : (Guid?)null;
    return Results.Ok(await service.ClearGoogleAccountVerificationAsync(employeeId, ct));
}).RequireAuthorization(RoleNames.PolicyAdminOnly);

// =============================================================================
// 8.5. ENDPOINTS DE PEDIDOS PIX, RAIO DE ENTREGA E CALCULADORA CORREIOS
// =============================================================================
app.MapGet("/api/orders", async (OrderAndShippingService orderService, CancellationToken ct) =>
    Results.Ok(await orderService.GetOrdersAsync(ct)))
    .RequireAuthorization(RoleNames.PolicyCanManageStock);

app.MapGet("/api/orders/sales-report", async (
    string? period,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
    Results.Ok(await orderService.GetSalesReportAsync(period, ct)))
    .RequireAuthorization(RoleNames.PolicyCanManageStock);

app.MapPost("/api/orders/manual-sale", async (
    RegisterManualSaleRequest request,
    ClaimsPrincipal user,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    var employeeId = Guid.Parse(user.FindFirstValue("employee_id")!);
    return Results.Ok(await orderService.RegisterManualSaleAsync(employeeId, request, ct));
}).RequireAuthorization(RoleNames.PolicyCanManageStock);

app.MapPost("/api/orders/{id:guid}/confirm-pix", async (
    Guid id,
    ClaimsPrincipal user,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    var employeeId = Guid.Parse(user.FindFirstValue("employee_id")!);
    return Results.Ok(await orderService.ConfirmPixPaymentAsync(id, employeeId, ct));
}).RequireAuthorization(RoleNames.PolicyCanManageStock);

app.MapPut("/api/orders/{id:guid}/fulfillment", async (
    Guid id,
    UpdateOrderFulfillmentRequest request,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    return Results.Ok(await orderService.UpdateOrderFulfillmentAsync(id, request, ct));
}).RequireAuthorization(RoleNames.PolicyCanManageStock);

app.MapPost("/api/orders/{id:guid}/cancel", async (
    Guid id,
    string? reason,
    ClaimsPrincipal user,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    var employeeId = Guid.Parse(user.FindFirstValue("employee_id")!);
    return Results.Ok(await orderService.CancelOrderAsync(id, employeeId, reason, ct));
}).RequireAuthorization(RoleNames.PolicyCanManageStock);

app.MapGet("/api/shipping/simulate-correios", async (
    string? originCep,
    string destCep,
    int? weightGrams,
    int? lengthCm,
    int? widthCm,
    int? heightCm,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    return Results.Ok(await orderService.SimulateCorreiosForSellerAsync(
        originCep,
        destCep,
        weightGrams ?? 450,
        lengthCm ?? 24,
        widthCm ?? 18,
        heightCm ?? 8,
        ct));
}).RequireAuthorization();

// =============================================================================
// 9. ENDPOINTS DE STATUS E SINCRONIZAÇÃO EM NUVEM (Google Cloud Run / 24h Online)
// =============================================================================
app.MapGet("/api/cloud/status", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var slug = ResolveApiStoreSlug(ctx);
    var repository = await gateway.ResolveReadRepositoryAsync(slug, ct) ?? gateway.StoreResolver.DefaultRepository;
    var settings = await repository.GetStoreSettingsAsync(ct);
    var hasEnvSecret = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"));
    return Results.Ok(new
    {
        status = "online",
        provider = settings.OwnServerProvider,
        hostingMode = settings.ServerHostingMode,
        storeName = settings.StoreName,
        storeSlug = settings.StoreSlug,
        service = "ClothingStore",
        requiresSyncKey = hasEnvSecret || !string.IsNullOrWhiteSpace(settings.OwnServerSyncSecret),
        multiTenantEnabled = true,
        centralPublishReady = true,
        hasMercadoPagoAppConfigured = gateway.MercadoPago.GetConfiguredApp().IsOAuthConfigured,
        timestamp = DateTimeOffset.UtcNow
    });
}).AllowAnonymous();

app.MapPost("/api/cloud/check-domain", async (
    CheckDomainAvailabilityRequest request,
    IHttpClientFactory httpFactory,
    IClothingStoreRepository repository,
    CancellationToken ct) =>
{
    var httpClient = httpFactory.CreateClient();
    var result = await ClothingStore.Shared.Helpers.GoogleCloudDomainChecker.EvaluateDomainAsync(
        request.Domain,
        httpClient,
        async domain =>
        {
            var settings = await repository.GetStoreSettingsAsync(ct);
            return string.Equals(settings.OnlineBaseUrl, $"https://{domain}", StringComparison.OrdinalIgnoreCase);
        },
        ct);

    return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/cloud/publish-store", async (
    HttpContext httpContext,
    CloudCatalogSyncPayload payload,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var hasJwtAccess = httpContext.User.Identity?.IsAuthenticated == true &&
        (httpContext.User.IsInRole(RoleNames.Admin) || httpContext.User.HasClaim("can_manage_products", "true"));

    var providedKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = false;
    if (!string.IsNullOrWhiteSpace(providedKey))
    {
        var settings = await repository.GetStoreSettingsAsync(ct);
        var expectedKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"))
            ? Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET")!
            : settings.OwnServerSyncSecret;

        if (!string.IsNullOrWhiteSpace(expectedKey))
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expectedKey.Trim());
            var providedBytes = Encoding.UTF8.GetBytes(providedKey.Trim());
            hasValidSyncKey = expectedBytes.Length == providedBytes.Length &&
                              CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
        }

        if (!hasValidSyncKey)
            return Results.Unauthorized();
    }

    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = httpContext.User.FindFirstValue(ClaimTypes.Email)
                     ?? httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();

    var response = await gateway.PublishStoreAsync(
        payload,
        ResolveApiStoreSlug(httpContext),
        providedKey,
        publishToken,
        hasJwtAccess || hasValidSyncKey,
        ownerEmail,
        GetApiPublicOrigin(httpContext),
        ct);

    if (!response.Success)
        return Results.Conflict(new { error = response.Message });

    return Results.Ok(response);
}).AllowAnonymous();

app.MapGet("/api/cloud/orders", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var hasJwtAccess = httpContext.User.Identity?.IsAuthenticated == true &&
        (httpContext.User.IsInRole(RoleNames.Admin) || httpContext.User.HasClaim("can_manage_stock", "true"));
    var providedKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var settings = await repository.GetStoreSettingsAsync(ct);
    var expectedKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"))
        ? Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET")!
        : settings.OwnServerSyncSecret;
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedKey) && !string.IsNullOrWhiteSpace(expectedKey) &&
                          CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedKey.Trim()), Encoding.UTF8.GetBytes(providedKey.Trim()));

    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = httpContext.User.FindFirstValue(ClaimTypes.Email)
                     ?? httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();

    var res = await gateway.GetStoreOrdersForSyncAsync(
        ResolveApiStoreSlug(httpContext),
        publishToken,
        hasJwtAccess || hasValidSyncKey,
        ownerEmail,
        ct);

    return res is null ? Results.Unauthorized() : Results.Ok(res);
}).AllowAnonymous();

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
}).AllowAnonymous();

app.MapPost("/api/payments/mercadopago/oauth/start", async (
    HttpContext ctx,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var hasJwtAccess = ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole(RoleNames.Admin);
    var providedKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var settings = await repository.GetStoreSettingsAsync(ct);
    var expectedKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"))
        ? Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET")!
        : settings.OwnServerSyncSecret;
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedKey) && !string.IsNullOrWhiteSpace(expectedKey) &&
                          CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedKey.Trim()), Encoding.UTF8.GetBytes(providedKey.Trim()));

    var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
    var callbackUri = $"{GetApiPublicOrigin(ctx).TrimEnd('/')}/api/payments/mercadopago/oauth/callback";

    var (startResp, issuedToken) = await gateway.StartMercadoPagoOAuthAsync(
        ResolveApiStoreSlug(ctx), providedKey, publishToken, hasJwtAccess || hasValidSyncKey, ownerEmail, callbackUri, ct);

    return Results.Ok(new
    {
        isConfigured = startResp.IsConfigured,
        authorizationUrl = startResp.AuthorizationUrl,
        stateToken = startResp.StateToken,
        publishToken = issuedToken,
        message = startResp.Message
    });
}).AllowAnonymous();

app.MapGet("/api/payments/mercadopago/oauth/callback", async (
    string? code,
    string? state,
    string? error,
    string? error_description,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var res = await gateway.CompleteMercadoPagoOAuthCallbackAsync(code, state, error, error_description, ct);
    return Results.Content(MercadoPagoPixService.BuildOAuthCallbackHtml(res), "text/html; charset=utf-8");
}).AllowAnonymous();

app.MapGet("/api/payments/mercadopago/status", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
    Results.Ok(await gateway.GetMercadoPagoStatusAsync(ResolveApiStoreSlug(ctx), ct))).AllowAnonymous();

app.MapPost("/api/payments/mercadopago/disconnect", async (
    HttpContext ctx,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var hasJwtAccess = ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole(RoleNames.Admin);
    var providedKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var settings = await repository.GetStoreSettingsAsync(ct);
    var expectedKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"))
        ? Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET")!
        : settings.OwnServerSyncSecret;
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedKey) && !string.IsNullOrWhiteSpace(expectedKey) &&
                          CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedKey.Trim()), Encoding.UTF8.GetBytes(providedKey.Trim()));

    var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
    var ok = await gateway.DisconnectMercadoPagoAsync(ResolveApiStoreSlug(ctx), publishToken, hasJwtAccess || hasValidSyncKey, ownerEmail, ct);
    return ok ? Results.Ok(new { disconnected = true }) : Results.Unauthorized();
}).AllowAnonymous();

app.MapPost("/api/cloud/sync-state", async (
    HttpContext httpContext,
    CloudCatalogSyncPayload payload,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var hasJwtAccess = httpContext.User.Identity?.IsAuthenticated == true &&
        (httpContext.User.IsInRole(RoleNames.Admin) || httpContext.User.HasClaim("can_manage_products", "true"));

    var providedKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = false;
    if (!string.IsNullOrWhiteSpace(providedKey))
    {
        var settings = await repository.GetStoreSettingsAsync(ct);
        var expectedKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"))
            ? Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET")!
            : settings.OwnServerSyncSecret;

        if (!string.IsNullOrWhiteSpace(expectedKey))
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expectedKey.Trim());
            var providedBytes = Encoding.UTF8.GetBytes(providedKey.Trim());
            hasValidSyncKey = expectedBytes.Length == providedBytes.Length &&
                              CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
        }
    }

    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = httpContext.User.FindFirstValue(ClaimTypes.Email) ?? httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();

    if (!hasJwtAccess && !hasValidSyncKey && string.IsNullOrWhiteSpace(publishToken))
    {
        return Results.Unauthorized();
    }

    var result = await gateway.PublishStoreAsync(
        payload,
        ResolveApiStoreSlug(httpContext),
        providedKey,
        publishToken,
        hasJwtAccess || hasValidSyncKey,
        ownerEmail,
        GetApiPublicOrigin(httpContext),
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
}).AllowAnonymous();

// =============================================================================
// 10. ENDPOINTS DE CONTRATAÇÃO AUTOMÁTICA DE PLANOS & SINCRONIZAÇÃO DE PREÇOS/PIX
// =============================================================================
app.MapPost("/api/platform-sync/plan-orders", async (
    CreatePlatformPlanOrderRequest request,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    var created = await subscriptionService.CreatePlanOrderAsync(request, ct);
    return Results.Created($"/api/platform-sync/plan-orders/{created.Id}", created);
}).AllowAnonymous().RequireRateLimiting("public-orders");

app.MapGet("/api/platform-sync/plan-orders/status", async (
    string orderNumber,
    string claimToken,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
    Results.Ok(await subscriptionService.GetPlanOrderStatusAsync(orderNumber, claimToken, ct)))
    .AllowAnonymous()
    .RequireRateLimiting("auth-strict");

app.MapGet("/api/platform-sync/plan-orders", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    return Results.Ok(await subscriptionService.GetAllOrdersAsync(ct));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/plan-orders/{id:guid}/approve", async (
    Guid id,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    var adminName = httpContext.User.FindFirstValue(ClaimTypes.Name) ?? "Administrador";
    return Results.Ok(await subscriptionService.ApprovePlanOrderAsync(id, adminName, null, ct));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/plan-orders/{id:guid}/cancel", async (
    Guid id,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    return Results.Ok(await subscriptionService.CancelPlanOrderAsync(id, ct));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/owner-pricing", async (
    UpdateOwnerPricingSyncRequest request,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    return Results.Ok(await settingsService.SyncOwnerPricingAsync(request, ct));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/gift-cards/redeem", async (
    PlatformGiftCardRedeemRequest request,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    try
    {
        var result = await settingsService.RedeemGiftCardForRemoteStoreAsync(request, ct);
        return Results.Ok(result);
    }
    catch (DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).AllowAnonymous().RequireRateLimiting("auth-strict");

app.MapGet("/api/platform-sync/gift-cards", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    return Results.Ok(await settingsService.GetActivationCodesForSyncAsync(ct));
}).AllowAnonymous();

app.MapGet("/api/platform-sync/plan-catalog", async (
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    IPlatformMercadoPagoTokenStore tokenStore,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    var isAppConfigured = gateway.MercadoPago.GetConfiguredApp().IsOAuthConfigured;
    var conn = await tokenStore.GetConnectionStatusAsync(isAppConfigured, ct);
    return Results.Ok(new PlatformPlanCatalogDto(
        BasicPlanMonthlyPrice: settings.BasicPlanMonthlyPrice,
        IntermediaryPlanMonthlyPrice: settings.IntermediaryPlanMonthlyPrice,
        PremiumPlanMonthlyPrice: settings.PremiumPlanMonthlyPrice,
        HasOfficialPixKey: !string.IsNullOrWhiteSpace(settings.PicpayPixKey),
        CentralCatalogServerUrl: settings.CentralCatalogBaseUrl,
        HasMercadoPagoAppConfigured: isAppConfigured,
        HasPlatformMercadoPagoConnected: conn.IsConnected,
        PlatformMercadoPagoAccountLabel: conn.AccountLabel));
}).AllowAnonymous();

app.MapPost("/api/platform-sync/gift-cards/sync", async (
    SyncPlatformGiftCardsRequest request,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    if (!IsAuthorizedPlatformSyncRequest(httpContext, settings))
        return Results.Unauthorized();

    return Results.Ok(await settingsService.SyncPlatformGiftCardsAsync(request.GiftCards, ct));
}).AllowAnonymous();

app.Run();
