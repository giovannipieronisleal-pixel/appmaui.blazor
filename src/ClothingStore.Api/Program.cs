using System.Security.Claims;
using ClothingStore.Api.Security;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Suporte automático à variável PORT exigida pelo Google Cloud Run
var cloudRunPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(cloudRunPort) && int.TryParse(cloudRunPort, out var parsedPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");
}

// Configuração de CORS para permitir chamadas do Blazor Web App e App Mobile
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
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
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "ClothingStore.Api",
    timestamp = DateTimeOffset.UtcNow
}));

// =============================================================================
// 1. ENDPOINTS PÚBLICOS DO CATÁLOGO (/api/public/...)
// =============================================================================
var publicGroup = app.MapGroup("/api/public").AllowAnonymous();

publicGroup.MapGet("/store-info", async (CatalogService catalogService, CancellationToken ct) =>
    Results.Ok(await catalogService.GetPublicStoreSettingsAsync(ct)));

publicGroup.MapGet("/categories", async (CatalogService catalogService, CancellationToken ct) =>
    Results.Ok(await catalogService.GetPublicCategoriesAsync(ct)));

publicGroup.MapGet("/filters", async (CatalogService catalogService, CancellationToken ct) =>
    Results.Ok(await catalogService.GetCatalogFiltersAsync(ct)));

publicGroup.MapGet("/products", async (
    string? search,
    Guid? categoryId,
    string? categorySlug,
    string? color,
    string? size,
    bool? onlyInStock,
    CatalogService catalogService,
    CancellationToken ct) =>
{
    var filter = new CatalogQueryFilter(
        search,
        categoryId,
        categorySlug,
        color,
        size,
        onlyInStock ?? false);

    return Results.Ok(await catalogService.GetCatalogProductsAsync(filter, ct));
});

publicGroup.MapGet("/products/{slugOrId}", async (
    string slugOrId,
    CatalogService catalogService,
    CancellationToken ct) =>
{
    var product = await catalogService.GetPublicProductDetailAsync(slugOrId, ct);
    return product is not null
        ? Results.Ok(product)
        : Results.NotFound(new { error = "Produto não encontrado ou indisponível no catálogo." });
});

// =============================================================================
// 2. ENDPOINTS DE AUTENTICAÇÃO E ENTRADA POR E-MAIL (/api/auth/...)
// =============================================================================
var authGroup = app.MapGroup("/api/auth");

authGroup.MapGet("/setup-status", async (AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.GetSetupStatusAsync(ct))).AllowAnonymous();

authGroup.MapPost("/register-initial-admin", async (RegisterInitialAdminRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.RegisterInitialAdminByEmailAsync(request, ct))).AllowAnonymous();

authGroup.MapPost("/login", async (LoginRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.LoginAsync(request, ct))).AllowAnonymous();

authGroup.MapPost("/email-code/request", async (RequestEmailLoginCodeRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.RequestEmailLoginCodeAsync(request, ct))).AllowAnonymous();

authGroup.MapPost("/email-code/verify", async (VerifyEmailLoginCodeRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.LoginWithEmailCodeAsync(request, ct))).AllowAnonymous();

authGroup.MapPost("/forgot-password", async (ForgotPasswordRequest request, AuthService authService, CancellationToken ct) =>
    Results.Ok(await authService.RequestPasswordResetAsync(request, ct))).AllowAnonymous();

authGroup.MapPost("/reset-password", async (ResetPasswordRequest request, AuthService authService, CancellationToken ct) =>
{
    await authService.ResetPasswordAsync(request, ct);
    return Results.Ok(new { message = "Senha redefinida com sucesso." });
}).AllowAnonymous();

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

app.MapPost("/api/products", async (SaveProductRequest request, ProductManagementService service, CancellationToken ct) =>
{
    var created = await service.CreateProductAsync(request, ct);
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
    ProductManagementService service,
    CancellationToken ct) =>
{
    return Results.Ok(await service.UpdateVariantAsync(variantId, request, ct));
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

app.Run();
