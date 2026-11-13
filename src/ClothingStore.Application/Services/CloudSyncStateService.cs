using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Application.Services;

/// <summary>
/// Aplica o snapshot de loja, categorias, produtos, variantes e imagens de forma idempotente
/// tanto no endpoint da API quanto no catálogo Web (Render / servidor próprio).
/// </summary>
public sealed class CloudSyncStateService
{
    private readonly IClothingStoreRepository _repository;
    private readonly EmployeeAndSettingsService _settingsService;

    public CloudSyncStateService(
        IClothingStoreRepository repository,
        EmployeeAndSettingsService settingsService)
    {
        _repository = repository;
        _settingsService = settingsService;
    }

    public async Task<CloudSyncApplyResult> ApplySyncPayloadAsync(
        CloudCatalogSyncPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var appliedSettings = await ApplySettingsAsync(payload.Settings, ct);
        var categoriesBySlug = await UpsertCategoriesAsync(payload.Categories, ct);

        int productsCount;
        if (payload.CatalogSnapshot is not null)
        {
            productsCount = await SyncSnapshotProductsAsync(payload.CatalogSnapshot, categoriesBySlug, ct);
        }
        else
        {
            productsCount = await UpsertLegacyProductsAsync(payload.Products, categoriesBySlug, ct);
        }

        var finalCategories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var finalSettings = await _repository.GetStoreSettingsAsync(ct);

        var effectiveCatalogUrl = WhatsAppLinkBuilder.BuildStoreCatalogUrl(
            string.IsNullOrWhiteSpace(finalSettings.OnlineBaseUrl)
                ? WhatsAppLinkBuilder.GenerateDefaultStoreUrl(finalSettings.StoreName)
                : finalSettings.OnlineBaseUrl,
            finalSettings.StoreSlug);

        return new CloudSyncApplyResult(
            CategoriesCount: finalCategories.Count,
            ProductsCount: productsCount,
            IsSiteActive: finalSettings.IsSiteActive,
            StoreName: finalSettings.StoreName,
            StoreSlug: finalSettings.StoreSlug,
            EffectiveCatalogUrl: effectiveCatalogUrl,
            AppliedSettings: appliedSettings);
    }

    private async Task<AdminStoreSettingsDto> ApplySettingsAsync(
        UpdateStoreSettingsRequest settingsRequest,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settingsRequest.StoreName))
        {
            var current = await _repository.GetStoreSettingsAsync(ct);
            return EmployeeAndSettingsService.MapToAdminSettingsDto(current);
        }

        // Mantém fallback seguro caso algum payload mínimo omita WhatsApp/Descrição.
        var currentSettings = await _repository.GetStoreSettingsAsync(ct);
        if (string.IsNullOrWhiteSpace(settingsRequest.WhatsappNumber))
        {
            settingsRequest.WhatsappNumber = currentSettings.WhatsappNumber;
        }
        if (string.IsNullOrWhiteSpace(settingsRequest.StoreDescription))
        {
            settingsRequest.StoreDescription = currentSettings.StoreDescription;
        }

        var updated = await _settingsService.UpdateStoreSettingsAsync(null, settingsRequest, ct);

        // UpdateSettings desativa temporariamente o site ao detectar mudança do nome publicado
        // (ex.: de "Minha Loja" inicial para o nome real). No sync online preservamos o estado desejado.
        if (settingsRequest.IsSiteActive)
        {
            updated = await _settingsService.ActivateStoreSiteAsync(null, ct);
        }
        else if (updated.IsSiteActive)
        {
            updated = await _settingsService.DeactivateStoreSiteAsync(
                null,
                string.IsNullOrWhiteSpace(settingsRequest.DeactivationReason)
                    ? "Site pausado pela loja."
                    : settingsRequest.DeactivationReason,
                ct);
        }

        return updated;
    }

    private async Task<Dictionary<string, Category>> UpsertCategoriesAsync(
        IReadOnlyList<SaveCategoryRequest> incomingCategories,
        CancellationToken ct)
    {
        var existing = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var bySlug = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);

        foreach (var cat in existing)
        {
            if (!string.IsNullOrWhiteSpace(cat.Slug))
            {
                bySlug[cat.Slug] = cat;
            }
        }

        foreach (var req in incomingCategories)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                continue;
            }

            var slug = Category.GenerateSlug(req.Name);
            if (bySlug.TryGetValue(slug, out var current))
            {
                current.UpdateDetails(req.Name, req.Description, req.DisplayOrder, req.IsActive);
                await _repository.UpdateCategoryAsync(current, ct);
            }
            else
            {
                var created = new Category(req.Name, req.Description, req.DisplayOrder);
                if (!req.IsActive)
                {
                    created.UpdateDetails(req.Name, req.Description, req.DisplayOrder, false);
                }

                await _repository.AddCategoryAsync(created, ct);
                bySlug[created.Slug] = created;
            }
        }

        return bySlug;
    }

    private async Task<Category> ResolveOrCreateCategoryAsync(
        string? categoryName,
        string? categorySlug,
        Guid? fallbackCategoryId,
        IDictionary<string, Category> bySlug,
        IReadOnlyList<Category> allCategories,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(categorySlug))
        {
            var normalizedSlug = Category.GenerateSlug(categorySlug);
            if (bySlug.TryGetValue(normalizedSlug, out var matchBySlug))
            {
                return matchBySlug;
            }
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var generatedSlug = Category.GenerateSlug(categoryName);
            if (bySlug.TryGetValue(generatedSlug, out var matchByName))
            {
                return matchByName;
            }

            var created = new Category(categoryName.Trim(), null, bySlug.Count + 1);
            await _repository.AddCategoryAsync(created, ct);
            bySlug[created.Slug] = created;
            return created;
        }

        if (fallbackCategoryId is Guid fid && fid != Guid.Empty)
        {
            var matchById = allCategories.FirstOrDefault(c => c.Id == fid)
                ?? bySlug.Values.FirstOrDefault(c => c.Id == fid);
            if (matchById is not null)
            {
                return matchById;
            }
        }

        if (bySlug.TryGetValue("geral", out var general))
        {
            return general;
        }

        if (bySlug.Values.FirstOrDefault() is { } firstExisting)
        {
            return firstExisting;
        }

        var fallbackCategory = new Category("Geral", "Categoria padrão", 1);
        await _repository.AddCategoryAsync(fallbackCategory, ct);
        bySlug[fallbackCategory.Slug] = fallbackCategory;
        return fallbackCategory;
    }

    private async Task<int> SyncSnapshotProductsAsync(
        IReadOnlyList<CloudProductSyncDto> snapshot,
        Dictionary<string, Category> categoriesBySlug,
        CancellationToken ct)
    {
        var allCategories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var existingList = (await _repository.GetProductsAsync(includeArchived: true, ct)).ToList();
        var syncedIds = new HashSet<Guid>();

        foreach (var item in snapshot)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var category = await ResolveOrCreateCategoryAsync(
                item.CategoryName,
                item.CategorySlug,
                null,
                categoriesBySlug,
                allCategories,
                ct);

            var targetSlug = Category.GenerateSlug(!string.IsNullOrWhiteSpace(item.Slug) ? item.Slug : item.Name);
            var existing = FindMatchingProduct(existingList, item.SourceId, targetSlug, item.Name);

            if (existing is not null)
            {
                existing.IsArchived = false;
                existing.UpdateDetails(
                    category.Id,
                    item.Name,
                    item.Description,
                    Math.Max(0m, item.BasePrice),
                    item.IsVisibleOnSite);

                ApplyDesiredSlugIfUnique(existing, existingList, targetSlug);
                SyncVariants(existing, item.Variants);
                SyncImages(existing, item.Images);

                await _repository.UpdateProductAsync(existing, ct);
                syncedIds.Add(existing.Id);
            }
            else
            {
                var created = new Product(
                    category.Id,
                    item.Name,
                    item.Description,
                    Math.Max(0m, item.BasePrice),
                    item.IsVisibleOnSite);

                if (item.SourceId != Guid.Empty && !existingList.Any(p => p.Id == item.SourceId))
                {
                    created.Id = item.SourceId;
                }

                ApplyDesiredSlugIfUnique(created, existingList, targetSlug);
                SyncVariants(created, item.Variants);
                SyncImages(created, item.Images);

                await _repository.AddProductAsync(created, ct);
                existingList.Add(created);
                syncedIds.Add(created.Id);
            }
        }

        // Produtos removidos localmente também deixam de aparecer na vitrine publicada.
        foreach (var stale in existingList.Where(p => !p.IsArchived && !syncedIds.Contains(p.Id)).ToList())
        {
            await _repository.DeleteOrArchiveProductAsync(stale.Id, ct);
        }

        return syncedIds.Count;
    }

    private async Task<int> UpsertLegacyProductsAsync(
        IReadOnlyList<SaveProductRequest> products,
        Dictionary<string, Category> categoriesBySlug,
        CancellationToken ct)
    {
        if (products.Count == 0)
        {
            var current = await _repository.GetProductsAsync(includeArchived: false, ct);
            return current.Count;
        }

        var allCategories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var existingList = (await _repository.GetProductsAsync(includeArchived: true, ct)).ToList();
        var count = 0;

        foreach (var req in products)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                continue;
            }

            var category = await ResolveOrCreateCategoryAsync(
                req.CategoryName,
                null,
                req.CategoryId,
                categoriesBySlug,
                allCategories,
                ct);

            var targetSlug = Category.GenerateSlug(req.Name);
            var existing = FindMatchingProduct(existingList, Guid.Empty, targetSlug, req.Name);

            if (existing is not null)
            {
                existing.IsArchived = false;
                existing.UpdateDetails(category.Id, req.Name, req.Description, req.BasePrice, req.IsVisibleOnSite);
                await _repository.UpdateProductAsync(existing, ct);
            }
            else
            {
                var created = new Product(category.Id, req.Name, req.Description, req.BasePrice, req.IsVisibleOnSite);
                if (!string.IsNullOrWhiteSpace(req.InitialColor) && !string.IsNullOrWhiteSpace(req.InitialSize))
                {
                    var variant = created.AddVariant(req.InitialColor, req.InitialSize);
                    variant.StockQuantity = Math.Max(0, req.InitialStockQuantity);
                }

                await _repository.AddProductAsync(created, ct);
                existingList.Add(created);
            }

            count++;
        }

        return count;
    }

    private static Product? FindMatchingProduct(
        IReadOnlyList<Product> existingList,
        Guid sourceId,
        string targetSlug,
        string name)
    {
        if (sourceId != Guid.Empty)
        {
            var byId = existingList.FirstOrDefault(p => p.Id == sourceId);
            if (byId is not null)
            {
                return byId;
            }
        }

        var trimmedName = name.Trim();
        return existingList
                   .Where(p => !p.IsArchived)
                   .FirstOrDefault(p =>
                       string.Equals(p.Slug, targetSlug, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(p.Name.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase))
               ?? existingList.FirstOrDefault(p =>
                   string.Equals(p.Slug, targetSlug, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p.Name.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyDesiredSlugIfUnique(
        Product product,
        IReadOnlyList<Product> existingList,
        string desiredSlug)
    {
        if (string.IsNullOrWhiteSpace(desiredSlug))
        {
            return;
        }

        var collision = existingList.Any(p =>
            p.Id != product.Id &&
            !p.IsArchived &&
            string.Equals(p.Slug, desiredSlug, StringComparison.OrdinalIgnoreCase));

        if (!collision)
        {
            product.Slug = desiredSlug;
        }
    }

    private static void SyncVariants(Product product, IReadOnlyList<CloudProductVariantSyncDto> incomingVariants)
    {
        var matchedVariantIds = new HashSet<Guid>();

        foreach (var vDto in incomingVariants)
        {
            if (string.IsNullOrWhiteSpace(vDto.Color) || string.IsNullOrWhiteSpace(vDto.Size))
            {
                continue;
            }

            var normalizedColor = vDto.Color.Trim();
            var normalizedSize = vDto.Size.Trim().ToUpperInvariant();
            var normalizedSku = string.IsNullOrWhiteSpace(vDto.Sku)
                ? ProductVariant.GenerateSku(product.Name, normalizedColor, normalizedSize)
                : vDto.Sku.Trim().ToUpperInvariant();

            var match = product.Variants.FirstOrDefault(v =>
                !matchedVariantIds.Contains(v.Id) &&
                ((string.Equals(v.Color, normalizedColor, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(v.Size, normalizedSize, StringComparison.OrdinalIgnoreCase)) ||
                 string.Equals(v.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                match.ProductId = product.Id;
                match.UpdateDetails(
                    normalizedSku,
                    normalizedColor,
                    normalizedSize,
                    vDto.ColorHex,
                    Math.Max(0, vDto.LowStockThreshold),
                    vDto.IsActive);
                match.StockQuantity = Math.Max(0, vDto.StockQuantity);
                matchedVariantIds.Add(match.Id);
            }
            else
            {
                var created = new ProductVariant(
                    product.Id,
                    normalizedSku,
                    normalizedColor,
                    normalizedSize,
                    vDto.ColorHex,
                    Math.Max(0, vDto.LowStockThreshold))
                {
                    StockQuantity = Math.Max(0, vDto.StockQuantity),
                    IsActive = vDto.IsActive
                };
                product.Variants.Add(created);
                matchedVariantIds.Add(created.Id);
            }
        }

        foreach (var removed in product.Variants.Where(v => !matchedVariantIds.Contains(v.Id)))
        {
            removed.IsActive = false;
            removed.StockQuantity = 0;
            removed.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static void SyncImages(Product product, IReadOnlyList<CloudProductImageSyncDto> incomingImages)
    {
        var validImages = incomingImages
            .Where(img => !string.IsNullOrWhiteSpace(img.PublicUrl))
            .OrderBy(img => img.DisplayOrder)
            .ToList();

        product.Images = validImages
            .Select((img, index) => new ProductImage(
                product.Id,
                string.IsNullOrWhiteSpace(img.StoragePath) ? img.PublicUrl : img.StoragePath,
                img.PublicUrl,
                img.AltText,
                img.DisplayOrder > 0 ? img.DisplayOrder : index,
                img.IsPrimary || (index == 0 && validImages.All(x => !x.IsPrimary))))
            .ToList();
    }
}

public sealed record CloudSyncApplyResult(
    int CategoriesCount,
    int ProductsCount,
    bool IsSiteActive,
    string StoreName,
    string StoreSlug,
    string EffectiveCatalogUrl,
    AdminStoreSettingsDto AppliedSettings);
