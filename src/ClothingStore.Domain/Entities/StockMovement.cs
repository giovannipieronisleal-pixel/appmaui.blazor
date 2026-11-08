using ClothingStore.Domain.Enums;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Registro histórico imutável de uma movimentação de estoque realizada por um funcionário.
/// </summary>
public sealed class StockMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductVariantId { get; set; }
    public Guid EmployeeId { get; set; }
    public StockMovementType MovementType { get; set; }
    public int QuantityChanged { get; set; }
    public int PreviousQuantity { get; set; }
    public int NewQuantity { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public StockMovement()
    {
    }

    public StockMovement(
        Guid productVariantId,
        Guid employeeId,
        StockMovementType movementType,
        int quantityChanged,
        int previousQuantity,
        int newQuantity,
        string? reason = null)
    {
        ProductVariantId = productVariantId;
        EmployeeId = employeeId;
        MovementType = movementType;
        QuantityChanged = quantityChanged;
        PreviousQuantity = previousQuantity;
        NewQuantity = newQuantity;
        Reason = string.IsNullOrWhiteSpace(reason) ? GetDefaultReason(movementType) : reason.Trim();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private static string GetDefaultReason(StockMovementType type) => type switch
    {
        StockMovementType.In => "Entrada de mercadoria",
        StockMovementType.Out => "Saída de mercadoria",
        StockMovementType.Adjustment => "Ajuste de inventário",
        _ => "Movimentação de estoque"
    };
}

