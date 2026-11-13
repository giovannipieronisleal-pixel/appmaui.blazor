using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Gerador gratuito e determinístico de códigos Pix "Copia e Cola" no padrão oficial EMV / BACEN (BR Code)
/// com cálculo CRC-16/CCITT-FALSE (0x1021), sem depender de CNPJ ou intermediador pago.
/// </summary>
public static partial class PixPayloadBuilder
{
    private const string GuiPix = "BR.GOV.BCB.PIX";

    public static bool IsValidPixKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return false;

        var normalized = NormalizePixKey(rawKey);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 5 || normalized.Length > 77)
            return false;

        // Evita placeholder padrão não editado
        if (normalized.Contains("sua-conta", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("suachave", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // E-mail
        if (normalized.Contains('@'))
            return normalized.Contains('.') && !normalized.Contains(' ');

        // Chave aleatória (EVP UUID)
        if (UuidRegex().IsMatch(normalized))
            return true;

        // Telefone E.164 (+55...)
        if (normalized.StartsWith('+'))
            return DigitsOnlyRegex().IsMatch(normalized[1..]) && normalized.Length is >= 12 and <= 15;

        // CPF (11 dígitos) ou CNPJ (14 dígitos)
        if (DigitsOnlyRegex().IsMatch(normalized))
            return normalized.Length is 11 or 14;

        return false;
    }

    public static string DetectKeyTypeLabel(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return "Não informada";

        var normalized = NormalizePixKey(rawKey);
        if (normalized.Contains('@'))
            return "E-mail";
        if (UuidRegex().IsMatch(normalized))
            return "Chave Aleatória (EVP)";
        if (normalized.StartsWith('+'))
            return "Celular (+55)";
        if (DigitsOnlyRegex().IsMatch(normalized))
            return normalized.Length == 11 ? "CPF" : normalized.Length == 14 ? "CNPJ" : "Chave Numérica";

        return "Chave Pix";
    }

    public static string NormalizePixKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return string.Empty;

        var trimmed = rawKey.Trim();

        if (trimmed.Contains('@'))
            return trimmed.ToLowerInvariant();

        if (UuidRegex().IsMatch(trimmed))
            return trimmed.ToLowerInvariant();

        // Se o usuário digitou telefone com +, parênteses ou espaço com DDD
        if (trimmed.StartsWith('+') || trimmed.Contains('(') || trimmed.Contains(')'))
        {
            var digits = Regex.Replace(trimmed, @"\D", "");
            if (digits.Length is 10 or 11)
                digits = "55" + digits;
            return "+" + digits;
        }

        var onlyDigits = Regex.Replace(trimmed, @"\D", "");
        if (onlyDigits.Length == 11 || onlyDigits.Length == 14)
        {
            // Se tiver letras também (ex.: algo misto), retorna o trimmed
            if (trimmed.Any(char.IsLetter))
                return trimmed;
            return onlyDigits;
        }

        if (trimmed.Length is 10 or 11 && onlyDigits.Length == trimmed.Length)
            return "+55" + onlyDigits;

        return trimmed;
    }

    public static string BuildBrCode(
        string pixKey,
        string merchantName,
        string? merchantCity,
        decimal amount,
        string? txId = null)
    {
        var normalizedKey = NormalizePixKey(pixKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            throw new ArgumentException("A chave Pix da loja é obrigatória para gerar o código Pix.", nameof(pixKey));

        var cleanName = CleanAsciiUpper(merchantName, 25, "LOJA YOULINK");
        var cleanCity = CleanAsciiUpper(string.IsNullOrWhiteSpace(merchantCity) ? "BRASIL" : merchantCity, 15, "BRASIL");
        var cleanTxId = CleanTxId(txId);

        var guiField = FormatEmvField("00", GuiPix);
        var keyField = FormatEmvField("01", normalizedKey);
        var merchantAccountInfo = FormatEmvField("26", guiField + keyField);

        var sb = new StringBuilder();
        sb.Append(FormatEmvField("00", "01")); // Payload Format Indicator
        sb.Append(merchantAccountInfo);         // Merchant Account Information (Pix)
        sb.Append(FormatEmvField("52", "0000")); // Merchant Category Code
        sb.Append(FormatEmvField("53", "986"));  // BRL

        if (amount > 0)
        {
            var amountText = Math.Round(amount, 2).ToString("0.00", CultureInfo.InvariantCulture);
            sb.Append(FormatEmvField("54", amountText));
        }

        sb.Append(FormatEmvField("58", "BR"));
        sb.Append(FormatEmvField("59", cleanName));
        sb.Append(FormatEmvField("60", cleanCity));

        var txIdField = FormatEmvField("05", cleanTxId);
        sb.Append(FormatEmvField("62", txIdField));

        sb.Append("6304");
        var crc = ComputeCrc16Ccitt(sb.ToString());
        sb.Append(crc);
        return sb.ToString();
    }

    public static string ComputeCrc16Ccitt(string payloadWithCrcTag)
    {
        var bytes = Encoding.ASCII.GetBytes(payloadWithCrcTag);
        ushort crc = 0xFFFF;
        const ushort polynomial = 0x1021;

        foreach (var b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ polynomial);
                else
                    crc <<= 1;
            }
        }

        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }

    public static bool VerifyCrc16Ccitt(string? brCode)
    {
        if (string.IsNullOrWhiteSpace(brCode) || brCode.Length < 12)
            return false;

        var trimmed = brCode.Trim();
        var body = trimmed[..^4];
        var providedCrc = trimmed[^4..].ToUpperInvariant();
        if (!body.EndsWith("6304", StringComparison.Ordinal))
            return false;

        var expectedCrc = ComputeCrc16Ccitt(body);
        return string.Equals(providedCrc, expectedCrc, StringComparison.Ordinal);
    }

    private static string FormatEmvField(string id, string value) =>
        $"{id}{value.Length:D2}{value}";

    private static string CleanTxId(string? txId)
    {
        if (string.IsNullOrWhiteSpace(txId))
            return "***";

        var stripped = RemoveDiacritics(txId.Trim());
        var alnum = Regex.Replace(stripped, @"[^A-Za-z0-9]", "");
        if (string.IsNullOrWhiteSpace(alnum))
            return "***";

        return alnum.Length <= 25 ? alnum : alnum[..25];
    }

    private static string CleanAsciiUpper(string? raw, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var noAccents = RemoveDiacritics(raw.Trim());
        var asciiOnly = Regex.Replace(noAccents, @"[^A-Za-z0-9 .\-/]", " ");
        var collapsed = Regex.Replace(asciiOnly, @"\s+", " ").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(collapsed))
            return fallback;

        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength].TrimEnd();
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsOnlyRegex();
}
