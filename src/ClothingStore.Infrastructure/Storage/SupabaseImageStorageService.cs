using System.Net.Http.Headers;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Exceptions;
using Microsoft.Extensions.Configuration;

namespace ClothingStore.Infrastructure.Storage;

/// <summary>
/// Serviço de armazenamento de imagens integrado ao Supabase Storage (via API REST oficial com Service Role Key no backend)
/// e fallback automático com Data URI + arquivo local, com validação estrita de tamanho máximo (8 MB),
/// assinatura binária (magic bytes) e proteção contra path traversal e XSS por SVG.
/// </summary>
public sealed class SupabaseImageStorageService : IImageStorageService
{
    public const int MaxUploadSizeBytes = 8 * 1024 * 1024;

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
        _localUploadsRoot = Path.GetFullPath(Path.Combine(localWebRootPath, "uploads"));
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
        if (contentStream is null)
            throw new DomainException("Arquivo de imagem inválido.");

        var bytes = await ReadBoundedBytesAsync(contentStream, MaxUploadSizeBytes, ct);
        if (bytes.Length == 0)
            throw new DomainException("O arquivo de imagem está vazio.");

        var (safeExtension, resolvedMime) = ValidateAndDetectImageFormat(bytes, fileName, contentType);

        var safeFolder = SanitizeFolderPath(folder);
        var uniqueFileName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{safeExtension}";
        var storagePath = $"{safeFolder}/{uniqueFileName}";

        if (IsSupabaseConfigured)
        {
            var requestUrl = $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{storagePath}";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _supabaseServiceKey);
            request.Headers.Add("apikey", _supabaseServiceKey);
            request.Headers.Add("x-upsert", "true");

            var byteContent = new ByteArrayContent(bytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue(resolvedMime);
            request.Content = byteContent;

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var publicUrl = $"{_supabaseUrl}/storage/v1/object/public/{_bucketName}/{storagePath}";
            return (storagePath, publicUrl);
        }

        // Fallback local: lê os bytes validados, salva cópia em disco e retorna Data URI seguro
        try
        {
            var targetDirectory = ResolveSafeSubPath(_localUploadsRoot, safeFolder);
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var physicalPath = ResolveSafeSubPath(targetDirectory, uniqueFileName);
            await File.WriteAllBytesAsync(physicalPath, bytes, ct);
        }
        catch (DomainException)
        {
            throw;
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
            var sanitizedPath = SanitizeFolderPath(storagePath);
            try
            {
                var requestUrl = $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{sanitizedPath}";
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
            var physicalPath = ResolveSafeSubPath(_localUploadsRoot, storagePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }
        }
        catch
        {
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
            {
                throw new DomainException($"A imagem ultrapassa o limite máximo permitido de {maxBytes / (1024 * 1024)} MB.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    public static (string Extension, string MimeType) ValidateAndDetectImageFormat(
        ReadOnlySpan<byte> bytes,
        string? fileName,
        string? declaredContentType)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (ext == ".svg" || string.Equals(declaredContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Arquivos SVG não são permitidos por segurança (risco de script embutido). Use PNG, JPG, WebP ou GIF.");
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return (".png", "image/png");
        }

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return (".jpg", "image/jpeg");
        }

        // GIF87a / GIF89a
        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
        {
            return (".gif", "image/gif");
        }

        // WebP: RIFF....WEBP
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return (".webp", "image/webp");
        }

        throw new DomainException("Formato de imagem inválido ou não suportado. Envie apenas PNG, JPG, WebP ou GIF válidos.");
    }

    private static string SanitizeFolderPath(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return "products";

        var segments = folder
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(seg => seg is not "." and not ".." && !seg.Contains(':'))
            .Select(seg => new string(seg.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()))
            .Where(seg => !string.IsNullOrWhiteSpace(seg))
            .ToArray();

        return segments.Length == 0 ? "products" : string.Join('/', segments);
    }

    private static string ResolveSafeSubPath(string rootDir, string relativePath)
    {
        var fullRoot = Path.GetFullPath(rootDir);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath.TrimStart('/', '\\')));
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Caminho de armazenamento inválido.");
        }

        return combined;
    }
}
