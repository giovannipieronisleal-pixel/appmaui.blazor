using ClothingStore.Application.Services;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Web.Services;

/// <summary>
/// Gateway do catálogo web que prioriza a comunicação via ASP.NET Core Web API
/// e possui fallback transparente para o repositório compartilhado.
/// </summary>
public sealed class CatalogDataGateway
{
    private readonly CatalogApiClient _apiClient;
    private readonly CatalogService _localCatalogService;

    public CatalogDataGateway(CatalogApiClient apiClient, CatalogService localCatalogService)
    {
        _apiClient = apiClient;
        _localCatalogService = localCatalogService;
    }

    public async Task<PublicStoreSettingsDto> GetStoreInfoAsync(CancellationToken ct = default)
    {
        try
        {
            return await _apiClient.GetStoreInfoAsync(ct);
        }
        catch
        {
            return await _localCatalogService.GetPublicStoreSettingsAsync(ct);
        }
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _apiClient.GetCategoriesAsync(ct);
        }
        catch
        {
            return await _localCatalogService.GetPublicCategoriesAsync(ct);
        }
    }

    public async Task<CatalogFiltersDto> GetFiltersAsync(CancellationToken ct = default)
    {
        try
        {
            return await _apiClient.GetFiltersAsync(ct);
        }
        catch
        {
            return await _localCatalogService.GetCatalogFiltersAsync(ct);
        }
    }

    public async Task<IReadOnlyList<CatalogProductCardDto>> GetProductsAsync(
        CatalogQueryFilter filter,
        CancellationToken ct = default)
    {
        try
        {
            return await _apiClient.GetProductsAsync(filter, ct);
        }
        catch
        {
            return await _localCatalogService.GetCatalogProductsAsync(filter, ct);
        }
    }

    public async Task<ProductDetailDto?> GetProductDetailAsync(string slugOrId, CancellationToken ct = default)
    {
        try
        {
            return await _apiClient.GetProductDetailAsync(slugOrId, ct);
        }
        catch
        {
            return await _localCatalogService.GetPublicProductDetailAsync(slugOrId, ct);
        }
    }
}
