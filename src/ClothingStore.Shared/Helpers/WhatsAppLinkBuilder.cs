using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Utilitário compartilhado entre o site Blazor e o aplicativo para construção do link oficial do WhatsApp
/// (https://wa.me/NUMERO?text=MENSAGEM), link personalizado da loja com o nome da loja (/loja/{slug}) e QR Code fixo.
/// </summary>
public static class WhatsAppLinkBuilder
{
    public static string SanitizePhoneNumber(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone))
            return string.Empty;

        return Regex.Replace(rawPhone, @"\D", "");
    }

    public static string GenerateStoreSlug(string? storeName)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            return "minha-loja";

        var normalized = storeName.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var clean = sb.ToString().Normalize(NormalizationForm.FormC);
        clean = Regex.Replace(clean, @"[^a-z0-9\s-]", "");
        clean = Regex.Replace(clean, @"\s+", "-");
        clean = Regex.Replace(clean, @"-+", "-").Trim('-');
        if (clean.Length > 80)
        {
            clean = clean[..80].TrimEnd('-');
        }
        return string.IsNullOrWhiteSpace(clean) ? "minha-loja" : clean;
    }

    public static string NormalizePublicServerUrl(string? rawUrl)
    {
        var candidate = (rawUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return string.Empty;

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            IsCloudMetadataHost(uri.Host))
        {
            return string.Empty;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static bool IsCloudMetadataHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalized = host.Trim().Trim('[', ']').ToLowerInvariant();
        return normalized.StartsWith("169.254.", StringComparison.Ordinal) ||
               normalized.StartsWith("::ffff:169.254.", StringComparison.Ordinal) ||
               normalized == "metadata.google.internal" ||
               normalized == "metadata.goog" ||
               normalized == "metadata";
    }

    private static readonly Regex SafeDataImageRegex = new(
        @"^data:image/(png|jpeg|jpg|webp|gif);base64,[A-Za-z0-9+/=\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSafeMediaUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        var candidate = rawUrl.Trim();
        if (candidate.Length > 8_500_000 || candidate.Any(c => char.IsControl(c)))
            return false;

        if (candidate.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return !candidate.Contains("..", StringComparison.Ordinal);

        if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return SafeDataImageRegex.IsMatch(candidate);

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.IsNullOrWhiteSpace(uri.Host) && !IsCloudMetadataHost(uri.Host);
    }

    public static string SanitizeMediaUrl(string? rawUrl, string fallback = "")
    {
        if (!IsSafeMediaUrl(rawUrl))
            return fallback;

        return rawUrl!.Trim();
    }

    public static string NormalizeSyncSecretInput(string? rawSecret)
    {
        var candidate = (rawSecret ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return string.Empty;

        const string envPrefix = "YOULINK_SYNC_SECRET=";
        if (candidate.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[envPrefix.Length..].Trim();
        }

        if (candidate.Length >= 2 &&
            ((candidate.StartsWith('"') && candidate.EndsWith('"')) ||
             (candidate.StartsWith('\'') && candidate.EndsWith('\''))))
        {
            candidate = candidate[1..^1].Trim();
        }

        return candidate;
    }

    /// <summary>
    /// Gera o domínio padrão do catálogo no formato lojas.nomedaloja.com (ex.: lojas.minhaloja.com).
    /// </summary>
    public static string GenerateDefaultStoreDomain(string? storeName)
    {
        var clean = GenerateStoreSlug(storeName).Replace("-", "");
        if (string.IsNullOrWhiteSpace(clean))
            clean = "minhaloja";
        return $"lojas.{clean}.com";
    }

    /// <summary>
    /// Gera a URL pública padrão do catálogo no formato https://lojas.nomedaloja.com
    /// </summary>
    public static string GenerateDefaultStoreUrl(string? storeName)
    {
        return $"https://{GenerateDefaultStoreDomain(storeName)}";
    }

    private static readonly string[] SharedCloudHostSuffixes =
    [
        ".run.app",
        ".onrender.com",
        ".koyeb.app",
        ".fly.dev",
        ".up.railway.app",
        ".azurewebsites.net",
        ".pages.dev",
        ".workers.dev",
        ".github.io"
    ];

    private static bool IsSharedCloudHost(string host) =>
        host is "localhost" or "127.0.0.1" ||
        SharedCloudHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public static string BuildStoreCatalogUrl(string? baseSiteUrl, string? storeSlugOrName)
    {
        if (string.IsNullOrWhiteSpace(baseSiteUrl))
        {
            return GenerateDefaultStoreUrl(storeSlugOrName);
        }

        var cleanBase = baseSiteUrl.Trim().TrimEnd('/');

        var slug = GenerateStoreSlug(storeSlugOrName);

        var uriText = cleanBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      cleanBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? cleanBase
            : $"https://{cleanBase}";

        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            // Para servidor local ou hospedagem compartilhada gratuita/nuvem (Cloud Run, Render, Koyeb, Fly.io, etc.),
            // roteia diretamente para /loja/{slug} sem exigir compra de domínio separado por cliente.
            if (IsSharedCloudHost(host))
            {
                var path = uri.AbsolutePath.TrimEnd('/');
                if (path.EndsWith($"/loja/{slug}", StringComparison.OrdinalIgnoreCase))
                {
                    return cleanBase;
                }

                return $"{cleanBase}/loja/{slug}";
            }

            // Para domínio próprio oficial do lojista (ex.: https://minhalojadigital.com ou minhaloja.com.br):
            // O catálogo oficial é o próprio domínio direto da loja, limpo e profissional!
            return cleanBase;
        }

        return $"{cleanBase}/loja/{slug}";
    }

    public static string BuildProductConsultationMessage(string productName, string? color = null, string? size = null)
    {
        var cleanName = string.IsNullOrWhiteSpace(productName) ? "Produto" : productName.Trim();

        if (!string.IsNullOrWhiteSpace(color) && !string.IsNullOrWhiteSpace(size))
        {
            return $"Olá! Tenho interesse no produto {cleanName}, na cor {color.Trim().ToLowerInvariant()} e tamanho {size.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        if (!string.IsNullOrWhiteSpace(color))
        {
            return $"Olá! Tenho interesse no produto {cleanName}, na cor {color.Trim().ToLowerInvariant()}. Gostaria de saber mais informações.";
        }

        if (!string.IsNullOrWhiteSpace(size))
        {
            return $"Olá! Tenho interesse no produto {cleanName}, no tamanho {size.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        return $"Olá! Tenho interesse no produto {cleanName}. Gostaria de saber mais informações.";
    }

    public static string BuildProductConsultationLink(string whatsappNumber, string productName, string? color = null, string? size = null)
    {
        var phone = SanitizePhoneNumber(whatsappNumber);
        var message = BuildProductConsultationMessage(productName, color, size);
        var encoded = Uri.EscapeDataString(message);
        return $"https://wa.me/{phone}?text={encoded}";
    }

    public static string BuildGeneralStoreLink(string whatsappNumber, string storeName)
    {
        var phone = SanitizePhoneNumber(whatsappNumber);
        var message = $"Olá! Vim pelo catálogo online da {storeName.Trim()} e gostaria de atendimento.";
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }

    /// <summary>
    /// Gera um SVG determinístico representando o QR Code do link fixo da loja para exibição offline e impressão.
    /// </summary>
    public static string GenerateQrCodeSvgDataUri(string storeUrl)
    {
        const int gridSize = 21;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(storeUrl ?? "loja"));
        var sb = new StringBuilder();
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 250 250' width='180' height='180'>");
        sb.Append("<rect width='250' height='250' rx='12' fill='#FFFFFF' stroke='#E4E4E7' stroke-width='2'/>");

        int cellSize = 9;
        int offset = 30;

        static bool IsFinderPattern(int r, int c)
        {
            bool topLeft = r < 7 && c < 7;
            bool topRight = r < 7 && c >= 14;
            bool bottomLeft = r >= 14 && c < 7;
            return topLeft || topRight || bottomLeft;
        }

        static bool IsFinderDark(int r, int c)
        {
            int lr = r >= 14 ? r - 14 : r;
            int lc = c >= 14 ? c - 14 : c;
            if (lr == 0 || lr == 6 || lc == 0 || lc == 6) return true;
            if (lr >= 2 && lr <= 4 && lc >= 2 && lc <= 4) return true;
            return false;
        }

        for (int r = 0; r < gridSize; r++)
        {
            for (int c = 0; c < gridSize; c++)
            {
                bool dark;
                if (IsFinderPattern(r, c))
                {
                    dark = IsFinderDark(r, c);
                }
                else
                {
                    int bitIndex = (r * gridSize + c) % (hash.Length * 8);
                    int byteIdx = bitIndex / 8;
                    int bitOffset = bitIndex % 8;
                    dark = ((hash[byteIdx] >> bitOffset) & 1) == 1;
                }

                if (dark)
                {
                    int x = offset + c * cellSize;
                    int y = offset + r * cellSize;
                    sb.Append($"<rect x='{x}' y='{y}' width='{cellSize}' height='{cellSize}' fill='#18181B'/>");
                }
            }
        }

        sb.Append("</svg>");
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        return $"data:image/svg+xml;base64,{base64}";
    }
}
