using System.Text.Json;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Gera o Web App Manifest (PWA) da vitrine web utilizando os metadados da loja ativa.
/// </summary>
public static class PwaManifestBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static string BuildJson(PublicStoreSettingsDto? settings)
    {
        var storeName = string.IsNullOrWhiteSpace(settings?.StoreName)
            ? "YouLink Loja"
            : settings.StoreName.Trim();

        var shortName = BuildShortName(storeName);

        var description = string.IsNullOrWhiteSpace(settings?.StoreDescription)
            ? "Catálogo online de roupas com pedido rápido pelo WhatsApp."
            : settings.StoreDescription.Trim();

        var payload = new
        {
            name = storeName,
            short_name = shortName,
            description,
            id = "/",
            start_url = "/",
            scope = "/",
            display = "standalone",
            orientation = "portrait-primary",
            lang = "pt-BR",
            theme_color = "#14151B",
            background_color = "#14151B",
            icons = new object[]
            {
                new
                {
                    src = "/icons/icon-192.png",
                    sizes = "192x192",
                    type = "image/png",
                    purpose = "any"
                },
                new
                {
                    src = "/icons/icon-512.png",
                    sizes = "512x512",
                    type = "image/png",
                    purpose = "any"
                },
                new
                {
                    src = "/icons/icon-maskable-512.png",
                    sizes = "512x512",
                    type = "image/png",
                    purpose = "maskable"
                }
            }
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    internal static string BuildShortName(string fullName)
    {
        var trimmed = string.IsNullOrWhiteSpace(fullName) ? "YouLink" : fullName.Trim();
        if (trimmed.Length <= 16)
        {
            return trimmed;
        }

        return trimmed[..15].TrimEnd() + "…";
    }
}
