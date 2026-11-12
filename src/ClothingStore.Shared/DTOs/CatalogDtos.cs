namespace ClothingStore.Shared.DTOs;

/// <summary>
/// Configurações públicas da loja consumidas dinamicamente pelo site Blazor Web App.
/// </summary>
public sealed record PublicStoreSettingsDto(
    string StoreName,
    string StoreSlug,
    string WhatsappNumber,
    string StoreDescription,
    string? LogoPublicUrl,
    DateTimeOffset UpdatedAt,
    string? OnlineBaseUrl = null,
    bool AutoSyncOnlineOnSave = true,
    DateTimeOffset? LastOnlineSyncAt = null,
    bool IsSiteActive = true,
    string? DeactivationReason = null,
    DateTimeOffset? LastActivityAt = null,
    string PlanTier = "Basico",
    StoreDesignDto? Design = null,
    DateTimeOffset? PlanActivatedAt = null,
    DateTimeOffset? PlanExpiresAt = null,
    string? ActiveGiftCardCode = null);

public sealed record CategoryDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    int DisplayOrder,
    bool IsActive,
    int ProductCount);

public sealed record CatalogFiltersDto(
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<ColorOptionDto> Colors,
    IReadOnlyList<string> Sizes);

public sealed record ColorOptionDto(
    string Color,
    string ColorHex);

public sealed record ProductImageDto(
    Guid Id,
    Guid ProductId,
    string PublicUrl,
    string? AltText,
    int DisplayOrder,
    bool IsPrimary);

public sealed record ProductVariantDto(
    Guid Id,
    Guid ProductId,
    string Sku,
    string Color,
    string ColorHex,
    string Size,
    int StockQuantity,
    int LowStockThreshold,
    bool IsActive,
    bool IsLowStock,
    bool IsOutOfStock);

public sealed record CatalogProductCardDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    decimal BasePrice,
    Guid CategoryId,
    string CategoryName,
    string CategorySlug,
    string? PrimaryImageUrl,
    int TotalStockQuantity,
    bool HasAvailableStock,
    IReadOnlyList<ColorOptionDto> AvailableColors,
    IReadOnlyList<string> AvailableSizes);

public sealed record ProductDetailDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string CategorySlug,
    string Name,
    string Slug,
    string? Description,
    decimal BasePrice,
    bool IsVisibleOnSite,
    bool IsArchived,
    int TotalStockQuantity,
    bool HasAvailableStock,
    IReadOnlyList<ProductImageDto> Images,
    IReadOnlyList<ProductVariantDto> Variants,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CatalogQueryFilter(
    string? Search = null,
    Guid? CategoryId = null,
    string? CategorySlug = null,
    string? Color = null,
    string? Size = null,
    bool OnlyInStock = false);

