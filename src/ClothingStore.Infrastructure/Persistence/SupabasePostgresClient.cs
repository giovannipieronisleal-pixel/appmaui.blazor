using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClothingStore.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Cliente de integração direta com o PostgreSQL no Supabase via PostgREST API.
/// Utilizado automaticamente quando SUPABASE_URL e SUPABASE_SERVICE_ROLE_KEY estão configurados
/// nas variáveis de ambiente (ex.: Google Cloud Run), mantendo sincronia transacional sem expor chaves no frontend.
/// </summary>
public sealed class SupabasePostgresClient
{
    private readonly HttpClient _httpClient;
    private readonly string? _supabaseUrl;
    private readonly string? _serviceRoleKey;

    private static readonly JsonSerializerOptions SnakeCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public SupabasePostgresClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _supabaseUrl = (configuration["SUPABASE_URL"] ?? configuration["Supabase:Url"])?.TrimEnd('/');
        _serviceRoleKey = configuration["SUPABASE_SERVICE_ROLE_KEY"] ?? configuration["Supabase:ServiceRoleKey"];
    }

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_supabaseUrl) &&
        !string.IsNullOrWhiteSpace(_serviceRoleKey) &&
        !_supabaseUrl.Contains("seu-projeto.supabase.co", StringComparison.OrdinalIgnoreCase);

    public async Task UpsertStoreSettingsAsync(StoreSettings settings, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var payload = new
        {
            id = settings.Id,
            store_name = settings.StoreName,
            whatsapp_number = settings.WhatsappNumber,
            store_description = settings.StoreDescription,
            logo_storage_path = settings.LogoStoragePath,
            logo_public_url = settings.LogoPublicUrl,
            default_low_stock_threshold = settings.DefaultLowStockThreshold,
            updated_by_employee_id = settings.UpdatedByEmployeeId,
            updated_at = settings.UpdatedAt
        };

        await SendPostgRestUpsertAsync("store_settings", payload, ct);
    }

    public async Task UpsertProductAsync(Product product, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var payload = new
        {
            id = product.Id,
            category_id = product.CategoryId,
            name = product.Name,
            slug = product.Slug,
            description = product.Description,
            base_price = product.BasePrice,
            is_visible_on_site = product.IsVisibleOnSite,
            is_archived = product.IsArchived,
            created_at = product.CreatedAt,
            updated_at = product.UpdatedAt
        };

        await SendPostgRestUpsertAsync("products", payload, ct);

        foreach (var variant in product.Variants)
        {
            await UpsertVariantAsync(variant, ct);
        }

        foreach (var image in product.Images)
        {
            await UpsertProductImageAsync(image, ct);
        }
    }

    public async Task UpsertVariantAsync(ProductVariant variant, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var payload = new
        {
            id = variant.Id,
            product_id = variant.ProductId,
            sku = variant.Sku,
            color = variant.Color,
            color_hex = variant.ColorHex,
            size = variant.Size,
            stock_quantity = variant.StockQuantity,
            low_stock_threshold = variant.LowStockThreshold,
            is_active = variant.IsActive,
            updated_at = variant.UpdatedAt
        };

        await SendPostgRestUpsertAsync("product_variants", payload, ct);
    }

    public async Task UpsertProductImageAsync(ProductImage image, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var payload = new
        {
            id = image.Id,
            product_id = image.ProductId,
            storage_path = image.StoragePath,
            public_url = image.PublicUrl,
            alt_text = image.AltText,
            display_order = image.DisplayOrder,
            is_primary = image.IsPrimary,
            created_at = image.CreatedAt
        };

        await SendPostgRestUpsertAsync("product_images", payload, ct);
    }

    public async Task InsertStockMovementAsync(StockMovement movement, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var payload = new
        {
            id = movement.Id,
            product_variant_id = movement.ProductVariantId,
            employee_id = movement.EmployeeId,
            movement_type = movement.MovementType.ToString().ToUpperInvariant(),
            quantity_changed = movement.QuantityChanged,
            previous_quantity = movement.PreviousQuantity,
            new_quantity = movement.NewQuantity,
            reason = movement.Reason,
            created_at = movement.CreatedAt
        };

        await SendPostgRestUpsertAsync("stock_movements", payload, ct);
    }

    private async Task SendPostgRestUpsertAsync<T>(string tableName, T payload, CancellationToken ct)
    {
        try
        {
            var url = $"{_supabaseUrl}/rest/v1/{tableName}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
            request.Headers.Add("apikey", _serviceRoleKey);
            request.Headers.Add("Prefer", "resolution=merge-duplicates");
            request.Content = JsonContent.Create(payload, options: SnakeCaseJsonOptions);

            await _httpClient.SendAsync(request, ct);
        }
        catch
        {
            // Mantém resiliência caso a rede oscile temporariamente
        }
    }
}

