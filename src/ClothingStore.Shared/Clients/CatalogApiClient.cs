using System.Net.Http.Json;
using System.Web;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Clients;

/// <summary>
/// Cliente HTTP tipado para consumo dos endpoints públicos do catálogo pelo site Blazor Web App.
/// </summary>
public sealed class CatalogApiClient
{
    private readonly HttpClient _httpClient;

    public CatalogApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PublicStoreSettingsDto> GetStoreInfoAsync(CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<PublicStoreSettingsDto>("/api/public/store-info", ct);
        return result ?? new PublicStoreSettingsDto(
            "Aura Vestuário",
            "aura-vestuario",
            "5511999999999",
            "Catálogo digital de moda contemporânea.",
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<List<CategoryDto>>("/api/public/categories", ct) ?? [];
    }

    public async Task<CatalogFiltersDto> GetFiltersAsync(CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<CatalogFiltersDto>("/api/public/filters", ct);
        return result ?? new CatalogFiltersDto([], [], []);
    }

    public async Task<IReadOnlyList<CatalogProductCardDto>> GetProductsAsync(
        CatalogQueryFilter filter,
        CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(filter.Search))
            query["search"] = filter.Search;
        if (filter.CategoryId.HasValue && filter.CategoryId.Value != Guid.Empty)
            query["categoryId"] = filter.CategoryId.Value.ToString();
        if (!string.IsNullOrWhiteSpace(filter.CategorySlug))
            query["categorySlug"] = filter.CategorySlug;
        if (!string.IsNullOrWhiteSpace(filter.Color))
            query["color"] = filter.Color;
        if (!string.IsNullOrWhiteSpace(filter.Size))
            query["size"] = filter.Size;
        if (filter.OnlyInStock)
            query["onlyInStock"] = "true";

        var qs = query.ToString();
        var url = string.IsNullOrWhiteSpace(qs) ? "/api/public/products" : $"/api/public/products?{qs}";
        return await _httpClient.GetFromJsonAsync<List<CatalogProductCardDto>>(url, ct) ?? [];
    }

    public async Task<ProductDetailDto?> GetProductDetailAsync(string slugOrId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"/api/public/products/{Uri.EscapeDataString(slugOrId)}", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct);
    }

    public async Task<CepAddressLookupDto?> LookupCepAsync(string cep, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"/api/public/cep/{Uri.EscapeDataString(cep)}", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<CepAddressLookupDto>(cancellationToken: ct);
    }

    public async Task<ShippingQuoteResponse> QuoteShippingAsync(ShippingQuoteRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/public/shipping-quote", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ShippingQuoteResponse>(cancellationToken: ct))!;
    }

    public async Task<StoreOrderDto> PlacePixOrderAsync(CreatePixOrderRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/public/orders/pix", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StoreOrderDto>(cancellationToken: ct))!;
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var raw = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var errProp))
                throw new InvalidOperationException(errProp.GetString() ?? "Erro na requisição.");
        }
        catch (System.Text.Json.JsonException)
        {
        }

        throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
    }
}

