using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Shared.Clients;
using ClothingStore.Web.Components;
using ClothingStore.Web.Services;

var builder = WebApplication.CreateBuilder(args);

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

// Fallback sincronizado com o mesmo banco compartilhado caso a API não esteja rodando em processo separado
var sharedDbPath = builder.Configuration["Database:FilePath"]
    ?? (Directory.Exists(@"C:\Dev\appmaui.blazor")
        ? @"C:\Dev\appmaui.blazor\App_Data\clothingstore_db.json"
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "App_Data", "clothingstore_db.json")));

builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<IClothingStoreRepository>(sp =>
    new ClothingStoreRepository(sp.GetRequiredService<IPasswordHasher>(), sharedDbPath));
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CatalogDataGateway>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
