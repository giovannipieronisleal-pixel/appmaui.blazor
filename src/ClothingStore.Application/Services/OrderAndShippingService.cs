using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Application.Services;

/// <summary>
/// Caso de uso de compras diretas no aplicativo/site exclusivamente via Pix (Meia Versão e Premium),
/// com endereço completo do comprador, validação de distância/área de entrega configurada pelo vendedor,
/// cálculo de frete dos Correios (PAC/SEDEX) e gestão de envio do vendedor.
/// </summary>
public sealed class OrderAndShippingService
{
    private readonly IClothingStoreRepository _repository;
    private readonly HttpClient? _httpClient;

    public OrderAndShippingService(IClothingStoreRepository repository, HttpClient? httpClient = null)
    {
        _repository = repository;
        _httpClient = httpClient;
    }

    public async Task<CepAddressLookupDto> LookupCepAsync(string rawCep, CancellationToken ct = default)
    {
        if (!BrazilShippingCalculator.TrySanitizeCep(rawCep, out var digits))
            throw new DomainException("Informe um CEP válido com 8 dígitos (ex.: 01310-100).");

        return await BrazilShippingCalculator.LookupCepAsync(digits, _httpClient, ct);
    }

    public async Task<ShippingQuoteResponse> QuoteShippingForBuyerAsync(
        ShippingQuoteRequest request,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var scopeDesc = BrazilShippingCalculator.DeliveryScopeModes.Describe(
            settings.DeliveryScopeMode,
            settings.MaxDeliveryDistanceKm,
            settings.OriginCity,
            settings.OriginState);

        if (!BrazilShippingCalculator.TrySanitizeCep(request.DestinationCep, out var destDigits))
            throw new DomainException("Informe um CEP de entrega válido com 8 dígitos.");

        var destInfo = await BrazilShippingCalculator.LookupCepAsync(destDigits, _httpClient, ct);
        var destCity = string.IsNullOrWhiteSpace(request.DestinationCity) ? destInfo.City : request.DestinationCity.Trim();
        var destState = string.IsNullOrWhiteSpace(request.DestinationState) ? destInfo.State : request.DestinationState.Trim().ToUpperInvariant();

        double? originLat = null;
        double? originLon = null;
        if (BrazilShippingCalculator.TrySanitizeCep(settings.OriginCep, out var originDigits))
        {
            var originRegion = BrazilShippingCalculator.ResolveRegionFromCep(originDigits);
            originLat = originRegion.Latitude;
            originLon = originRegion.Longitude;
        }

        var eligibility = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            settings.OriginCep,
            settings.OriginCity,
            settings.OriginState,
            settings.DeliveryScopeMode,
            settings.MaxDeliveryDistanceKm,
            destDigits,
            destCity,
            destState,
            originLat,
            originLon,
            destInfo.Latitude,
            destInfo.Longitude);

        CorreiosQuoteDto? correiosQuote = null;
        if (BrazilShippingCalculator.TrySanitizeCep(settings.OriginCep, out _))
        {
            var weight = request.WeightGrams > 0 ? request.WeightGrams : settings.DefaultPackageWeightGrams;
            correiosQuote = BrazilShippingCalculator.EstimateCorreios(
                settings.OriginCep!,
                destDigits,
                weight,
                request.LengthCm,
                request.WidthCm,
                request.HeightCm,
                eligibility.OriginState,
                eligibility.DestState,
                eligibility.DistanceKm);
        }

        return new ShippingQuoteResponse(
            settings.IsDirectPixCheckoutReady(),
            eligibility,
            correiosQuote,
            scopeDesc);
    }

    public async Task<CorreiosQuoteDto> SimulateCorreiosForSellerAsync(
        string? originCep,
        string destCep,
        int weightGrams = 450,
        int lengthCm = 24,
        int widthCm = 18,
        int heightCm = 8,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var effectiveOrigin = string.IsNullOrWhiteSpace(originCep) ? settings.OriginCep : originCep;

        if (!BrazilShippingCalculator.TrySanitizeCep(effectiveOrigin, out var originDigits))
            throw new DomainException("Informe um CEP de origem válido com 8 dígitos para simular os Correios.");

        if (!BrazilShippingCalculator.TrySanitizeCep(destCep, out var destDigits))
            throw new DomainException("Informe um CEP de destino válido com 8 dígitos para simular os Correios.");

        var origInfo = await BrazilShippingCalculator.LookupCepAsync(originDigits, _httpClient, ct);
        var destInfo = await BrazilShippingCalculator.LookupCepAsync(destDigits, _httpClient, ct);

        var distanceKm = originDigits == destDigits
            ? 0.5
            : Math.Max(2.0, BrazilShippingCalculator.CalculateDistanceKm(
                origInfo.Latitude,
                origInfo.Longitude,
                destInfo.Latitude,
                destInfo.Longitude));

        return BrazilShippingCalculator.EstimateCorreios(
            originDigits,
            destDigits,
            weightGrams > 0 ? weightGrams : settings.DefaultPackageWeightGrams,
            lengthCm,
            widthCm,
            heightCm,
            origInfo.State,
            destInfo.State,
            distanceKm);
    }

    public async Task<StoreOrderDto> PlacePixOrderAsync(
        CreatePixOrderRequest request,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        if (!settings.CanUseDirectPixSales())
        {
            throw new DomainException(
                "Vendas diretas pelo app estão disponíveis apenas para lojas a partir da Meia Versão (Intermediário) até a Premium.");
        }

        if (!settings.DirectPixSalesEnabled)
        {
            throw new DomainException(
                "O vendedor ainda não ativou as vendas diretas por Pix nas configurações da loja.");
        }

        if (!settings.IsDirectPixCheckoutReady())
        {
            throw new DomainException(
                "Para receber compras diretas por Pix, o vendedor precisa cadastrar uma Chave Pix válida e um CEP de origem.");
        }

        var product = await _repository.GetProductBySlugOrIdAsync(request.ProductId.ToString(), ct);
        if (product is null || product.IsArchived || !product.IsVisibleOnSite)
            throw new DomainException("O produto selecionado não está mais disponível para venda no catálogo.");

        var variant = product.Variants.FirstOrDefault(v => v.Id == request.ProductVariantId && v.IsActive)
            ?? throw new DomainException("A cor/tamanho selecionada não está disponível.");

        if (!BrazilShippingCalculator.TrySanitizeCep(request.Cep, out var destDigits))
            throw new DomainException("Informe um CEP de entrega válido com 8 dígitos.");

        var destInfo = await BrazilShippingCalculator.LookupCepAsync(destDigits, _httpClient, ct);
        var street = string.IsNullOrWhiteSpace(request.Street) ? destInfo.Street : request.Street.Trim();
        var neighborhood = string.IsNullOrWhiteSpace(request.Neighborhood) ? destInfo.Neighborhood : request.Neighborhood.Trim();
        var city = string.IsNullOrWhiteSpace(request.City) ? destInfo.City : request.City.Trim();
        var state = string.IsNullOrWhiteSpace(request.State) ? destInfo.State : request.State.Trim().ToUpperInvariant();

        var eligibility = BrazilShippingCalculator.EvaluateDeliveryEligibility(
            settings.OriginCep,
            settings.OriginCity,
            settings.OriginState,
            settings.DeliveryScopeMode,
            settings.MaxDeliveryDistanceKm,
            destDigits,
            city,
            state,
            destLatOverride: destInfo.Latitude,
            destLonOverride: destInfo.Longitude);

        if (!eligibility.IsAllowed)
            throw new DomainException(eligibility.ReasonMessage);

        var totalWeight = Math.Clamp(settings.DefaultPackageWeightGrams * Math.Max(1, request.Quantity), 300, 30000);
        var correiosQuote = BrazilShippingCalculator.EstimateCorreios(
            settings.OriginCep!,
            destDigits,
            totalWeight,
            originStateOverride: eligibility.OriginState,
            destStateOverride: eligibility.DestState,
            distanceKmOverride: eligibility.DistanceKm);

        var receiverName = string.IsNullOrWhiteSpace(settings.StorePixReceiverName)
            ? settings.StoreName
            : settings.StorePixReceiverName;

        var orderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var orderNumber = $"YL-{now:yyyyMMdd}-{orderId.ToString("N")[..4].ToUpperInvariant()}";
        var normalizedPixKey = PixPayloadBuilder.NormalizePixKey(settings.StorePixKey!);
        var pixBrCode = PixPayloadBuilder.BuildBrCode(
            normalizedPixKey,
            receiverName,
            settings.OriginCity ?? eligibility.OriginCity,
            Math.Round(product.BasePrice * request.Quantity, 2),
            orderNumber.Replace("-", ""));

        var order = StoreOrder.CreatePixOrder(
            orderId,
            orderNumber,
            product,
            variant,
            request.Quantity,
            request.CustomerName,
            request.CustomerWhatsapp,
            destDigits,
            street,
            request.Number,
            request.Complement,
            neighborhood,
            city,
            state,
            request.CustomerNotes,
            eligibility.DistanceKm,
            correiosQuote.ZoneName,
            correiosQuote.PacPrice,
            correiosQuote.PacDaysMin,
            correiosQuote.PacDaysMax,
            correiosQuote.SedexPrice,
            correiosQuote.SedexDaysMin,
            correiosQuote.SedexDaysMax,
            normalizedPixKey,
            pixBrCode);

        await _repository.SaveOrderAsync(order, ct);
        settings.RecordActivity();
        await _repository.UpdateStoreSettingsAsync(settings, ct);

        return MapToDto(order);
    }

    public async Task<IReadOnlyList<StoreOrderDto>> GetOrdersAsync(CancellationToken ct = default)
    {
        var orders = await _repository.GetOrdersAsync(ct);
        return orders.Select(MapToDto).ToList();
    }

    public async Task<StoreOrderDto?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await _repository.GetOrderByIdAsync(orderId, ct);
        return order is null ? null : MapToDto(order);
    }

    public async Task<StoreOrderDto> ConfirmPixPaymentAsync(
        Guid orderId,
        Guid employeeId,
        CancellationToken ct = default)
    {
        var order = await _repository.GetOrderByIdAsync(orderId, ct)
            ?? throw new DomainException("Pedido não encontrado.");

        if (employeeId == Guid.Empty)
            throw new DomainException("Usuário administrador inválido para confirmar o pagamento.");

        order.ConfirmPixPayment();

        if (!order.StockDeducted)
        {
            foreach (var item in order.Items)
            {
                var product = await _repository.GetProductBySlugOrIdAsync(item.ProductId.ToString(), ct)
                    ?? throw new DomainException($"Produto '{item.ProductName}' não encontrado para baixa de estoque.");

                var variant = product.Variants.FirstOrDefault(v => v.Id == item.ProductVariantId)
                    ?? throw new DomainException($"Variante {item.Color}/{item.Size} não encontrada.");

                var movement = variant.ApplyStockMovement(
                    employeeId,
                    StockMovementType.Out,
                    item.Quantity,
                    $"Venda Pix #{order.OrderNumber} — {order.CustomerName}");

                await _repository.RecordStockMovementAsync(variant, movement, ct);
                await _repository.UpdateProductAsync(product, ct);
            }

            order.StockDeducted = true;
        }

        await _repository.SaveOrderAsync(order, ct);

        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.RecordActivity();
        await _repository.UpdateStoreSettingsAsync(settings, ct);

        return MapToDto(order);
    }

    public async Task<StoreOrderDto> UpdateOrderFulfillmentAsync(
        Guid orderId,
        UpdateOrderFulfillmentRequest request,
        CancellationToken ct = default)
    {
        var order = await _repository.GetOrderByIdAsync(orderId, ct)
            ?? throw new DomainException("Pedido não encontrado.");

        order.UpdateFulfillment(
            request.FulfillmentMethod,
            request.TrackingCode,
            request.AgreedShippingCost,
            request.SellerNotes,
            request.MarkAsShipped,
            request.MarkAsDelivered);

        await _repository.SaveOrderAsync(order, ct);

        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.RecordActivity();
        await _repository.UpdateStoreSettingsAsync(settings, ct);

        return MapToDto(order);
    }

    public async Task<StoreOrderDto> CancelOrderAsync(
        Guid orderId,
        Guid employeeId,
        string? reason = null,
        CancellationToken ct = default)
    {
        var order = await _repository.GetOrderByIdAsync(orderId, ct)
            ?? throw new DomainException("Pedido não encontrado.");

        order.Cancel(reason);

        if (order.StockDeducted && employeeId != Guid.Empty)
        {
            foreach (var item in order.Items)
            {
                var product = await _repository.GetProductBySlugOrIdAsync(item.ProductId.ToString(), ct);
                var variant = product?.Variants.FirstOrDefault(v => v.Id == item.ProductVariantId);
                if (product is not null && variant is not null)
                {
                    var movement = variant.ApplyStockMovement(
                        employeeId,
                        StockMovementType.In,
                        item.Quantity,
                        $"Estorno Pedido Pix cancelado #{order.OrderNumber}");

                    await _repository.RecordStockMovementAsync(variant, movement, ct);
                    await _repository.UpdateProductAsync(product, ct);
                }
            }

            order.StockDeducted = false;
        }

        await _repository.SaveOrderAsync(order, ct);
        return MapToDto(order);
    }

    private static StoreOrderDto MapToDto(StoreOrder o) => new(
        o.Id,
        o.OrderNumber,
        o.PaymentMethod,
        o.Status,
        o.ItemsTotal,
        o.EstimatedCorreiosPac,
        o.EstimatedCorreiosSedex,
        o.AgreedShippingCost,
        o.TotalPixAmount,
        o.CustomerName,
        o.CustomerWhatsapp,
        o.DeliveryCep,
        o.DeliveryStreet,
        o.DeliveryNumber,
        o.DeliveryComplement,
        o.DeliveryNeighborhood,
        o.DeliveryCity,
        o.DeliveryState,
        o.FormatDeliveryAddress(),
        o.CustomerNotes,
        o.DistanceKmFromSeller,
        o.ShippingZoneLabel,
        o.EstimatedPacDaysMin,
        o.EstimatedPacDaysMax,
        o.EstimatedSedexDaysMin,
        o.EstimatedSedexDaysMax,
        o.PixKey,
        o.PixBrCode,
        o.FulfillmentMethod,
        o.TrackingCode,
        o.SellerNotes,
        o.StockDeducted,
        o.CreatedAt,
        o.PixConfirmedAt,
        o.ShippedAt,
        o.DeliveredAt,
        o.CancelledAt,
        o.Items.Select(i => new StoreOrderItemDto(
            i.ProductId,
            i.ProductVariantId,
            i.ProductName,
            i.Sku,
            i.Color,
            i.Size,
            i.Quantity,
            i.UnitPrice,
            i.Subtotal)).ToList());
}
