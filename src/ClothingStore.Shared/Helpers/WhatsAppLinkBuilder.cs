using System.Text.RegularExpressions;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Utilitário compartilhado entre o site Blazor e o aplicativo para construção do link oficial do WhatsApp
/// no formato https://wa.me/NUMERO?text=MENSAGEM com codificação segura de URL.
/// </summary>
public static class WhatsAppLinkBuilder
{
    public static string SanitizePhoneNumber(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone))
            return string.Empty;

        return Regex.Replace(rawPhone, @"\D", "");
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
}
