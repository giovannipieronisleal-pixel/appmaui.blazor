using System.Net.Http.Headers;
using ClothingStore.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace ClothingStore.Infrastructure.Storage;

/// <summary>
/// Serviço de armazenamento de imagens integrado ao Supabase Storage (via API REST oficial com Service Role Key no backend)
/// e fallback automático com Data URI + arquivo local durante o desenvolvimento para funcionar imediatamente no App e no Site.
/// </summary>
public sealed class SupabaseImageStorageService : IImageStorageService
{
    private readonly HttpClient _httpClient;
    private readonly string? _supabaseUrl;
    private readonly string? _supabaseServiceKey;
    private readonly string _bucketName;
    private readonly string _localUploadsRoot;

    public SupabaseImageStorageService(
        HttpClient httpClient,
        IConfiguration configuration,
        string localWebRootPath)
    {
        _httpClient = httpClient;
        _supabaseUrl = (configuration["SUPABASE_URL"] ?? configuration["Supabase:Url"])?.TrimEnd('/');
        _supabaseServiceKey = configuration["SUPABASE_SERVICE_ROLE_KEY"] ?? configuration["Supabase:ServiceRoleKey"];
        _bucketName = configuration["SUPABASE_STORAGE_BUCKET"] ?? configuration["Supabase:Bucket"] ?? "clothing-images";
        _localUploadsRoot = Path.Combine(localWebRootPath, "uploads");
    }

    private bool IsSupabaseConfigured =>
        !string.IsNullOrWhiteSpace(_supabaseUrl) &&
        !string.IsNullOrWhiteSpace(_supabaseServiceKey) &&
        !_supabaseUrl.Contains("seu-projeto.supabase.co", StringComparison.OrdinalIgnoreCase);

    public async Task<(string StoragePath, string PublicUrl)> UploadImageAsync(
        string folder,
        string fileName,
        Stream contentStream,
        string contentType,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        var safeFolder = string.IsNullOrWhiteSpace(folder) ? "products" : folder.Trim('/');
        var uniqueFileName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var storagePath = $"{safeFolder}/{uniqueFileName}";
        var resolvedMime = ResolveMimeType(contentType, extension);

        if (IsSupabaseConfigured)
        {
            var requestUrl = $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{storagePath}";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _supabaseServiceKey);
            request.Headers.Add("apikey", _supabaseServiceKey);
            request.Headers.Add("x-upsert", "true");

            var streamContent = new StreamContent(contentStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(resolvedMime);
            request.Content = streamContent;

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var publicUrl = $"{_supabaseUrl}/storage/v1/object/public/{_bucketName}/{storagePath}";
            return (storagePath, publicUrl);
        }

        // Fallback local: lê os bytes, salva cópia em disco e retorna Data URI para funcionar tanto no MAUI quanto no Site Web
        using var ms = new MemoryStream();
        await contentStream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            var targetDirectory = Path.Combine(_localUploadsRoot, safeFolder);
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var physicalPath = Path.Combine(targetDirectory, uniqueFileName);
            await File.WriteAllBytesAsync(physicalPath, bytes, ct);
        }
        catch
        {
        }

        var base64 = Convert.ToBase64String(bytes);
        var dataUri = $"data:{resolvedMime};base64,{base64}";
        return (storagePath, dataUri);
    }

    public async Task DeleteImageAsync(string storagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            return;

        if (IsSupabaseConfigured && !storagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !storagePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var requestUrl = $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{storagePath.TrimStart('/')}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, requestUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _supabaseServiceKey);
                request.Headers.Add("apikey", _supabaseServiceKey);
                await _httpClient.SendAsync(request, ct);
            }
            catch
            {
            }
            return;
        }

        try
        {
            var physicalPath = Path.Combine(_localUploadsRoot, storagePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }
        }
        catch
        {
        }
    }

    private static string ResolveMimeType(string? contentType, string extension)
    {
        if (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return contentType;

        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            _ => "image/jpeg"
        };
    }
}
