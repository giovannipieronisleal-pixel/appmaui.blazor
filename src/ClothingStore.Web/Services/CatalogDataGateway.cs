using ClothingStore.Application.Services;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Web.Services;

/// <summary>
/// Gateway do catálogo web que prioriza a comunicação via ASP.NET Core Web API
/// e possui fallback instantâneo para o repositório compartilhado quando a API não está rodando em processo separado.
/// </summary>
public sealed class CatalogDataGateway
{
    private readonly CatalogApiClient _apiClient;
    private readonly CatalogService _localCatalogService;
    private static DateTimeOffset _skipRemoteApiUntil = DateTimeOffset.MinValue;

    public CatalogDataGateway(CatalogApiClient apiClient, CatalogService localCatalogService)
    {
        _apiClient = apiClient;
        _localCatalogService = localCatalogService;
    }

    private async Task<T> ExecuteWithFallbackAsync<T>(Func<Task<T>> remoteCall, Func<Task<T>> localFallback)
    {
        if (DateTimeOffset.UtcNow >= _skipRemoteApiUntil)
        {
            try
            {
                return await remoteCall();
            }
            catch
            {
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }

        return await localFallback();
    }

    public Task<PublicStoreSettingsDto> GetStoreInfoAsync(CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetStoreInfoAsync(ct),
            () => _localCatalogService.GetPublicStoreSettingsAsync(ct));

    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetCategoriesAsync(ct),
            () => _localCatalogService.GetPublicCategoriesAsync(ct));

    public Task<CatalogFiltersDto> GetFiltersAsync(CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetFiltersAsync(ct),
            () => _localCatalogService.GetCatalogFiltersAsync(ct));

    public Task<IReadOnlyList<CatalogProductCardDto>> GetProductsAsync(
        CatalogQueryFilter filter,
        CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetProductsAsync(filter, ct),
            () => _localCatalogService.GetCatalogProductsAsync(filter, ct));

    public Task<ProductDetailDto?> GetProductDetailAsync(string slugOrId, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetProductDetailAsync(slugOrId, ct),
            () => _localCatalogService.GetPublicProductDetailAsync(slugOrId, ct));
}
    