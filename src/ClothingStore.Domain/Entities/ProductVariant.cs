using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Variante individual de um produto (combinação única de Cor e Tamanho com estoque próprio).
/// </summary>
public sealed class ProductVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string? ColorHex { get; set; }
    public string Size { get; set; } = string.Empty;
    public int StockQuantity { get; set; }
    public int LowStockThreshold { get; set; } = 3;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ProductVariant()
    {
    }

    public ProductVariant(
        Guid productId,
        string sku,
        string color,
        string size,
        string? colorHex = null,
        int lowStockThreshold = 3)
    {
        if (productId == Guid.Empty)
            throw new DomainException("A variante deve pertencer a um produto válido.");

        ProductId = productId;
        UpdateDetails(sku, color, size, colorHex, lowStockThreshold, true);
    }

    public bool IsOutOfStock => StockQuantity <= 0;

    public bool IsLowStock => StockQuantity > 0 && StockQuantity <= LowStockThreshold;

    public void UpdateDetails(
        string sku,
        string color,
        string size,
        string? colorHex,
        int lowStockThreshold,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new DomainException("O código SKU da variante é obrigatório.");

        if (string.IsNullOrWhiteSpace(color))
            throw new DomainException("A cor da variante é obrigatória.");

        if (string.IsNullOrWhiteSpace(size))
            throw new DomainException("O tamanho da variante é obrigatório.");

        if (lowStockThreshold < 0)
            throw new DomainException("O limite de alerta de estoque baixo não pode ser negativo.");

        Sku = sku.Trim().ToUpperInvariant();
        Color = color.Trim();
        Size = size.Trim().ToUpperInvariant();
        ColorHex = string.IsNullOrWhiteSpace(colorHex) ? ResolveDefaultColorHex(Color) : colorHex.Trim();
        LowStockThreshold = lowStockThreshold;
        IsActive = isActive;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Aplica uma movimentação de estoque (Entrada, Saída ou Ajuste) garantindo que o saldo nunca fique negativo
    /// e retorna o registro auditável <see cref="StockMovement"/>.
    /// </summary>
    public StockMovement ApplyStockMovement(
        Guid employeeId,
        StockMovementType movementType,
        int quantity,
        string? reason = null)
    {
        if (employeeId == Guid.Empty)
            throw new DomainException("O funcionário responsável pela movimentação é obrigatório.");

        var previousQuantity = StockQuantity;
        int newQuantity;
        int quantityChanged;

        switch (movementType)
        {
            case StockMovementType.In:
                if (quantity <= 0)
                    throw new DomainException("A quantidade de entrada deve ser maior que zero.");
                if (quantity > 1_000_000 || int.MaxValue - previousQuantity < quantity)
                    throw new DomainException("A quantidade de entrada não pode exceder 1.000.000 unidades.");
                quantityChanged = quantity;
                newQuantity = previousQuantity + quantity;
                break;

            case StockMovementType.Out:
                if (quantity <= 0)
                    throw new DomainException("A quantidade de saída deve ser maior que zero.");
                if (quantity > previousQuantity)
                    throw new DomainException($"Estoque insuficiente para a variante {Color}/{Size}. Disponível: {previousQuantity}, solicitado: {quantity}.");
                quantityChanged = quantity;
                newQuantity = previousQuantity - quantity;
                break;

            case StockMovementType.Adjustment:
                if (quantity < 0)
                    throw new DomainException("O novo saldo de estoque em um ajuste não pode ser negativo.");
                if (quantity > 1_000_000)
                    throw new DomainException("O novo saldo de estoque em um ajuste não pode exceder 1.000.000 unidades.");
                quantityChanged = quantity - previousQuantity;
                if (quantityChanged == 0)
                    throw new DomainException("O ajuste informado não altera a quantidade atual em estoque.");
                newQuantity = quantity;
                break;

            default:
                throw new DomainException("Tipo de movimentação de estoque inválido.");
        }

        StockQuantity = newQuantity;
        UpdatedAt = DateTimeOffset.UtcNow;

        return new StockMovement(
            Id,
            employeeId,
            movementType,
            quantityChanged,
            previousQuantity,
            newQuantity,
            reason);
    }

    public static string GenerateSku(string productName, string color, string size)
    {
        var prodPrefix = ExtractCode(productName, 6);
        var colorPrefix = ExtractCode(color, 3);
        var sizeCode = size.Trim().ToUpperInvariant();
        return $"{prodPrefix}-{colorPrefix}-{sizeCode}";
    }

    private static string ExtractCode(string input, int maxLen)
    {
        var slug = Category.GenerateSlug(input).Replace("-", "").ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(slug))
            return "ITEM";
        return slug.Length <= maxLen ? slug : slug[..maxLen];
    }

    public static string ResolveDefaultColorHex(string colorName)
    {
        var lower = colorName.Trim().ToLowerInvariant();
        return lower switch
        {
            "preto" or "preta" => "#18181B",
            "branco" or "branca" or "off-white" or "off white" => "#F8FAFC",
            "azul" or "azul marinho" or "marinho" => "#1E3A8A",
            "azul claro" => "#60A5FA",
            "vermelho" or "vermelha" or "vinho" or "bordo" => "#991B1B",
            "verde" or "verde militar" or "oliva" => "#3F6212",
            "bege" or "areia" or "nude" or "caqui" => "#D6C7B2",
            "cinza" or "mescla" or "chumbo" => "#71717A",
            "rosa" or "rose" => "#F472B6",
            "amarelo" or "mostarda" => "#EAB308",
            "marrom" or "caramelo" or "terracota" => "#78350F",
            _ => "#64748B"
        };
    }
}

