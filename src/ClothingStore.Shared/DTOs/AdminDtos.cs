using System.ComponentModel.DataAnnotations;

namespace ClothingStore.Shared.DTOs;

public sealed record DashboardSummaryDto(
    int TotalRegisteredProducts,
    int TotalVisibleProducts,
    int TotalUnitsInStock,
    int LowStockVariantsCount,
    int OutOfStockVariantsCount,
    IReadOnlyList<StockAlertItemDto> StockAlerts,
    IReadOnlyList<StockMovementDto> RecentMovements);

public sealed record StockAlertItemDto(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string Sku,
    string Color,
    string ColorHex,
    string Size,
    int StockQuantity,
    int LowStockThreshold,
    bool IsOutOfStock);

public sealed record StockMovementDto(
    Guid Id,
    Guid ProductVariantId,
    Guid ProductId,
    string ProductName,
    string VariantSku,
    string Color,
    string Size,
    Guid EmployeeId,
    string EmployeeName,
    string MovementType,
    int QuantityChanged,
    int PreviousQuantity,
    int NewQuantity,
    string? Reason,
    DateTimeOffset CreatedAt);

public sealed class SaveCategoryRequest
{
    [Required(ErrorMessage = "O nome da categoria é obrigatório.")]
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SaveProductRequest
{
    [Required(ErrorMessage = "A categoria é obrigatória.")]
    public Guid CategoryId { get; set; }

    [Required(ErrorMessage = "O nome do produto é obrigatório.")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Range(0.01, 999999, ErrorMessage = "O preço deve ser maior que zero.")]
    public decimal BasePrice { get; set; }

    public bool IsVisibleOnSite { get; set; } = true;
}

public sealed class SaveProductVariantRequest
{
    [Required(ErrorMessage = "A cor é obrigatória.")]
    public string Color { get; set; } = string.Empty;

    public string? ColorHex { get; set; }

    [Required(ErrorMessage = "O tamanho é obrigatório.")]
    public string Size { get; set; } = string.Empty;

    public string? CustomSku { get; set; }

    [Range(0, 100000)]
    public int InitialStockQuantity { get; set; }

    [Range(0, 10000)]
    public int LowStockThreshold { get; set; } = 3;

    public bool IsActive { get; set; } = true;
}

public sealed class AddProductImageByUrlRequest
{
    [Required(ErrorMessage = "A URL da imagem é obrigatória.")]
    public string ImageUrl { get; set; } = string.Empty;
    public string? AltText { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class CreateStockMovementRequest
{
    [Required]
    public Guid ProductVariantId { get; set; }

    /// <summary>
    /// "IN" (Entrada), "OUT" (Saída) ou "ADJUSTMENT" (Ajuste de saldo).
    /// </summary>
    [Required]
    public string MovementType { get; set; } = "IN";

    [Range(0, 1000000, ErrorMessage = "Informe uma quantidade válida.")]
    public int Quantity { get; set; }

    public string? Reason { get; set; }
}

public sealed class CreateEmployeeRequest
{
    [Required(ErrorMessage = "O nome completo é obrigatório.")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "O e-mail é obrigatório.")]
    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "A senha inicial é obrigatória.")]
    [MinLength(6, ErrorMessage = "A senha deve ter no mínimo 6 caracteres.")]
    public string InitialPassword { get; set; } = string.Empty;

    public string RoleName { get; set; } = "Employee";
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public bool CanManageProducts { get; set; }
    public bool CanManageStock { get; set; } = true;
}

public sealed class UpdateEmployeeRequest
{
    [Required(ErrorMessage = "O nome completo é obrigatório.")]
    public string FullName { get; set; } = string.Empty;

    public string RoleName { get; set; } = "Employee";
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public bool CanManageProducts { get; set; }
    public bool CanManageStock { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public sealed class UpdateStoreSettingsRequest
{
    [Required(ErrorMessage = "O nome da loja é obrigatório.")]
    public string StoreName { get; set; } = string.Empty;

    [Required(ErrorMessage = "O número de WhatsApp é obrigatório.")]
    public string WhatsappNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "A descrição da loja é obrigatória.")]
    public string StoreDescription { get; set; } = string.Empty;

    public string? LogoPublicUrl { get; set; }

    [Range(0, 1000)]
    public int DefaultLowStockThreshold { get; set; } = 3;
}

public sealed record AdminStoreSettingsDto(
    Guid Id,
    string StoreName,
    string WhatsappNumber,
    string StoreDescription,
    string? LogoStoragePath,
    string? LogoPublicUrl,
    int DefaultLowStockThreshold,
    DateTimeOffset UpdatedAt);
