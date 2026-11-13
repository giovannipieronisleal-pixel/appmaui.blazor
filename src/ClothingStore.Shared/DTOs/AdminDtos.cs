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
    public Guid CategoryId { get; set; }

    /// <summary>
    /// Nome de nova categoria (opcional). Se preenchido, cria ou seleciona a categoria automaticamente.
    /// </summary>
    public string? CategoryName { get; set; }

    [Required(ErrorMessage = "O nome do produto é obrigatório.")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Range(0.01, 999999, ErrorMessage = "O preço deve ser maior que zero.")]
    public decimal BasePrice { get; set; }

    public bool IsVisibleOnSite { get; set; } = true;

    /// <summary>
    /// Campos opcionais para já cadastrar a primeira variante (Cor/Tamanho/Estoque) junto com o produto.
    /// </summary>
    public string? InitialColor { get; set; }
    public string? InitialSize { get; set; }
    public int InitialStockQuantity { get; set; }
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

    /// <summary>
    /// Quando verdadeiro na edição de variante, ajusta também o saldo em estoque para <see cref="InitialStockQuantity"/>.
    /// </summary>
    public bool UpdateStockQuantity { get; set; }

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

    /// <summary>
    /// Login de acesso do funcionário (ex.: "joao", "vendedor1") ou e-mail opcional.
    /// Se não informado, é gerado automaticamente a partir do nome.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// E-mail (Gmail do funcionário para que ele defina/altere a própria senha).
    /// </summary>
    public string? Email { get; set; } = string.Empty;

    /// <summary>
    /// Campo legado/opcional apenas para compatibilidade interna de testes;
    /// administradores não criam nem visualizam senhas dos funcionários na interface.
    /// </summary>
    public string? InitialPassword { get; set; }

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

    public string? StoreSlug { get; set; }

    [Required(ErrorMessage = "O número de WhatsApp é obrigatório.")]
    public string WhatsappNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "A descrição da loja é obrigatória.")]
    public string StoreDescription { get; set; } = string.Empty;

    public string? LogoPublicUrl { get; set; }

    [Range(0, 1000)]
    public int DefaultLowStockThreshold { get; set; } = 3;

    /// <summary>
    /// URL pública 24h online do catálogo no Google Cloud Run (ou domínio próprio).
    /// Ex.: https://minhaloja-catalog-xxxx.a.run.app
    /// </summary>
    public string? OnlineBaseUrl { get; set; }

    /// <summary>
    /// Define se o catálogo online no Google Cloud é atualizado automaticamente ao salvar itens.
    /// </summary>
    public bool AutoSyncOnlineOnSave { get; set; } = true;

    public DateTimeOffset? LastOnlineSyncAt { get; set; }

    public bool IsSiteActive { get; set; } = true;
    public string? DeactivationReason { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public string? PublishedStoreName { get; set; }

    public string? PlanTier { get; set; }
    public decimal? BasicPlanMonthlyPrice { get; set; }
    public decimal? IntermediaryPlanMonthlyPrice { get; set; }
    public decimal? PremiumPlanMonthlyPrice { get; set; }
    public string? PicpayPixKey { get; set; }
    public StoreDesignDto? Design { get; set; }

    // Vendas por Pix no app (exclusivo Intermediário e Premium, ativação explícita), distância máxima e CEP Correios
    public bool? DirectPixSalesEnabled { get; set; }
    public string? StorePixKey { get; set; }
    public string? StorePixReceiverName { get; set; }
    public string? OriginCep { get; set; }
    public string? OriginCity { get; set; }
    public string? OriginState { get; set; }
    public string? DeliveryScopeMode { get; set; }
    public int? MaxDeliveryDistanceKm { get; set; }
    public int? DefaultPackageWeightGrams { get; set; }
}

public sealed record AdminStoreSettingsDto(
    Guid Id,
    string StoreName,
    string StoreSlug,
    string WhatsappNumber,
    string StoreDescription,
    string? LogoStoragePath,
    string? LogoPublicUrl,
    int DefaultLowStockThreshold,
    DateTimeOffset UpdatedAt,
    string? OnlineBaseUrl = null,
    bool AutoSyncOnlineOnSave = true,
    DateTimeOffset? LastOnlineSyncAt = null,
    bool IsSiteActive = true,
    string? DeactivationReason = null,
    DateTimeOffset? LastActivityAt = null,
    string? PublishedStoreName = null,
    string PlanTier = "Basico",
    decimal BasicPlanMonthlyPrice = 49.90m,
    decimal PremiumPlanMonthlyPrice = 129.90m,
    string PicpayPixKey = "picpay.me/sua-conta",
    StoreDesignDto? Design = null,
    DateTimeOffset? PlanActivatedAt = null,
    DateTimeOffset? PlanExpiresAt = null,
    string? ActiveGiftCardCode = null,
    decimal IntermediaryPlanMonthlyPrice = 89.90m,
    bool DirectPixSalesEnabled = false,
    bool DirectPixSalesReady = false,
    string? StorePixKey = null,
    string? StorePixReceiverName = null,
    string? OriginCep = null,
    string? OriginCity = null,
    string? OriginState = null,
    string DeliveryScopeMode = "TodoBrasil",
    int MaxDeliveryDistanceKm = 0,
    int DefaultPackageWeightGrams = 450);

public sealed class CloudCatalogSyncPayload
{
    public UpdateStoreSettingsRequest Settings { get; set; } = new();
    public List<SaveCategoryRequest> Categories { get; set; } = [];
    public List<SaveProductRequest> Products { get; set; } = [];
    public string? RawDatabaseJson { get; set; }
}

public sealed class CheckDomainAvailabilityRequest
{
    [Required(ErrorMessage = "Informe o domínio desejado.")]
    public string Domain { get; set; } = string.Empty;
}

public sealed record CheckDomainAvailabilityResponse(
    string Domain,
    bool IsAvailable,
    string Status,
    string Message,
    string NormalizedUrl,
    IReadOnlyList<string> Suggestions
);

public sealed class RedeemActivationCodeRequest
{
    [Required(ErrorMessage = "Informe o código do Gift Card.")]
    public string Code { get; set; } = string.Empty;
}

public sealed record RedeemActivationCodeResponse(
    string Code,
    string ActivatedPlanTier,
    int? DurationDays,
    DateTimeOffset? PlanExpiresAt,
    string Message,
    AdminStoreSettingsDto UpdatedSettings);

public sealed record FreeDomainCandidateDto(
    string Domain,
    string NormalizedUrl,
    string ProviderName,
    string RegistrationMode,
    string? SetupUrl,
    bool IsAvailable,
    bool VerifiedLiveDns,
    decimal ExtraDomainCostPerYear,
    decimal MonthlyPlanDiscount,
    string OfferHeadline,
    string StatusMessage);

public sealed record FreeDomainSearchResponse(
    string QuerySlug,
    FreeDomainCandidateDto? FeaturedOffer,
    IReadOnlyList<FreeDomainCandidateDto> Candidates);


