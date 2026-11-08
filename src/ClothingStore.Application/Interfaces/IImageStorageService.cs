namespace ClothingStore.Application.Interfaces;

/// <summary>
/// Abstração de armazenamento de imagens de produtos e logotipo da loja no Supabase Storage.
/// </summary>
public interface IImageStorageService
{
    Task<(string StoragePath, string PublicUrl)> UploadImageAsync(
        string folder,
        string fileName,
        Stream contentStream,
        string contentType,
        CancellationToken ct = default);

    Task DeleteImageAsync(string storagePath, CancellationToken ct = default);
}
