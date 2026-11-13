namespace ClothingStore.Shared.DTOs;

public sealed record CepAddressLookupDto(
    string Cep,
    string Street,
    string Neighborhood,
    string City,
    string State,
    double Latitude,
    double Longitude,
    string Source);

public sealed record DeliveryEligibilityDto(
    bool IsAllowed,
    double DistanceKm,
    string OriginCity,
    string OriginState,
    string DestCity,
    string DestState,
    string ReasonMessage);

public sealed record CorreiosQuoteDto(
    string OriginCep,
    string DestCep,
    double DistanceKm,
    string ZoneName,
    int WeightGrams,
    int CubicWeightGrams,
    int BillableWeightGrams,
    decimal PacPrice,
    int PacDaysMin,
    int PacDaysMax,
    decimal SedexPrice,
    int SedexDaysMin,
    int SedexDaysMax,
    string OfficialCorreiosUrl);

public sealed class ShippingQuoteRequest
{
    public string DestinationCep { get; set; } = string.Empty;
    public string? DestinationCity { get; set; }
    public string? DestinationState { get; set; }
    public int WeightGrams { get; set; } = 450;
    public int LengthCm { get; set; } = 24;
    public int WidthCm { get; set; } = 18;
    public int HeightCm { get; set; } = 8;
}

public sealed record ShippingQuoteResponse(
    bool DirectPixSalesReady,
    DeliveryEligibilityDto Eligibility,
    CorreiosQuoteDto? CorreiosQuote,
    string ScopeDescription);

public sealed class CreatePixOrderRequest
{
    public Guid ProductId { get; set; }
    public Guid ProductVariantId { get; set; }
    public int Quantity { get; set; } = 1;

    public string CustomerName { get; set; } = string.Empty;
    public string CustomerWhatsapp { get; set; } = string.Empty;

    public string Cep { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string? Complement { get; set; }
    public string Neighborhood { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? CustomerNotes { get; set; }
}

public sealed class UpdateOrderFulfillmentRequest
{
    public string FulfillmentMethod { get; set; } = string.Empty;
    public string? TrackingCode { get; set; }
    public decimal? AgreedShippingCost { get; set; }
    public string? SellerNotes { get; set; }
    public bool MarkAsShipped { get; set; }
    public bool MarkAsDelivered { get; set; }
}

public sealed record StoreOrderItemDto(
    Guid ProductId,
    Guid ProductVariantId,
    string ProductName,
    string Sku,
    string Color,
    string Size,
    int Quantity,
    decimal UnitPrice,
    decimal Subtotal);

public sealed record StoreOrderDto(
    Guid Id,
    string OrderNumber,
    string PaymentMethod,
    string Status,
    decimal ItemsTotal,
    decimal EstimatedCorreiosPac,
    decimal EstimatedCorreiosSedex,
    decimal? AgreedShippingCost,
    decimal TotalPixAmount,
    string CustomerName,
    string CustomerWhatsapp,
    string DeliveryCep,
    string DeliveryStreet,
    string DeliveryNumber,
    string? DeliveryComplement,
    string DeliveryNeighborhood,
    string DeliveryCity,
    string DeliveryState,
    string FormattedDeliveryAddress,
    string? CustomerNotes,
    double DistanceKmFromSeller,
    string ShippingZoneLabel,
    int EstimatedPacDaysMin,
    int EstimatedPacDaysMax,
    int EstimatedSedexDaysMin,
    int EstimatedSedexDaysMax,
    string PixKey,
    string PixBrCode,
    string FulfillmentMethod,
    string? TrackingCode,
    string? SellerNotes,
    bool StockDeducted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PixConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<StoreOrderItemDto> Items);

public sealed record RegisterManualSaleRequest(
    Guid ProductId,
    Guid ProductVariantId,
    int Quantity,
    string CustomerName,
    string? CustomerWhatsapp = null,
    string PaymentMethod = "WhatsApp",
    decimal? AgreedShippingCost = null,
    string? SellerNotes = null);

public sealed record DailySalesPointDto(
    string DateLabel,
    string DateIso,
    decimal ConfirmedRevenue,
    int OrdersCount,
    int UnitsSold);

public sealed record TopSellingProductDto(
    Guid ProductId,
    string ProductName,
    int UnitsSold,
    decimal ConfirmedRevenue,
    decimal SharePercent);

public sealed record SalesReportDto(
    bool IsUnlocked,
    string EffectivePlanTier,
    string LockMessage,
    string PeriodKey,
    decimal TotalConfirmedRevenue,
    decimal TodayConfirmedRevenue,
    decimal Last7DaysConfirmedRevenue,
    decimal Last30DaysConfirmedRevenue,
    decimal SelectedPeriodConfirmedRevenue,
    decimal AwaitingPixRevenue,
    decimal CancelledRevenue,
    int ConfirmedOrdersCount,
    int SelectedPeriodOrdersCount,
    int AwaitingPixOrdersCount,
    int CancelledOrdersCount,
    int TotalUnitsSold,
    decimal AverageTicket,
    IReadOnlyList<TopSellingProductDto> TopProducts,
    IReadOnlyList<DailySalesPointDto> DailySeries,
    IReadOnlyList<StoreOrderDto> RecentConfirmedSales);
