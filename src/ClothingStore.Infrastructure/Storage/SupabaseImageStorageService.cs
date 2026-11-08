using System.Net.Http.Headers;
using ClothingStore.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace ClothingStore.Infrastructure.Storage;

/// <summary>
/// Serviço de armazenamento de imagens integrado ao Supabase Storage (via API REST oficial com Service Role Key no backend)
/// e fallback automático para armazenamento local em /wwwroot/uploads durante desenvolvimento.
/// </summary>
public sealed class SupabaseImageStorageService : IImageStorageService
{
    private readonly HttpClient _httpClient;
    private readonly string? _supabaseUrl;
    private readonly string? _supabaseServiceKey;
    private readonly string _bucketName;
    private readonly string _localUploadsRoot;
    private readonly string _publicBaseUrl;

    public SupabaseImageStorageService(
        HttpClient httpClient,
        IConfiguration configuration,
        string localWebRootPath)
    {
        _httpClient = httpClient;
        _supabaseUrl = (configuration["SUPABASE_URL"] ?? configuration["Supabase:Url"])?.TrimEnd('/');
        _supabaseServiceKey = configuration["SUPABASE_SERVICE_ROLE_KEY"] ?? configuration["Supabase:ServiceRoleKey"];
        _bucketName = configuration["SUPABASE_STORAGE_BUCKET"] ?? configuration["Supabase:Bucket"] ?? "clothing-images";
        _publicBaseUrl = (configuration["PUBLIC_API_BASE_URL"] ?? configuration["PublicApiBaseUrl"] ?? "http://localhost:5180").TrimEnd('/');
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

        if (IsSupabaseConfigured)
        {
            var requestUrl = $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{storagePath}";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _supabaseServiceKey);
            request.Headers.Add("apikey", _supabaseServiceKey);
            request.Headers.Add("x-upsert", "true");

            var streamContent = new StreamContent(contentStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);
            request.Content = streamContent;

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var publicUrl = $"{_supabaseUrl}/storage/v1/object/public/{_bucketName}/{storagePath}";
            return (storagePath, publicUrl);
        }

        // Fallback de desenvolvimento: salva localmente na pasta wwwroot/uploads da API
        var targetDirectory = Path.Combine(_localUploadsRoot, safeFolder);
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var physicalPath = Path.Combine(targetDirectory, uniqueFileName);
        await using (var fileStream = File.Create(physicalPath))
        {
            await contentStream.CopyToAsync(fileStream, ct);
        }

        var localPublicUrl = $"{_publicBaseUrl}/uploads/{storagePath}";
        return (storagePath, localPublicUrl);
    }

    public async Task DeleteImageAsync(string storagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            return;

        if (IsSupabaseConfigured && !storagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
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
                // Ignora falha de deleção remota caso o objeto já tenha sido removido
            }
            return;
        }

        var physicalPath = Path.Combine(_localUploadsRoot, storagePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
        }
    }
}
