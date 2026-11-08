using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Fotografia associada a um produto armazenada no Supabase Storage.
/// </summary>
public sealed class ProductImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public string? AltText { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsPrimary { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ProductImage()
    {
    }

    public ProductImage(
        Guid productId,
        string storagePath,
        string publicUrl,
        string? altText = null,
        int displayOrder = 0,
        bool isPrimary = false)
    {
        if (productId == Guid.Empty)
            throw new DomainException("A imagem deve estar associada a um produto válido.");

        if (string.IsNullOrWhiteSpace(publicUrl))
            throw new DomainException("A URL pública da imagem é obrigatória.");

        ProductId = productId;
        StoragePath = string.IsNullOrWhiteSpace(storagePath) ? publicUrl : storagePath.Trim();
        PublicUrl = publicUrl.Trim();
        AltText = altText?.Trim();
        DisplayOrder = displayOrder;
        IsPrimary = isPrimary;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}

