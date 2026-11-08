namespace ClothingStore.Domain.Enums;

/// <summary>
/// Tipos de movimentação de estoque auditáveis no sistema.
/// </summary>
public enum StockMovementType
{
    /// <summary>
    /// Entrada de mercadorias (reposição, compra de fornecedor, devolução).
    /// </summary>
    In = 1,

    /// <summary>
    /// Saída de mercadorias (venda, baixa operacional).
    /// </summary>
    Out = 2,

    /// <summary>
    /// Ajuste direto de inventário para igualar ao estoque físico.
    /// </summary>
    Adjustment = 3
}

