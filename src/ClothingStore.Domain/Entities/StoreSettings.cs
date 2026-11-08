using System.Text.RegularExpressions;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Configurações personalizadas da loja gerenciadas pelo proprietário no aplicativo .NET MAUI
/// e carregadas dinamicamente pelo site Blazor Web App.
/// </summary>
public sealed class StoreSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StoreName { get; set; } = "Aura Vestuário";
    public string StoreSlug { get; set; } = "aura-vestuario";
    public string WhatsappNumber { get; set; } = "5511999999999";
    public string StoreDescription { get; set; } = "Moda contemporânea com peças essenciais, cortes precisos e tecidos selecionados.";
    public string? LogoStoragePath { get; set; }
    public string? LogoPublicUrl { get; set; }
    public int DefaultLowStockThreshold { get; set; } = 3;
    public Guid? UpdatedByEmployeeId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public StoreSettings()
    {
    }

    public StoreSettings(
        string storeName,
        string whatsappNumber,
        string storeDescription,
        string? logoStoragePath = null,
        string? logoPublicUrl = null,
        int defaultLowStockThreshold = 3,
        string? customSlug = null)
    {
        UpdateSettings(storeName, whatsappNumber, storeDescription, defaultLowStockThreshold, null, customSlug);
        SetLogo(logoStoragePath, logoPublicUrl, null);
    }

    public void UpdateSettings(
        string storeName,
        string whatsappNumber,
        string storeDescription,
        int defaultLowStockThreshold,
        Guid? employeeId,
        string? customSlug = null)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            throw new DomainException("O nome da loja é obrigatório.");

        if (string.IsNullOrWhiteSpace(storeDescription))
            throw new DomainException("A descrição da loja é obrigatória.");

        if (defaultLowStockThreshold < 0)
            throw new DomainException("O limite padrão de estoque baixo não pode ser negativo.");

        var sanitizedNumber = SanitizeWhatsappNumber(whatsappNumber);

        StoreName = storeName.Trim();
        StoreSlug = Category.GenerateSlug(string.IsNullOrWhiteSpace(customSlug) ? StoreName : customSlug);
        WhatsappNumber = sanitizedNumber;
        StoreDescription = storeDescription.Trim();
        DefaultLowStockThreshold = defaultLowStockThreshold;
        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetLogo(string? storagePath, string? publicUrl, Guid? employeeId)
    {
        LogoStoragePath = string.IsNullOrWhiteSpace(storagePath) ? null : storagePath.Trim();
        LogoPublicUrl = string.IsNullOrWhiteSpace(publicUrl) ? null : publicUrl.Trim();
        UpdatedByEmployeeId = employeeId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static string SanitizeWhatsappNumber(string rawNumber)
    {
        if (string.IsNullOrWhiteSpace(rawNumber))
            throw new DomainException("O número de WhatsApp é obrigatório.");

        var digitsOnly = Regex.Replace(rawNumber, @"\D", "");
        if (digitsOnly.Length < 10 || digitsOnly.Length > 15)
            throw new DomainException("Informe o número de WhatsApp completo com código do país (DDI) e DDD (ex.: 5511999999999).");

        return digitsOnly;
    }

    /// <summary>
    /// Gera o link público da loja com o nome/slug da loja para uso em QR Code fixo.
    /// </summary>
    public string BuildStoreCatalogUrl(string baseSiteUrl)
    {
        var cleanBase = string.IsNullOrWhiteSpace(baseSiteUrl) ? "http://localhost:5190" : baseSiteUrl.TrimEnd('/');
        var slug = string.IsNullOrWhiteSpace(StoreSlug) ? Category.GenerateSlug(StoreName) : StoreSlug;
        return $"{cleanBase}/loja/{slug}";
    }

    /// <summary>
    /// Gera o link oficial do WhatsApp no formato https://wa.me/NUMERO?text=MENSAGEM
    /// com a mensagem devidamente codificada para consulta de um produto do catálogo.
    /// </summary>
    public string BuildWhatsAppConsultationUrl(string productName, string? color = null, string? size = null)
    {
        var message = BuildWhatsAppConsultationMessage(productName, color, size);
        var encodedMessage = Uri.EscapeDataString(message);
        return $"https://wa.me/{WhatsappNumber}?text={encodedMessage}";
    }

    public static string BuildWhatsAppConsultationMessage(string productName, string? color = null, string? size = null)
    {
        var cleanProduct = string.IsNullOrWhiteSpace(productName) ? "Produto" : productName.Trim();

        if (!string.IsNullOrWhiteSpace(color) && !string.IsNullOrWhiteSpace(size))
        {
            return $"Olá! Tenho interesse no produto {cleanProduct}, na cor {color.Trim().ToLowerInvariant()} e tamanho {size.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        if (!string.IsNullOrWhiteSpace(color))
        {
            return $"Olá! Tenho interesse no produto {cleanProduct}, na cor {color.Trim().ToLowerInvariant()}. Gostaria de saber mais informações.";
        }

        if (!string.IsNullOrWhiteSpace(size))
        {
            return $"Olá! Tenho interesse no produto {cleanProduct}, no tamanho {size.Trim().ToUpperInvariant()}. Gostaria de saber mais informações.";
        }

        return $"Olá! Tenho interesse no produto {cleanProduct}. Gostaria de saber mais informações.";
    }
}
