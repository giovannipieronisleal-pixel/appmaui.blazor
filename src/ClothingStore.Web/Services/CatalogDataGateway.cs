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
    private readonly OrderAndShippingService? _localOrderService;
    private static DateTimeOffset _skipRemoteApiUntil = DateTimeOffset.MinValue;

    public CatalogDataGateway(
        CatalogApiClient apiClient,
        CatalogService localCatalogService,
        OrderAndShippingService? localOrderService = null)
    {
        _apiClient = apiClient;
        _localCatalogService = localCatalogService;
        _localOrderService = localOrderService;
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

    public Task<CepAddressLookupDto?> LookupCepAsync(string cep, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync<CepAddressLookupDto?>(
            () => _apiClient.LookupCepAsync(cep, ct),
            async () => _localOrderService is not null
                ? await _localOrderService.LookupCepAsync(cep, ct)
                : ClothingStore.Shared.Helpers.BrazilShippingCalculator.ResolveRegionFromCep(cep));

    public Task<ShippingQuoteResponse> QuoteShippingAsync(ShippingQuoteRequest request, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.QuoteShippingAsync(request, ct),
            () => _localOrderService is not null
                ? _localOrderService.QuoteShippingForBuyerAsync(request, ct)
                : throw new InvalidOperationException("Serviço de cálculo de entrega indisponível."));

    public Task<StoreOrderDto> PlacePixOrderAsync(CreatePixOrderRequest request, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.PlacePixOrderAsync(request, ct),
            () => _localOrderService is not null
                ? _localOrderService.PlacePixOrderAsync(request, ct)
                : throw new InvalidOperationException("Serviço de pedidos Pix indisponível."));
}
    