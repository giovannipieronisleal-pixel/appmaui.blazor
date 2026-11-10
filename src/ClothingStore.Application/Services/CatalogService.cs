using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Serviço público de consulta ao catálogo de roupas e configurações dinâmicas da loja.
/// </summary>
public sealed class CatalogService
{
    private readonly IClothingStoreRepository _repository;

    public CatalogService(IClothingStoreRepository repository)
    {
        _repository = repository;
    }

    public async Task<PublicStoreSettingsDto> GetPublicStoreSettingsAsync(CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var slug = string.IsNullOrWhiteSpace(settings.StoreSlug)
            ? Category.GenerateSlug(settings.StoreName)
            : settings.StoreSlug;

        return new PublicStoreSettingsDto(
            settings.StoreName,
            slug,
            settings.WhatsappNumber,
            settings.StoreDescription,
            settings.LogoPublicUrl,
            settings.UpdatedAt,
            settings.OnlineBaseUrl,
            settings.AutoSyncOnlineOnSave,
            settings.LastOnlineSyncAt);
    }

    public async Task<IReadOnlyList<CategoryDto>> GetPublicCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await _repository.GetCategoriesAsync(onlyActive: true, ct);
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        var visibleProducts = products.Where(p => p.IsVisibleOnSite).ToList();

        return categories
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.Slug,
                c.Description,
                c.DisplayOrder,
                c.IsActive,
                visibleProducts.Count(p => p.CategoryId == c.Id)))
            .ToList();
    }

    public async Task<CatalogFiltersDto> GetCatalogFiltersAsync(CancellationToken ct = default)
    {
        var categories = await GetPublicCategoriesAsync(ct);
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        var activeVariants = products
            .Where(p => p.IsVisibleOnSite)
            .SelectMany(p => p.Variants)
            .Where(v => v.IsActive)
            .ToList();

        var colors = activeVariants
            .GroupBy(v => v.Color.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ColorOptionDto(
                g.Key,
                g.First().ColorHex ?? ProductVariant.ResolveDefaultColorHex(g.Key)))
            .OrderBy(c => c.Color)
            .ToList();

        var sizeOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["PP"] = 1, ["P"] = 2, ["M"] = 3, ["G"] = 4, ["GG"] = 5, ["XG"] = 6,
            ["34"] = 10, ["36"] = 11, ["38"] = 12, ["40"] = 13, ["42"] = 14, ["44"] = 15, ["46"] = 16
        };

        var sizes = activeVariants
            .Select(v => v.Size.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => sizeOrder.TryGetValue(s, out var rank) ? rank : 100)
            .ThenBy(s => s)
            .ToList();

        return new CatalogFiltersDto(categories, colors, sizes);
    }

    public async Task<IReadOnlyList<CatalogProductCardDto>> GetCatalogProductsAsync(
        CatalogQueryFilter filter,
        CancellationToken ct = default)
    {
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        var categories = (await _repository.GetCategoriesAsync(onlyActive: true, ct)).ToDictionary(c => c.Id);

        var query = products.Where(p => p.IsVisibleOnSite && categories.ContainsKey(p.CategoryId));

        if (filter.CategoryId.HasValue && filter.CategoryId.Value != Guid.Empty)
        {
            query = query.Where(p => p.CategoryId == filter.CategoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.CategorySlug))
        {
            query = query.Where(p =>
                categories.TryGetValue(p.CategoryId, out var cat) &&
                string.Equals(cat.Slug, filter.CategorySlug.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (categories.TryGetValue(p.CategoryId, out var cat) && cat.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Color))
        {
            var colorFilter = filter.Color.Trim();
            query = query.Where(p => p.Variants.Any(v =>
                v.IsActive &&
                string.Equals(v.Color, colorFilter, StringComparison.OrdinalIgnoreCase) &&
                (!filter.OnlyInStock || v.StockQuantity > 0)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Size))
        {
            var sizeFilter = filter.Size.Trim();
            query = query.Where(p => p.Variants.Any(v =>
                v.IsActive &&
                string.Equals(v.Size, sizeFilter, StringComparison.OrdinalIgnoreCase) &&
                (!filter.OnlyInStock || v.StockQuantity > 0)));
        }

        if (filter.OnlyInStock)
        {
            query = query.Where(p => p.HasAvailableStock);
        }

        return query
            .Select(p =>
            {
                var category = categories[p.CategoryId];
                var activeVariants = p.Variants.Where(v => v.IsActive).ToList();

                var availableColors = activeVariants
                    .GroupBy(v => v.Color, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ColorOptionDto(
                        g.Key,
                        g.First().ColorHex ?? ProductVariant.ResolveDefaultColorHex(g.Key)))
                    .ToList();

                var availableSizes = activeVariants
                    .Where(v => v.StockQuantity > 0)
                    .Select(v => v.Size)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new CatalogProductCardDto(
                    p.Id,
                    p.Name,
                    p.Slug,
                    p.Description,
                    p.BasePrice,
                    p.CategoryId,
                    category.Name,
                    category.Slug,
                    p.PrimaryImageUrl,
                    p.TotalStockQuantity,
                    p.HasAvailableStock,
                    availableColors,
                    availableSizes);
            })
            .ToList();
    }

    public async Task<ProductDetailDto?> GetPublicProductDetailAsync(string slugOrId, CancellationToken ct = default)
    {
        var product = await _repository.GetProductBySlugOrIdAsync(slugOrId, ct);
        if (product is null || product.IsArchived || !product.IsVisibleOnSite)
            return null;

        var category = await _repository.GetCategoryByIdAsync(product.CategoryId, ct);
        return MapToProductDetailDto(product, category);
    }

    public static ProductDetailDto MapToProductDetailDto(Product product, Category? category)
    {
        var images = product.Images
            .OrderByDescending(i => i.IsPrimary)
            .ThenBy(i => i.DisplayOrder)
            .Select(i => new ProductImageDto(
                i.Id,
                i.ProductId,
                i.PublicUrl,
                i.AltText,
                i.DisplayOrder,
                i.IsPrimary))
            .ToList();

        var variants = product.Variants
            .Where(v => v.IsActive)
            .OrderBy(v => v.Color)
            .ThenBy(v => v.Size)
            .Select(v => new ProductVariantDto(
                v.Id,
                v.ProductId,
                v.Sku,
                v.Color,
                v.ColorHex ?? ProductVariant.ResolveDefaultColorHex(v.Color),
                v.Size,
                v.StockQuantity,
                v.LowStockThreshold,
                v.IsActive,
                v.IsLowStock,
                v.IsOutOfStock))
            .ToList();

        return new ProductDetailDto(
            product.Id,
            product.CategoryId,
            category?.Name ?? "Geral",
            category?.Slug ?? "geral",
            product.Name,
            product.Slug,
            product.Description,
            product.BasePrice,
            product.IsVisibleOnSite,
            product.IsArchived,
            product.TotalStockQuantity,
            product.HasAvailableStock,
            images,
            variants,
            product.CreatedAt,
            product.UpdatedAt);
    }
}

