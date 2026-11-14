using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using Microsoft.AspNetCore.Http;

namespace ClothingStore.Web.Services;

/// <summary>
/// Gateway do catálogo web multi-tenant que prioriza a comunicação via ASP.NET Core Web API
/// e possui fallback instantâneo para o repositório compartilhado multi-loja no servidor web.
/// </summary>
public sealed class CatalogDataGateway
{
    private readonly CatalogApiClient _apiClient;
    private readonly CatalogService _localCatalogService;
    private readonly OrderAndShippingService? _localOrderService;
    private readonly MultiTenantGatewayService? _multiTenantGateway;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private static DateTimeOffset _skipRemoteApiUntil = DateTimeOffset.MinValue;

    public CatalogDataGateway(
        CatalogApiClient apiClient,
        CatalogService localCatalogService,
        OrderAndShippingService? localOrderService = null,
        MultiTenantGatewayService? multiTenantGateway = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _apiClient = apiClient;
        _localCatalogService = localCatalogService;
        _localOrderService = localOrderService;
        _multiTenantGateway = multiTenantGateway;
        _httpContextAccessor = httpContextAccessor;
    }

    private async Task<T> ExecuteWithFallbackAsync<T>(Func<Task<T>> remoteCall, Func<Task<T>> localFallback)
    {
        if (DateTimeOffset.UtcNow >= _skipRemoteApiUntil)
        {
            try
            {
                return await remoteCall();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }

        return await localFallback();
    }

    private async Task<CatalogService?> ResolveLocalCatalogServiceAsync(string? storeSlug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storeSlug) || _multiTenantGateway is null)
            return _localCatalogService;

        var repo = await _multiTenantGateway.ResolveReadRepositoryAsync(storeSlug, ct);
        return repo is null ? null : _multiTenantGateway.CreateCatalogService(repo);
    }

    private async Task<OrderAndShippingService?> ResolveLocalOrderServiceAsync(string? storeSlug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storeSlug) || _multiTenantGateway is null)
            return _localOrderService;

        var repo = await _multiTenantGateway.ResolveReadRepositoryAsync(storeSlug, ct);
        return repo is null ? null : _multiTenantGateway.CreateOrderService(repo);
    }

    private string? GetCurrentPublicOrigin()
    {
        var req = _httpContextAccessor?.HttpContext?.Request;
        if (req is null || !req.Host.HasValue)
            return null;

        var scheme = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
        return $"{scheme}://{req.Host.Value}";
    }

    public Task<PublicStoreSettingsDto?> GetStoreInfoOrNullAsync(string? storeSlug = null, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync<PublicStoreSettingsDto?>(
            async () =>
            {
                try
                {
                    return await _apiClient.GetStoreInfoAsync(storeSlug, ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }
            },
            async () =>
            {
                var svc = await ResolveLocalCatalogServiceAsync(storeSlug, ct);
                return svc is null ? null : await svc.GetPublicStoreSettingsAsync(ct);
            });

    public async Task<PublicStoreSettingsDto> GetStoreInfoAsync(string? storeSlug = null, CancellationToken ct = default)
    {
        var resolved = await GetStoreInfoOrNullAsync(storeSlug, ct);
        return resolved ?? await _localCatalogService.GetPublicStoreSettingsAsync(ct);
    }

    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(string? storeSlug = null, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetCategoriesAsync(storeSlug, ct),
            async () =>
            {
                var svc = await ResolveLocalCatalogServiceAsync(storeSlug, ct);
                return svc is null ? [] : await svc.GetPublicCategoriesAsync(ct);
            });

    public Task<CatalogFiltersDto> GetFiltersAsync(string? storeSlug = null, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetFiltersAsync(storeSlug, ct),
            async () =>
            {
                var svc = await ResolveLocalCatalogServiceAsync(storeSlug, ct);
                return svc is null ? new CatalogFiltersDto([], [], []) : await svc.GetCatalogFiltersAsync(ct);
            });

    public Task<IReadOnlyList<CatalogProductCardDto>> GetProductsAsync(
        CatalogQueryFilter filter,
        string? storeSlug = null,
        CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetProductsAsync(filter, storeSlug, ct),
            async () =>
            {
                var svc = await ResolveLocalCatalogServiceAsync(storeSlug, ct);
                return svc is null ? [] : await svc.GetCatalogProductsAsync(filter, ct);
            });

    public Task<ProductDetailDto?> GetProductDetailAsync(
        string slugOrId,
        string? storeSlug = null,
        CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetProductDetailAsync(slugOrId, storeSlug, ct),
            async () =>
            {
                var svc = await ResolveLocalCatalogServiceAsync(storeSlug, ct);
                return svc is null ? null : await svc.GetPublicProductDetailAsync(slugOrId, ct);
            });

    public Task<CepAddressLookupDto?> LookupCepAsync(string cep, CancellationToken ct = default) =>
        ExecuteWithFallbackAsync<CepAddressLookupDto?>(
            () => _apiClient.LookupCepAsync(cep, ct),
            async () => _localOrderService is not null
                ? await _localOrderService.LookupCepAsync(cep, ct)
                : ClothingStore.Shared.Helpers.BrazilShippingCalculator.ResolveRegionFromCep(cep));

    public Task<ShippingQuoteResponse> QuoteShippingAsync(
        ShippingQuoteRequest request,
        string? storeSlug = null,
        CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.QuoteShippingAsync(request, storeSlug, ct),
            async () =>
            {
                var orderSvc = await ResolveLocalOrderServiceAsync(storeSlug, ct);
                return orderSvc is not null
                    ? await orderSvc.QuoteShippingForBuyerAsync(request, ct)
                    : throw new InvalidOperationException("Serviço de cálculo de entrega indisponível.");
            });

    public Task<StoreOrderDto> PlacePixOrderAsync(
        CreatePixOrderRequest request,
        string? storeSlug = null,
        CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.PlacePixOrderAsync(request, storeSlug, ct),
            async () =>
            {
                if (_multiTenantGateway is not null)
                {
                    return await _multiTenantGateway.PlacePixOrderForStoreAsync(
                        storeSlug,
                        request,
                        GetCurrentPublicOrigin(),
                        ct);
                }

                return _localOrderService is not null
                    ? await _localOrderService.PlacePixOrderAsync(request, ct)
                    : throw new InvalidOperationException("Serviço de pedidos Pix indisponível.");
            });

    public Task<PublicOrderStatusDto?> GetPublicOrderStatusAsync(
        Guid orderId,
        string? statusToken,
        string? storeSlug = null,
        CancellationToken ct = default) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.GetPublicOrderStatusAsync(orderId, statusToken, storeSlug, ct),
            async () =>
            {
                if (_multiTenantGateway is not null)
                {
                    return await _multiTenantGateway.GetPublicOrderStatusAsync(storeSlug, orderId, statusToken, ct);
                }

                return _localOrderService is not null
                    ? await _localOrderService.GetPublicOrderStatusAsync(orderId, statusToken, null, ct)
                    : null;
            });
}
