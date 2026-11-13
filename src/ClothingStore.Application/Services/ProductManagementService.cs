using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Serviço administrativo de gerenciamento de categorias, produtos, variantes e fotografias.
/// </summary>
public sealed class ProductManagementService
{
    private readonly IClothingStoreRepository _repository;
    private readonly IImageStorageService _imageStorage;

    public ProductManagementService(
        IClothingStoreRepository repository,
        IImageStorageService imageStorage)
    {
        _repository = repository;
        _imageStorage = imageStorage;
    }

    public async Task<IReadOnlyList<CategoryDto>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);

        return categories
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.Slug,
                c.Description,
                c.DisplayOrder,
                c.IsActive,
                products.Count(p => p.CategoryId == c.Id)))
            .ToList();
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default)
    {
        var category = new Category(request.Name, request.Description, request.DisplayOrder)
        {
            IsActive = request.IsActive
        };

        await _repository.AddCategoryAsync(category, ct);
        return new CategoryDto(category.Id, category.Name, category.Slug, category.Description, category.DisplayOrder, category.IsActive, 0);
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken ct = default)
    {
        var category = await _repository.GetCategoryByIdAsync(id, ct)
            ?? throw new DomainException("Categoria não encontrada.");

        category.UpdateDetails(request.Name, request.Description, request.DisplayOrder, request.IsActive);
        await _repository.UpdateCategoryAsync(category, ct);

        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        var count = products.Count(p => p.CategoryId == category.Id);
        return new CategoryDto(category.Id, category.Name, category.Slug, category.Description, category.DisplayOrder, category.IsActive, count);
    }

    public async Task<IReadOnlyList<ProductDetailDto>> GetAdminProductsAsync(
        string? search = null,
        Guid? categoryId = null,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var products = await _repository.GetProductsAsync(includeArchived, ct);
        var categories = (await _repository.GetCategoriesAsync(onlyActive: false, ct)).ToDictionary(c => c.Id);

        var query = products.AsEnumerable();

        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                p.Variants.Any(v => v.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        return query
            .Select(p =>
            {
                categories.TryGetValue(p.CategoryId, out var cat);
                return CatalogService.MapToProductDetailDto(p, cat);
            })
            .ToList();
    }

    public async Task<ProductDetailDto> GetProductByIdAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var category = await _repository.GetCategoryByIdAsync(product.CategoryId, ct);
        return CatalogService.MapToProductDetailDto(product, category);
    }

    private async Task<Category> ResolveOrCreateCategoryAsync(
        Guid categoryId,
        string? categoryName,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var trimmedName = categoryName.Trim();
            var slug = Category.GenerateSlug(trimmedName);
            var existingCategories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
            var matched = existingCategories.FirstOrDefault(c =>
                string.Equals(c.Name, trimmedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
                return matched;

            var created = new Category(trimmedName, null, existingCategories.Count + 1)
            {
                IsActive = true
            };
            await _repository.AddCategoryAsync(created, ct);
            return created;
        }

        if (categoryId != Guid.Empty)
        {
            var byId = await _repository.GetCategoryByIdAsync(categoryId, ct);
            if (byId is not null)
                return byId;
        }

        var allCategories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        if (allCategories.Count > 0)
            return allCategories[0];

        throw new DomainException("Nenhuma categoria cadastrada ainda. Crie ou digite o nome da sua categoria ao adicionar o produto.");
    }

    public async Task<ProductDetailDto> CreateProductAsync(
        SaveProductRequest request,
        Guid? employeeId = null,
        CancellationToken ct = default)
    {
        var category = await ResolveOrCreateCategoryAsync(request.CategoryId, request.CategoryName, ct);

        var product = new Product(
            category.Id,
            request.Name,
            request.Description,
            request.BasePrice,
            request.IsVisibleOnSite);

        await _repository.AddProductAsync(product, ct);

        if (!string.IsNullOrWhiteSpace(request.InitialColor) && !string.IsNullOrWhiteSpace(request.InitialSize))
        {
            var variant = product.AddVariant(
                request.InitialColor,
                request.InitialSize,
                ProductVariant.ResolveDefaultColorHex(request.InitialColor),
                customSku: null,
                lowStockThreshold: 3);

            await _repository.UpdateProductAsync(product, ct);

            if (request.InitialStockQuantity > 0)
            {
                var effectiveEmpId = employeeId.HasValue && employeeId.Value != Guid.Empty
                    ? employeeId.Value
                    : (await _repository.GetEmployeesAsync(ct)).FirstOrDefault()?.Id ?? Guid.Empty;

                if (effectiveEmpId != Guid.Empty)
                {
                    var movement = variant.ApplyStockMovement(
                        effectiveEmpId,
                        StockMovementType.In,
                        request.InitialStockQuantity,
                        "Estoque inicial no cadastro do produto");

                    await _repository.RecordStockMovementAsync(variant, movement, ct);
                    await _repository.UpdateProductAsync(product, ct);
                }
            }
        }

        return CatalogService.MapToProductDetailDto(product, category);
    }

    public async Task<ProductDetailDto> UpdateProductAsync(Guid productId, SaveProductRequest request, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var category = await ResolveOrCreateCategoryAsync(request.CategoryId, request.CategoryName, ct);

        product.UpdateDetails(
            category.Id,
            request.Name,
            request.Description,
            request.BasePrice,
            request.IsVisibleOnSite);

        await _repository.UpdateProductAsync(product, ct);
        return CatalogService.MapToProductDetailDto(product, category);
    }

    public async Task<ProductDetailDto> ToggleVisibilityAsync(Guid productId, bool isVisibleOnSite, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        product.IsVisibleOnSite = isVisibleOnSite;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.UpdateProductAsync(product, ct);

        var category = await _repository.GetCategoryByIdAsync(product.CategoryId, ct);
        return CatalogService.MapToProductDetailDto(product, category);
    }

    public async Task DeleteOrArchiveProductAsync(Guid productId, CancellationToken ct = default)
    {
        await _repository.DeleteOrArchiveProductAsync(productId, ct);
    }

    // =========================================================================
    // VARIANTES (Cor + Tamanho + Estoque Independente)
    // =========================================================================
    public async Task<ProductVariantDto> AddVariantAsync(
        Guid productId,
        Guid employeeId,
        SaveProductVariantRequest request,
        CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var variant = product.AddVariant(
            request.Color,
            request.Size,
            request.ColorHex,
            request.CustomSku,
            request.LowStockThreshold);

        await _repository.UpdateProductAsync(product, ct);

        if (request.InitialStockQuantity > 0 && employeeId != Guid.Empty)
        {
            var movement = variant.ApplyStockMovement(
                employeeId,
                StockMovementType.In,
                request.InitialStockQuantity,
                "Estoque inicial no cadastro da variante");

            await _repository.RecordStockMovementAsync(variant, movement, ct);
            await _repository.UpdateProductAsync(product, ct);
        }

        return new ProductVariantDto(
            variant.Id,
            variant.ProductId,
            variant.Sku,
            variant.Color,
            variant.ColorHex ?? ProductVariant.ResolveDefaultColorHex(variant.Color),
            variant.Size,
            variant.StockQuantity,
            variant.LowStockThreshold,
            variant.IsActive,
            variant.IsLowStock,
            variant.IsOutOfStock);
    }

    public async Task<ProductVariantDto> UpdateVariantAsync(
        Guid variantId,
        SaveProductVariantRequest request,
        Guid? employeeId = null,
        CancellationToken ct = default)
    {
        var variant = await _repository.GetVariantByIdAsync(variantId, ct)
            ?? throw new DomainException("Variante não encontrada.");

        var product = await _repository.GetProductByIdAsync(variant.ProductId, ct)
            ?? throw new DomainException("Produto da variante não encontrado.");

        var duplicate = product.Variants.Any(v =>
            v.Id != variantId &&
            v.IsActive &&
            string.Equals(v.Color, request.Color.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(v.Size, request.Size.Trim(), StringComparison.OrdinalIgnoreCase));

        if (duplicate)
            throw new DomainException("Já existe outra variante ativa com esta mesma cor e tamanho neste produto.");

        var sku = string.IsNullOrWhiteSpace(request.CustomSku) ? variant.Sku : request.CustomSku;
        variant.UpdateDetails(sku, request.Color, request.Size, request.ColorHex, request.LowStockThreshold, request.IsActive);

        if (request.UpdateStockQuantity && request.InitialStockQuantity >= 0 && request.InitialStockQuantity != variant.StockQuantity)
        {
            var effectiveEmpId = employeeId.HasValue && employeeId.Value != Guid.Empty
                ? employeeId.Value
                : (await _repository.GetEmployeesAsync(ct)).FirstOrDefault()?.Id ?? Guid.Empty;

            var movement = variant.ApplyStockMovement(
                effectiveEmpId,
                StockMovementType.Adjustment,
                request.InitialStockQuantity,
                "Ajuste direto na edição da variante");

            await _repository.RecordStockMovementAsync(variant, movement, ct);
        }

        await _repository.UpdateProductAsync(product, ct);

        return new ProductVariantDto(
            variant.Id,
            variant.ProductId,
            variant.Sku,
            variant.Color,
            variant.ColorHex ?? ProductVariant.ResolveDefaultColorHex(variant.Color),
            variant.Size,
            variant.StockQuantity,
            variant.LowStockThreshold,
            variant.IsActive,
            variant.IsLowStock,
            variant.IsOutOfStock);
    }

    public async Task DeleteVariantAsync(Guid variantId, CancellationToken ct = default)
    {
        var variant = await _repository.GetVariantByIdAsync(variantId, ct)
            ?? throw new DomainException("Variante não encontrada.");

        var product = await _repository.GetProductByIdAsync(variant.ProductId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var movements = await _repository.GetStockMovementsAsync(variantId: variantId, limit: 1, ct: ct);
        if (movements.Count > 0)
        {
            variant.IsActive = false;
            variant.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            product.Variants.RemoveAll(v => v.Id == variantId);
        }

        await _repository.UpdateProductAsync(product, ct);
    }

    // =========================================================================
    // IMAGENS DO PRODUTO (Supabase Storage + Galeria)
    // =========================================================================
    public async Task<ProductImageDto> UploadProductImageAsync(
        Guid productId,
        string fileName,
        Stream contentStream,
        string contentType,
        string? altText,
        bool isPrimary,
        CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var (storagePath, publicUrl) = await _imageStorage.UploadImageAsync(
            $"products/{productId:N}",
            fileName,
            contentStream,
            contentType,
            ct);

        return await AttachImageToProductAsync(product, storagePath, publicUrl, altText, isPrimary, ct);
    }

    public async Task<ProductImageDto> AddProductImageByUrlAsync(
        Guid productId,
        AddProductImageByUrlRequest request,
        CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        return await AttachImageToProductAsync(
            product,
            request.ImageUrl,
            request.ImageUrl,
            request.AltText,
            request.IsPrimary,
            ct);
    }

    private async Task<ProductImageDto> AttachImageToProductAsync(
        Product product,
        string storagePath,
        string publicUrl,
        string? altText,
        bool isPrimary,
        CancellationToken ct)
    {
        var shouldBePrimary = isPrimary || product.Images.Count == 0;
        if (shouldBePrimary)
        {
            foreach (var existing in product.Images)
            {
                existing.IsPrimary = false;
            }
        }

        var nextOrder = product.Images.Count == 0 ? 1 : product.Images.Max(i => i.DisplayOrder) + 1;
        var image = new ProductImage(
            product.Id,
            storagePath,
            publicUrl,
            string.IsNullOrWhiteSpace(altText) ? product.Name : altText,
            nextOrder,
            shouldBePrimary);

        product.Images.Add(image);
        await _repository.UpdateProductAsync(product, ct);

        return new ProductImageDto(
            image.Id,
            image.ProductId,
            image.PublicUrl,
            image.AltText,
            image.DisplayOrder,
            image.IsPrimary);
    }

    public async Task SetPrimaryProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var target = product.Images.FirstOrDefault(i => i.Id == imageId)
            ?? throw new DomainException("Fotografia não encontrada neste produto.");

        foreach (var img in product.Images)
        {
            img.IsPrimary = img.Id == target.Id;
        }

        await _repository.UpdateProductAsync(product, ct);
    }

    public async Task DeleteProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var target = product.Images.FirstOrDefault(i => i.Id == imageId)
            ?? throw new DomainException("Fotografia não encontrada neste produto.");

        product.Images.Remove(target);
        if (target.IsPrimary && product.Images.Count > 0)
        {
            product.Images.OrderBy(i => i.DisplayOrder).First().IsPrimary = true;
        }

        await _repository.UpdateProductAsync(product, ct);
        await _imageStorage.DeleteImageAsync(target.StoragePath, ct);
    }
}

