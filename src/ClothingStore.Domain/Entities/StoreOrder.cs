using System.Text.RegularExpressions;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Pedido realizado diretamente pelo aplicativo/catálogo usando exclusivamente Pix,
/// com endereço de entrega do comprador, validação de raio de distância do vendedor e estimativa dos Correios.
/// </summary>
public sealed class StoreOrder
{
    public const string PaymentMethodPixOnly = "Pix";
    public const string PaymentMethodCash = "Dinheiro";
    public const string PaymentMethodWhatsapp = "WhatsApp";
    public const string PaymentMethodCardOrLink = "Cartão / Link";

    public const string StatusAwaitingPix = "AguardandoPix";
    public const string StatusPaidPreparing = "PagoSeparandoEnvio";
    public const string StatusShipped = "Enviado";
    public const string StatusDelivered = "Entregue";
    public const string StatusCancelled = "Cancelado";
    public const string FulfillmentPending = "A definir pelo vendedor";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = PaymentMethodPixOnly;
    public string Status { get; set; } = StatusAwaitingPix;

    public List<StoreOrderItem> Items { get; set; } = [];
    public decimal ItemsTotal { get; set; }
    public decimal EstimatedCorreiosPac { get; set; }
    public decimal EstimatedCorreiosSedex { get; set; }
    public decimal? AgreedShippingCost { get; set; }
    public decimal TotalPixAmount { get; set; }

    public string CustomerName { get; set; } = string.Empty;
    public string CustomerWhatsapp { get; set; } = string.Empty;
    public string DeliveryCep { get; set; } = string.Empty;
    public string DeliveryStreet { get; set; } = string.Empty;
    public string DeliveryNumber { get; set; } = string.Empty;
    public string? DeliveryComplement { get; set; }
    public string DeliveryNeighborhood { get; set; } = string.Empty;
    public string DeliveryCity { get; set; } = string.Empty;
    public string DeliveryState { get; set; } = string.Empty;
    public string? CustomerNotes { get; set; }

    public double DistanceKmFromSeller { get; set; }
    public string ShippingZoneLabel { get; set; } = string.Empty;
    public int EstimatedPacDaysMin { get; set; }
    public int EstimatedPacDaysMax { get; set; }
    public int EstimatedSedexDaysMin { get; set; }
    public int EstimatedSedexDaysMax { get; set; }

    public string PixKey { get; set; } = string.Empty;
    public string PixBrCode { get; set; } = string.Empty;

    public string FulfillmentMethod { get; set; } = FulfillmentPending;
    public string? TrackingCode { get; set; }
    public string? SellerNotes { get; set; }
    public bool StockDeducted { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PixConfirmedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public StoreOrder()
    {
    }

    public static StoreOrder CreatePixOrder(
        Guid id,
        string orderNumber,
        Product product,
        ProductVariant variant,
        int quantity,
        string customerName,
        string customerWhatsapp,
        string deliveryCep,
        string deliveryStreet,
        string deliveryNumber,
        string? deliveryComplement,
        string deliveryNeighborhood,
        string deliveryCity,
        string deliveryState,
        string? customerNotes,
        double distanceKmFromSeller,
        string shippingZoneLabel,
        decimal estimatedCorreiosPac,
        int pacDaysMin,
        int pacDaysMax,
        decimal estimatedCorreiosSedex,
        int sedexDaysMin,
        int sedexDaysMax,
        string pixKey,
        string pixBrCode)
    {
        if (quantity <= 0)
            throw new DomainException("A quantidade comprada deve ser maior que zero.");

        if (!variant.IsActive || variant.StockQuantity < quantity)
        {
            throw new DomainException(
                $"Estoque insuficiente para {product.Name} ({variant.Color}/{variant.Size}). Disponível: {variant.StockQuantity}.");
        }

        if (string.IsNullOrWhiteSpace(customerName) || customerName.Trim().Length < 2)
            throw new DomainException("Informe seu nome completo para o envio do pedido.");

        var cleanWhatsapp = StoreSettings.SanitizeWhatsappNumber(customerWhatsapp);
        var cepDigits = Regex.Replace(deliveryCep ?? string.Empty, @"\D", "");
        if (cepDigits.Length != 8)
            throw new DomainException("Informe um CEP de entrega válido com 8 dígitos.");

        if (string.IsNullOrWhiteSpace(deliveryStreet))
            throw new DomainException("Informe a rua/logradouro do endereço de entrega.");

        if (string.IsNullOrWhiteSpace(deliveryNumber))
            throw new DomainException("Informe o número do endereço de entrega (ou S/N).");

        if (string.IsNullOrWhiteSpace(deliveryCity) || string.IsNullOrWhiteSpace(deliveryState) || deliveryState.Trim().Length != 2)
            throw new DomainException("Informe a cidade e o estado (UF com 2 letras) do endereço de entrega.");

        if (string.IsNullOrWhiteSpace(pixKey) || string.IsNullOrWhiteSpace(pixBrCode))
            throw new DomainException("Os dados Pix do pedido são obrigatórios.");

        var now = DateTimeOffset.UtcNow;
        var unitPrice = Math.Round(product.BasePrice, 2);
        var itemsTotal = Math.Round(unitPrice * quantity, 2);
        var formattedCep = $"{cepDigits[..5]}-{cepDigits[5..]}";

        var item = new StoreOrderItem
        {
            ProductId = product.Id,
            ProductVariantId = variant.Id,
            ProductName = product.Name,
            Sku = variant.Sku,
            Color = variant.Color,
            Size = variant.Size,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Subtotal = itemsTotal
        };

        return new StoreOrder
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id,
            OrderNumber = orderNumber,
            PaymentMethod = PaymentMethodPixOnly,
            Status = StatusAwaitingPix,
            Items = [item],
            ItemsTotal = itemsTotal,
            EstimatedCorreiosPac = Math.Round(estimatedCorreiosPac, 2),
            EstimatedCorreiosSedex = Math.Round(estimatedCorreiosSedex, 2),
            TotalPixAmount = itemsTotal,
            CustomerName = customerName.Trim(),
            CustomerWhatsapp = cleanWhatsapp,
            DeliveryCep = formattedCep,
            DeliveryStreet = deliveryStreet.Trim(),
            DeliveryNumber = deliveryNumber.Trim(),
            DeliveryComplement = string.IsNullOrWhiteSpace(deliveryComplement) ? null : deliveryComplement.Trim(),
            DeliveryNeighborhood = string.IsNullOrWhiteSpace(deliveryNeighborhood) ? "Centro" : deliveryNeighborhood.Trim(),
            DeliveryCity = deliveryCity.Trim(),
            DeliveryState = deliveryState.Trim().ToUpperInvariant(),
            CustomerNotes = string.IsNullOrWhiteSpace(customerNotes) ? null : customerNotes.Trim(),
            DistanceKmFromSeller = Math.Round(distanceKmFromSeller, 1),
            ShippingZoneLabel = shippingZoneLabel,
            EstimatedPacDaysMin = pacDaysMin,
            EstimatedPacDaysMax = pacDaysMax,
            EstimatedSedexDaysMin = sedexDaysMin,
            EstimatedSedexDaysMax = sedexDaysMax,
            PixKey = pixKey.Trim(),
            PixBrCode = pixBrCode.Trim(),
            FulfillmentMethod = FulfillmentPending,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static StoreOrder CreateManualPaidSale(
        Guid id,
        string orderNumber,
        Product product,
        ProductVariant variant,
        int quantity,
        string customerName,
        string? customerWhatsapp = null,
        string? paymentMethod = null,
        decimal? agreedShippingCost = null,
        string? sellerNotes = null,
        DateTimeOffset? soldAt = null)
    {
        if (quantity <= 0)
            throw new DomainException("A quantidade vendida deve ser maior que zero.");

        if (!variant.IsActive || variant.StockQuantity < quantity)
        {
            throw new DomainException(
                $"Estoque insuficiente para {product.Name} ({variant.Color}/{variant.Size}). Disponível: {variant.StockQuantity}.");
        }

        if (agreedShippingCost.HasValue && agreedShippingCost.Value < 0)
            throw new DomainException("O valor de frete não pode ser negativo.");

        var normalizedMethod = NormalizeManualPaymentMethod(paymentMethod);
        var cleanCustomer = string.IsNullOrWhiteSpace(customerName)
            ? "Cliente Balcão / WhatsApp"
            : customerName.Trim();

        var cleanWhatsapp = string.IsNullOrWhiteSpace(customerWhatsapp)
            ? string.Empty
            : StoreSettings.SanitizeWhatsappNumber(customerWhatsapp);

        var timestamp = soldAt ?? DateTimeOffset.UtcNow;
        var unitPrice = Math.Round(product.BasePrice, 2);
        var itemsTotal = Math.Round(unitPrice * quantity, 2);
        var roundedShipping = agreedShippingCost.HasValue && agreedShippingCost.Value > 0
            ? Math.Round(agreedShippingCost.Value, 2)
            : (decimal?)null;
        var totalAmount = Math.Round(itemsTotal + (roundedShipping ?? 0m), 2);

        var item = new StoreOrderItem
        {
            ProductId = product.Id,
            ProductVariantId = variant.Id,
            ProductName = product.Name,
            Sku = variant.Sku,
            Color = variant.Color,
            Size = variant.Size,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Subtotal = itemsTotal
        };

        return new StoreOrder
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id,
            OrderNumber = orderNumber,
            PaymentMethod = normalizedMethod,
            Status = StatusDelivered,
            Items = [item],
            ItemsTotal = itemsTotal,
            AgreedShippingCost = roundedShipping,
            TotalPixAmount = totalAmount,
            CustomerName = cleanCustomer,
            CustomerWhatsapp = cleanWhatsapp,
            ShippingZoneLabel = "Venda Registrada no Painel",
            FulfillmentMethod = "Retirada / Venda Direta",
            SellerNotes = string.IsNullOrWhiteSpace(sellerNotes) ? null : sellerNotes.Trim(),
            CreatedAt = timestamp,
            PixConfirmedAt = timestamp,
            ShippedAt = timestamp,
            DeliveredAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    public static string NormalizeManualPaymentMethod(string? rawMethod)
    {
        if (string.IsNullOrWhiteSpace(rawMethod))
            return PaymentMethodWhatsapp;

        var trimmed = rawMethod.Trim();
        if (trimmed.Equals(PaymentMethodPixOnly, StringComparison.OrdinalIgnoreCase))
            return PaymentMethodPixOnly;
        if (trimmed.Equals(PaymentMethodCash, StringComparison.OrdinalIgnoreCase))
            return PaymentMethodCash;
        if (trimmed.Equals(PaymentMethodWhatsapp, StringComparison.OrdinalIgnoreCase))
            return PaymentMethodWhatsapp;
        if (trimmed.Equals(PaymentMethodCardOrLink, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("cart", StringComparison.OrdinalIgnoreCase))
            return PaymentMethodCardOrLink;

        return trimmed.Length > 40 ? trimmed[..40] : trimmed;
    }

    public bool IsConfirmedSale() =>
        Status is StatusPaidPreparing or StatusShipped or StatusDelivered;

    public string FormatDeliveryAddress()
    {
        var comp = string.IsNullOrWhiteSpace(DeliveryComplement) ? string.Empty : $", {DeliveryComplement}";
        return $"{DeliveryStreet}, {DeliveryNumber}{comp} — {DeliveryNeighborhood}, {DeliveryCity}/{DeliveryState}, CEP {DeliveryCep}";
    }

    public void ConfirmPixPayment()
    {
        if (Status == StatusCancelled)
            throw new DomainException("Não é possível confirmar Pix de um pedido cancelado.");

        var now = DateTimeOffset.UtcNow;
        if (!PixConfirmedAt.HasValue)
            PixConfirmedAt = now;

        if (Status == StatusAwaitingPix)
            Status = StatusPaidPreparing;

        UpdatedAt = now;
    }

    public void UpdateFulfillment(
        string fulfillmentMethod,
        string? trackingCode,
        decimal? agreedShippingCost,
        string? sellerNotes,
        bool markAsShipped,
        bool markAsDelivered)
    {
        if (Status == StatusCancelled)
            throw new DomainException("Não é possível atualizar envio de um pedido cancelado.");

        if (agreedShippingCost.HasValue && agreedShippingCost.Value < 0)
            throw new DomainException("O valor combinado de frete não pode ser negativo.");

        FulfillmentMethod = string.IsNullOrWhiteSpace(fulfillmentMethod)
            ? FulfillmentPending
            : fulfillmentMethod.Trim();

        TrackingCode = string.IsNullOrWhiteSpace(trackingCode) ? null : trackingCode.Trim().ToUpperInvariant();
        AgreedShippingCost = agreedShippingCost.HasValue ? Math.Round(agreedShippingCost.Value, 2) : null;
        SellerNotes = string.IsNullOrWhiteSpace(sellerNotes) ? null : sellerNotes.Trim();

        var now = DateTimeOffset.UtcNow;
        if (markAsDelivered)
        {
            if (!ShippedAt.HasValue)
                ShippedAt = now;
            DeliveredAt = now;
            Status = StatusDelivered;
        }
        else if (markAsShipped)
        {
            if (!ShippedAt.HasValue)
                ShippedAt = now;
            Status = StatusShipped;
        }
        else if (Status == StatusAwaitingPix && PixConfirmedAt.HasValue)
        {
            Status = StatusPaidPreparing;
        }

        UpdatedAt = now;
    }

    public void Cancel(string? reason = null)
    {
        if (Status == StatusDelivered)
            throw new DomainException("Não é possível cancelar um pedido que já foi entregue.");

        Status = StatusCancelled;
        if (!string.IsNullOrWhiteSpace(reason))
            SellerNotes = reason.Trim();

        var now = DateTimeOffset.UtcNow;
        CancelledAt ??= now;
        UpdatedAt = now;
    }
}

public sealed class StoreOrderItem
{
    public Guid ProductId { get; set; }
    public Guid ProductVariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }
}
