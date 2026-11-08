using appmaui.blazor.Services;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace appmaui.blazor;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var apiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5180";

        builder.Services.AddSingleton(_ =>
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(600)
            };
            return new HttpClient(handler)
            {
                BaseAddress = new Uri(apiBaseUrl),
                Timeout = TimeSpan.FromMilliseconds(1200)
            };
        });
        builder.Services.AddSingleton<AdminApiClient>();

        // Repositório compartilhado para sincronização imediata com a API e o Site Blazor
        var sharedWorkspaceDb = @"C:\Dev\appmaui.blazor\App_Data\clothingstore_db.json";
        var dbFilePath = Directory.Exists(@"C:\Dev\appmaui.blazor")
            ? sharedWorkspaceDb
            : Path.Combine(FileSystem.AppDataDirectory, "clothingstore_db.json");

        builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        builder.Services.AddSingleton<IJwtTokenService>(sp =>
            new JwtTokenService(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<IClothingStoreRepository>(sp =>
            new ClothingStoreRepository(sp.GetRequiredService<IPasswordHasher>(), dbFilePath));

        builder.Services.AddSingleton<IImageStorageService>(sp =>
        {
            var http = sp.GetRequiredService<HttpClient>();
            var cfg = sp.GetRequiredService<IConfiguration>();
            var uploadsDir = Directory.Exists(@"C:\Dev\appmaui.blazor\src\ClothingStore.Api")
                ? @"C:\Dev\appmaui.blazor\src\ClothingStore.Api\wwwroot"
                : FileSystem.AppDataDirectory;
            return new SupabaseImageStorageService(http, cfg, uploadsDir);
        });

        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<CatalogService>();
        builder.Services.AddSingleton<ProductManagementService>();
        builder.Services.AddSingleton<StockManagementService>();
        builder.Services.AddSingleton<EmployeeAndSettingsService>();
        builder.Services.AddSingleton<MauiAdminGateway>();

        // Inicia automaticamente o servidor do site público Blazor (http://localhost:5190) em segundo plano
        _ = Task.Run(LocalWebServerLauncher.EnsureWebCatalogRunningAsync);

        return builder.Build();
    }
}
