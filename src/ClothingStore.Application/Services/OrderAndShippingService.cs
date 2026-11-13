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

        var isExpressMobileWithoutCep = request.ExpressMobileMode && string.IsNullOrWhiteSpace(request.Cep);

        var destDigits = string.Empty;
        string street;
        string number;
        string neighborhood;
        string city;
        string state;
        DeliveryEligibilityDto eligibility;
        CorreiosQuoteDto correiosQuote;

        if (isExpressMobileWithoutCep)
        {
            var fallbackCep = BrazilShippingCalculator.TrySanitizeCep(settings.OriginCep, out var originCepDigits)
                ? originCepDigits
                : "01001000";
            destDigits = fallbackCep;
            street = string.IsNullOrWhiteSpace(request.Street) ? "Retirada / Envio Combinado no App" : request.Street.Trim();
            number = string.IsNullOrWhiteSpace(request.Number) ? "S/N" : request.Number.Trim();
            neighborhood = string.IsNullOrWhiteSpace(request.Neighborhood) ? "Centro" : request.Neighborhood.Trim();
            city = string.IsNullOrWhiteSpace(request.City) ? (settings.OriginCity ?? "Brasil") : request.City.Trim();
            state = string.IsNullOrWhiteSpace(request.State) ? (settings.OriginState ?? "SP") : request.State.Trim().ToUpperInvariant();
            eligibility = new DeliveryEligibilityDto(true, 0, settings.OriginCity ?? city, settings.OriginState ?? state, city, state, "Compra direta pelo celular (Pix).");
            correiosQuote = BrazilShippingCalculator.EstimateCorreios(
                fallbackCep,
                fallbackCep,
                Math.Clamp(settings.DefaultPackageWeightGrams * Math.Max(1, request.Quantity), 300, 30000),
                originStateOverride: eligibility.OriginState,
                destStateOverride: eligibility.DestState,
                distanceKmOverride: 0.5);
        }
        else
        {
            if (!BrazilShippingCalculator.TrySanitizeCep(request.Cep, out destDigits))
                throw new DomainException("Informe um CEP de entrega válido com 8 dígitos.");

            var destInfo = await BrazilShippingCalculator.LookupCepAsync(destDigits, _httpClient, ct);
            street = string.IsNullOrWhiteSpace(request.Street)
                ? (request.ExpressMobileMode ? "A combinar no envio" : destInfo.Street)
                : request.Street.Trim();
            number = string.IsNullOrWhiteSpace(request.Number)
                ? (request.ExpressMobileMode ? "S/N" : string.Empty)
                : request.Number.Trim();
            neighborhood = string.IsNullOrWhiteSpace(request.Neighborhood) ? destInfo.Neighborhood : request.Neighborhood.Trim();
            city = string.IsNullOrWhiteSpace(request.City) ? destInfo.City : request.City.Trim();
            state = string.IsNullOrWhiteSpace(request.State) ? destInfo.State : request.State.Trim().ToUpperInvariant();

            eligibility = BrazilShippingCalculator.EvaluateDeliveryEligibility(
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
            correiosQuote = BrazilShippingCalculator.EstimateCorreios(
                settings.OriginCep!,
                destDigits,
                totalWeight,
                originStateOverride: eligibility.OriginState,
                destStateOverride: eligibility.DestState,
                distanceKmOverride: eligibility.DistanceKm);
        }

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
            string.IsNullOrWhiteSpace(request.CustomerWhatsapp) && request.ExpressMobileMode
                ? settings.WhatsappNumber
                : request.CustomerWhatsapp,
            destDigits,
            street,
            number,
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
            await DeductStockForOrderItemsAsync(
                order,
                employeeId,
                $"Venda Pix #{order.OrderNumber} — {order.CustomerName}",
                ct);
            order.StockDeducted = true;
        }

        await _repository.SaveOrderAsync(order, ct);

        var settings = await _repository.GetStoreSettingsAsync(ct);
        settings.RecordActivity();
        await _repository.UpdateStoreSettingsAsync(settings, ct);

        return MapToDto(order);
    }

    public async Task<SalesReportDto> GetSalesReportAsync(
        string? periodKey = "30d",
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var normalizedPeriod = NormalizeSalesPeriodKey(periodKey);

        if (!settings.CanViewSalesAnalytics())
        {
            return new SalesReportDto(
                IsUnlocked: false,
                EffectivePlanTier: settings.GetEffectivePlanTier(),
                LockMessage: "O relatório de vendas e o total vendido estão disponíveis nas versões pagas (Meia Versão e Premium).",
                PeriodKey: normalizedPeriod,
                TotalConfirmedRevenue: 0m,
                TodayConfirmedRevenue: 0m,
                Last7DaysConfirmedRevenue: 0m,
                Last30DaysConfirmedRevenue: 0m,
                SelectedPeriodConfirmedRevenue: 0m,
                AwaitingPixRevenue: 0m,
                CancelledRevenue: 0m,
                ConfirmedOrdersCount: 0,
                SelectedPeriodOrdersCount: 0,
                AwaitingPixOrdersCount: 0,
                CancelledOrdersCount: 0,
                TotalUnitsSold: 0,
                AverageTicket: 0m,
                TopProducts: [],
                DailySeries: [],
                RecentConfirmedSales: []);
        }

        var brazilOffset = TimeSpan.FromHours(-3);
        var nowBrazil = DateTimeOffset.UtcNow.ToOffset(brazilOffset);
        var todayDate = nowBrazil.Date;
        var start7Days = todayDate.AddDays(-6);
        var start30Days = todayDate.AddDays(-29);

        var orders = await _repository.GetOrdersAsync(ct);

        var awaitingOrders = orders.Where(o => o.Status == StoreOrder.StatusAwaitingPix).ToList();
        var cancelledOrders = orders.Where(o => o.Status == StoreOrder.StatusCancelled).ToList();
        var confirmedOrders = orders
            .Where(o => o.IsConfirmedSale())
            .OrderByDescending(GetConfirmedSaleTimestamp)
            .ToList();

        static decimal GetOrderRevenue(StoreOrder order) =>
            order.TotalPixAmount > 0m ? order.TotalPixAmount : order.ItemsTotal;

        var totalConfirmedRevenue = Math.Round(confirmedOrders.Sum(GetOrderRevenue), 2);
        var todayConfirmedRevenue = Math.Round(
            confirmedOrders
                .Where(o => GetConfirmedSaleTimestamp(o).ToOffset(brazilOffset).Date == todayDate)
                .Sum(GetOrderRevenue),
            2);
        var last7DaysRevenue = Math.Round(
            confirmedOrders
                .Where(o => GetConfirmedSaleTimestamp(o).ToOffset(brazilOffset).Date >= start7Days)
                .Sum(GetOrderRevenue),
            2);
        var last30DaysRevenue = Math.Round(
            confirmedOrders
                .Where(o => GetConfirmedSaleTimestamp(o).ToOffset(brazilOffset).Date >= start30Days)
                .Sum(GetOrderRevenue),
            2);

        var selectedOrders = confirmedOrders
            .Where(o => MatchesSalesPeriod(GetConfirmedSaleTimestamp(o).ToOffset(brazilOffset).Date, normalizedPeriod, todayDate, start7Days, start30Days))
            .ToList();

        var selectedRevenue = Math.Round(selectedOrders.Sum(GetOrderRevenue), 2);
        var totalUnitsSold = selectedOrders.SelectMany(o => o.Items).Sum(i => i.Quantity);
        var averageTicket = selectedOrders.Count > 0
            ? Math.Round(selectedRevenue / selectedOrders.Count, 2)
            : 0m;

        var topProducts = BuildTopSellingProducts(selectedOrders);
        var dailySeries = BuildDailySalesSeries(selectedOrders, normalizedPeriod, todayDate, brazilOffset);

        return new SalesReportDto(
            IsUnlocked: true,
            EffectivePlanTier: settings.GetEffectivePlanTier(),
            LockMessage: string.Empty,
            PeriodKey: normalizedPeriod,
            TotalConfirmedRevenue: totalConfirmedRevenue,
            TodayConfirmedRevenue: todayConfirmedRevenue,
            Last7DaysConfirmedRevenue: last7DaysRevenue,
            Last30DaysConfirmedRevenue: last30DaysRevenue,
            SelectedPeriodConfirmedRevenue: selectedRevenue,
            AwaitingPixRevenue: Math.Round(awaitingOrders.Sum(GetOrderRevenue), 2),
            CancelledRevenue: Math.Round(cancelledOrders.Sum(GetOrderRevenue), 2),
            ConfirmedOrdersCount: confirmedOrders.Count,
            SelectedPeriodOrdersCount: selectedOrders.Count,
            AwaitingPixOrdersCount: awaitingOrders.Count,
            CancelledOrdersCount: cancelledOrders.Count,
            TotalUnitsSold: totalUnitsSold,
            AverageTicket: averageTicket,
            TopProducts: topProducts,
            DailySeries: dailySeries,
            RecentConfirmedSales: selectedOrders.Take(50).Select(MapToDto).ToList());
    }

    public async Task<StoreOrderDto> RegisterManualSaleAsync(
        Guid employeeId,
        RegisterManualSaleRequest request,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        if (!settings.CanViewSalesAnalytics())
        {
            throw new DomainException(
                "O registro de vendas diretas e o painel de quanto vendeu exigem o plano Meia Versão (Intermediário) ou Premium.");
        }

        if (employeeId == Guid.Empty)
            throw new DomainException("Usuário inválido para registrar a venda.");

        var product = await _repository.GetProductBySlugOrIdAsync(request.ProductId.ToString(), ct)
            ?? throw new DomainException("Produto selecionado não encontrado.");

        var variant = product.Variants.FirstOrDefault(v => v.Id == request.ProductVariantId)
            ?? throw new DomainException("Variante (cor/tamanho) selecionada não encontrada.");

        var existingOrders = await _repository.GetOrdersAsync(ct);
        var orderNumber = $"VND-{(existingOrders.Count + 1):0000}";

        var order = StoreOrder.CreateManualPaidSale(
            Guid.NewGuid(),
            orderNumber,
            product,
            variant,
            request.Quantity,
            request.CustomerName,
            request.CustomerWhatsapp,
            request.PaymentMethod,
            request.AgreedShippingCost,
            request.SellerNotes);

        await DeductStockForOrderItemsAsync(
            order,
            employeeId,
            $"Venda #{order.OrderNumber} ({order.PaymentMethod}) — {order.CustomerName}",
            ct);

        order.StockDeducted = true;
        await _repository.SaveOrderAsync(order, ct);

        settings.RecordActivity();
        await _repository.UpdateStoreSettingsAsync(settings, ct);

        return MapToDto(order);
    }

    private async Task DeductStockForOrderItemsAsync(
        StoreOrder order,
        Guid employeeId,
        string reason,
        CancellationToken ct)
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
                reason);

            await _repository.RecordStockMovementAsync(variant, movement, ct);
            await _repository.UpdateProductAsync(product, ct);
        }
    }

    private static DateTimeOffset GetConfirmedSaleTimestamp(StoreOrder order) =>
        order.PixConfirmedAt ?? order.DeliveredAt ?? order.CreatedAt;

    private static string NormalizeSalesPeriodKey(string? periodKey) =>
        (periodKey ?? "30d").Trim().ToLowerInvariant() switch
        {
            "today" or "hoje" => "today",
            "7d" => "7d",
            "all" or "tudo" => "all",
            _ => "30d"
        };

    private static bool MatchesSalesPeriod(
        DateTime localDate,
        string normalizedPeriod,
        DateTime todayDate,
        DateTime start7Days,
        DateTime start30Days) =>
        normalizedPeriod switch
        {
            "today" => localDate == todayDate,
            "7d" => localDate >= start7Days,
            "30d" => localDate >= start30Days,
            _ => true
        };

    private static IReadOnlyList<TopSellingProductDto> BuildTopSellingProducts(IReadOnlyList<StoreOrder> selectedOrders)
    {
        var allItems = selectedOrders.SelectMany(o => o.Items).ToList();
        var totalMerchRevenue = allItems.Sum(i => i.Subtotal);

        return allItems
            .GroupBy(i => i.ProductId)
            .Select(g =>
            {
                var revenue = Math.Round(g.Sum(i => i.Subtotal), 2);
                var share = totalMerchRevenue > 0m
                    ? Math.Round(revenue / totalMerchRevenue * 100m, 1)
                    : 0m;
                return new TopSellingProductDto(
                    g.Key,
                    g.First().ProductName,
                    g.Sum(i => i.Quantity),
                    revenue,
                    share);
            })
            .OrderByDescending(p => p.ConfirmedRevenue)
            .ThenByDescending(p => p.UnitsSold)
            .Take(10)
            .ToList();
    }

    private static IReadOnlyList<DailySalesPointDto> BuildDailySalesSeries(
        IReadOnlyList<StoreOrder> selectedOrders,
        string normalizedPeriod,
        DateTime todayDate,
        TimeSpan brazilOffset)
    {
        var grouped = selectedOrders
            .GroupBy(o => GetConfirmedSaleTimestamp(o).ToOffset(brazilOffset).Date)
            .ToDictionary(
                g => g.Key,
                g => (
                    Revenue: Math.Round(g.Sum(o => o.TotalPixAmount > 0m ? o.TotalPixAmount : o.ItemsTotal), 2),
                    OrdersCount: g.Count(),
                    UnitsSold: g.SelectMany(o => o.Items).Sum(i => i.Quantity)));

        if (normalizedPeriod == "all")
        {
            var dates = grouped.Keys.OrderBy(d => d).ToList();
            if (dates.Count == 0)
                dates.Add(todayDate);

            return dates.Select(d =>
            {
                var stats = grouped.TryGetValue(d, out var val) ? val : (0m, 0, 0);
                return new DailySalesPointDto(d.ToString("dd/MM"), d.ToString("yyyy-MM-dd"), stats.Item1, stats.Item2, stats.Item3);
            }).ToList();
        }

        var dayCount = normalizedPeriod switch
        {
            "today" => 1,
            "7d" => 7,
            _ => 30
        };

        var result = new List<DailySalesPointDto>(dayCount);
        for (var i = dayCount - 1; i >= 0; i--)
        {
            var date = todayDate.AddDays(-i);
            var stats = grouped.TryGetValue(date, out var val) ? val : (0m, 0, 0);
            result.Add(new DailySalesPointDto(
                date.ToString("dd/MM"),
                date.ToString("yyyy-MM-dd"),
                stats.Item1,
                stats.Item2,
                stats.Item3));
        }

        return result;
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
