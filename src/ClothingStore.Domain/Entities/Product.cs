using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Representa um produto do catálogo de roupas, com suas fotografias e variantes de Cor/Tamanho.
/// </summary>
public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal BasePrice { get; set; }
    public bool IsVisibleOnSite { get; set; } = true;
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ProductVariant> Variants { get; set; } = [];
    public List<ProductImage> Images { get; set; } = [];

    public Product()
    {
    }

    public Product(
        Guid categoryId,
        string name,
        string? description,
        decimal basePrice,
        bool isVisibleOnSite = true)
    {
        UpdateDetails(categoryId, name, description, basePrice, isVisibleOnSite);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void UpdateDetails(
        Guid categoryId,
        string name,
        string? description,
        decimal basePrice,
        bool isVisibleOnSite)
    {
        if (categoryId == Guid.Empty)
            throw new DomainException("O produto deve estar associado a uma categoria válida.");

        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("O nome do produto é obrigatório.");

        if (basePrice < 0)
            throw new DomainException("O preço do produto não pode ser negativo.");

        CategoryId = categoryId;
        Name = name.Trim();
        Slug = Category.GenerateSlug(Name);
        Description = description?.Trim();
        BasePrice = Math.Round(basePrice, 2);
        IsVisibleOnSite = isVisibleOnSite;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public int TotalStockQuantity =>
        Variants.Where(v => v.IsActive).Sum(v => v.StockQuantity);

    public bool HasAvailableStock =>
        Variants.Any(v => v.IsActive && v.StockQuantity > 0);

    public string? PrimaryImageUrl =>
        Images
            .OrderByDescending(i => i.IsPrimary)
            .ThenBy(i => i.DisplayOrder)
            .Select(i => i.PublicUrl)
            .FirstOrDefault();

    public ProductVariant AddVariant(
        string color,
        string size,
        string? colorHex = null,
        string? customSku = null,
        int lowStockThreshold = 3)
    {
        if (string.IsNullOrWhiteSpace(color))
            throw new DomainException("A cor da variante é obrigatória.");

        if (string.IsNullOrWhiteSpace(size))
            throw new DomainException("O tamanho da variante é obrigatório.");

        var normalizedColor = color.Trim();
        var normalizedSize = size.Trim().ToUpperInvariant();

        var exists = Variants.Any(v =>
            v.IsActive &&
            string.Equals(v.Color, normalizedColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(v.Size, normalizedSize, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            throw new DomainException($"Já existe uma variante ativa para a cor '{normalizedColor}' e tamanho '{normalizedSize}'.");
        }

        var sku = string.IsNullOrWhiteSpace(customSku)
            ? ProductVariant.GenerateSku(Name, normalizedColor, normalizedSize)
            : customSku.Trim().ToUpperInvariant();

        var variant = new ProductVariant(
            Id,
            sku,
            normalizedColor,
            normalizedSize,
            colorHex,
            lowStockThreshold);

        Variants.Add(variant);
        UpdatedAt = DateTimeOffset.UtcNow;
        return variant;
    }

    public void Archive()
    {
        IsArchived = true;
        IsVisibleOnSite = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
