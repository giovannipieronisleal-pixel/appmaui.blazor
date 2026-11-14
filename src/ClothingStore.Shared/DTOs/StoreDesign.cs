using System.Globalization;
using System.Text;

namespace ClothingStore.Shared.DTOs;

/// <summary>
/// Tokens visuais do catálogo digital e do aplicativo administrativo.
/// No Plano Básico utiliza o padrão Minimalista Luxo; no Plano Premium é moldável pelo cliente.
/// </summary>
public sealed class StoreDesignDto
{
    public string PresetKey { get; set; } = "minimalista-luxo";
    public string BackgroundColor { get; set; } = "#FAF8F5";
    public string SurfaceColor { get; set; } = "#FFFFFF";
    public string SurfaceSubtleColor { get; set; } = "#F3EFE8";
    public string PrimaryColor { get; set; } = "#18181B";
    public string PrimaryTextColor { get; set; } = "#FAF8F5";
    public string AccentColor { get; set; } = "#B69158";
    public string TextColor { get; set; } = "#18181B";
    public string MutedTextColor { get; set; } = "#57534E";
    public string BorderColor { get; set; } = "#E7E2DA";
    public string SidebarColor { get; set; } = "#141416";
    public string DisplayFont { get; set; } = "Playfair Display";
    public string BodyFont { get; set; } = "Plus Jakarta Sans";
    public string CornerRadiusMode { get; set; } = "ROUND_FOUR";
    public string DensityMode { get; set; } = "balanced";
    public string HeroLayout { get; set; } = "split";
    public int GridColumns { get; set; } = 3;
    public bool ShowLookbookBadge { get; set; } = true;
    public string? HeroEyebrow { get; set; }
    public string? HeroHeadline { get; set; }

    public static StoreDesignDto CreateMinimalistaLuxo() => new();

    public StoreDesignDto Clone() => new()
    {
        PresetKey = PresetKey,
        BackgroundColor = BackgroundColor,
        SurfaceColor = SurfaceColor,
        SurfaceSubtleColor = SurfaceSubtleColor,
        PrimaryColor = PrimaryColor,
        PrimaryTextColor = PrimaryTextColor,
        AccentColor = AccentColor,
        TextColor = TextColor,
        MutedTextColor = MutedTextColor,
        BorderColor = BorderColor,
        SidebarColor = SidebarColor,
        DisplayFont = DisplayFont,
        BodyFont = BodyFont,
        CornerRadiusMode = CornerRadiusMode,
        DensityMode = DensityMode,
        HeroLayout = HeroLayout,
        GridColumns = GridColumns,
        ShowLookbookBadge = ShowLookbookBadge,
        HeroEyebrow = HeroEyebrow,
        HeroHeadline = HeroHeadline
    };
}

public sealed record StoreDesignPresetOption(
    string Key,
    string Name,
    string Tagline,
    StoreDesignDto Design,
    string AudienceHint = "");

public static class StoreDesignPresets
{
    public const string PlanBasic = "Basico";
    public const string PlanIntermediate = "Intermediario";
    public const string PlanPremium = "Premium";

    public static string NormalizePlanTier(string? planTier)
    {
        if (string.IsNullOrWhiteSpace(planTier))
            return PlanBasic;

        var trimmed = planTier.Trim();
        if (string.Equals(trimmed, PlanPremium, StringComparison.OrdinalIgnoreCase))
            return PlanPremium;

        if (string.Equals(trimmed, PlanIntermediate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Intermediário", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "MeiaVersao", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Meia Versão", StringComparison.OrdinalIgnoreCase))
        {
            return PlanIntermediate;
        }

        return PlanBasic;
    }

    public static StoreDesignDto ResolveByPresetKey(string? presetKey)
    {
        var match = All.FirstOrDefault(p => string.Equals(p.Key, presetKey?.Trim(), StringComparison.OrdinalIgnoreCase));
        return (match ?? All[0]).Design.Clone();
    }

    public static IReadOnlyList<string> DisplayFonts { get; } =
    [
        "Playfair Display",
        "Cormorant Garamond",
        "Bodoni Moda",
        "Montserrat",
        "Space Grotesk"
    ];

    public static IReadOnlyList<string> BodyFonts { get; } =
    [
        "Plus Jakarta Sans",
        "Inter",
        "DM Sans",
        "Montserrat",
        "Work Sans"
    ];

    public static IReadOnlyList<StoreDesignPresetOption> All { get; } =
    [
        new(
            "minimalista-luxo",
            "Minimalista Luxo",
            "Alabastro quente, sépia e ouro champagne editorial.",
            StoreDesignDto.CreateMinimalistaLuxo()),
        new(
            "editorial-vogue",
            "Editorial Vogue",
            "Contraste agudo de passarela com cantos retos.",
            new StoreDesignDto
            {
                PresetKey = "editorial-vogue",
                BackgroundColor = "#F7F5F2",
                SurfaceColor = "#FFFFFF",
                SurfaceSubtleColor = "#EFECE6",
                PrimaryColor = "#0F0F10",
                PrimaryTextColor = "#FFFFFF",
                AccentColor = "#9A7B4F",
                TextColor = "#0F0F10",
                MutedTextColor = "#525252",
                BorderColor = "#DEDAD2",
                SidebarColor = "#0F0F10",
                DisplayFont = "Bodoni Moda",
                BodyFont = "Inter",
                CornerRadiusMode = "SHARP",
                DensityMode = "airy",
                HeroLayout = "centered",
                GridColumns = 3,
                ShowLookbookBadge = true
            }),
        new(
            "boutique-rose",
            "Boutique Rosé",
            "Curadoria suave em marfim rosado e bordô atemporal.",
            new StoreDesignDto
            {
                PresetKey = "boutique-rose",
                BackgroundColor = "#FDF8F6",
                SurfaceColor = "#FFFFFF",
                SurfaceSubtleColor = "#F7ECE8",
                PrimaryColor = "#3D2329",
                PrimaryTextColor = "#FDF8F6",
                AccentColor = "#C4847A",
                TextColor = "#2E1A1E",
                MutedTextColor = "#7A5B61",
                BorderColor = "#EBDAD5",
                SidebarColor = "#2B181C",
                DisplayFont = "Cormorant Garamond",
                BodyFont = "DM Sans",
                CornerRadiusMode = "ROUND_TWELVE",
                DensityMode = "airy",
                HeroLayout = "split",
                GridColumns = 3,
                ShowLookbookBadge = true
            }),
        new(
            "urban-sartorial",
            "Urban Sartorial",
            "Alfaiataria contemporânea com estética clean de galeria.",
            new StoreDesignDto
            {
                PresetKey = "urban-sartorial",
                BackgroundColor = "#F4F4F5",
                SurfaceColor = "#FFFFFF",
                SurfaceSubtleColor = "#E4E4E7",
                PrimaryColor = "#09090B",
                PrimaryTextColor = "#FAFAFA",
                AccentColor = "#52525B",
                TextColor = "#09090B",
                MutedTextColor = "#52525B",
                BorderColor = "#D4D4D8",
                SidebarColor = "#09090B",
                DisplayFont = "Space Grotesk",
                BodyFont = "Inter",
                CornerRadiusMode = "ROUND_FOUR",
                DensityMode = "compact",
                HeroLayout = "split",
                GridColumns = 4,
                ShowLookbookBadge = true
            }),
        new(
            "noir-noturno",
            "Noir Noturno",
            "Vitrine noturna em obsidiana profunda e dourado acetinado.",
            new StoreDesignDto
            {
                PresetKey = "noir-noturno",
                BackgroundColor = "#0E0E10",
                SurfaceColor = "#18181B",
                SurfaceSubtleColor = "#232327",
                PrimaryColor = "#E5C99B",
                PrimaryTextColor = "#111113",
                AccentColor = "#C8A267",
                TextColor = "#F5F2EB",
                MutedTextColor = "#A8A29E",
                BorderColor = "#2C2C32",
                SidebarColor = "#09090B",
                DisplayFont = "Playfair Display",
                BodyFont = "Plus Jakarta Sans",
                CornerRadiusMode = "ROUND_FOUR",
                DensityMode = "balanced",
                HeroLayout = "banner",
                GridColumns = 3,
                ShowLookbookBadge = true
            })
    ];
}

public static class StoreDesignCssHelper
{
    private static readonly HashSet<string> AllowedFontFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cormorant Garamond",
        "Playfair Display",
        "Montserrat",
        "Space Grotesk",
        "Plus Jakarta Sans",
        "Inter",
        "DM Sans",
        "Lora",
        "Poppins",
        "Outfit",
        "Manrope"
    };

    public static string BuildInlineStyle(StoreDesignDto? raw)
    {
        var d = Sanitize(raw);
        var (cardRadius, btnRadius, pillRadius) = ResolveRadii(d.CornerRadiusMode);
        var (cardPad, gridGap) = ResolveDensity(d.DensityMode);
        var displayStack = ResolveDisplayFontStack(d.DisplayFont);
        var bodyStack = ResolveBodyFontStack(d.BodyFont);
        var accentHover = ShiftHexBrightness(d.AccentColor, -18);
        var headerGlass = HexToRgba(d.SurfaceColor, 0.88);
        var cols = Math.Clamp(d.GridColumns, 2, 4);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"--brand-bg:{d.BackgroundColor};--brand-surface:{d.SurfaceColor};--brand-surface-subtle:{d.SurfaceSubtleColor};--brand-primary:{d.PrimaryColor};--brand-primary-fg:{d.PrimaryTextColor};--brand-accent:{d.AccentColor};--brand-accent-hover:{accentHover};--brand-text:{d.TextColor};--brand-text-muted:{d.MutedTextColor};--brand-border:{d.BorderColor};--brand-sidebar:{d.SidebarColor};--brand-header-glass:{headerGlass};--brand-font-display:{displayStack};--brand-font-body:{bodyStack};--brand-radius-card:{cardRadius};--brand-radius-btn:{btnRadius};--brand-radius-pill:{pillRadius};--brand-pad:{cardPad};--brand-gap:{gridGap};--brand-grid-cols:{cols};");
    }

    public static string BuildRootCssBlock(StoreDesignDto? raw) =>
        ":root{" + BuildInlineStyle(raw) + "}";

    public static string BuildGoogleFontsUrl(StoreDesignDto? raw)
    {
        var d = Sanitize(raw);
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            d.DisplayFont,
            d.BodyFont
        };

        var sb = new StringBuilder("https://fonts.googleapis.com/css2?");
        var first = true;
        foreach (var fam in families)
        {
            if (!first) sb.Append('&');
            first = false;
            var encoded = fam.Replace(' ', '+');
            sb.Append("family=").Append(encoded).Append(":wght@400;500;600;700");
        }
        sb.Append("&display=swap");
        return sb.ToString();
    }

    public static StoreDesignDto Sanitize(StoreDesignDto? raw)
    {
        var fallback = StoreDesignDto.CreateMinimalistaLuxo();
        if (raw is null) return fallback;

        return new StoreDesignDto
        {
            PresetKey = SafeIdentifier(raw.PresetKey, fallback.PresetKey, 48),
            BackgroundColor = SafeHex(raw.BackgroundColor, fallback.BackgroundColor),
            SurfaceColor = SafeHex(raw.SurfaceColor, fallback.SurfaceColor),
            SurfaceSubtleColor = SafeHex(raw.SurfaceSubtleColor, fallback.SurfaceSubtleColor),
            PrimaryColor = SafeHex(raw.PrimaryColor, fallback.PrimaryColor),
            PrimaryTextColor = SafeHex(raw.PrimaryTextColor, fallback.PrimaryTextColor),
            AccentColor = SafeHex(raw.AccentColor, fallback.AccentColor),
            TextColor = SafeHex(raw.TextColor, fallback.TextColor),
            MutedTextColor = SafeHex(raw.MutedTextColor, fallback.MutedTextColor),
            BorderColor = SafeHex(raw.BorderColor, fallback.BorderColor),
            SidebarColor = SafeHex(raw.SidebarColor, fallback.SidebarColor),
            DisplayFont = SafeFontFamily(raw.DisplayFont, fallback.DisplayFont),
            BodyFont = SafeFontFamily(raw.BodyFont, fallback.BodyFont),
            CornerRadiusMode = raw.CornerRadiusMode is "SHARP" or "ROUND_FOUR" or "ROUND_TWELVE" ? raw.CornerRadiusMode : fallback.CornerRadiusMode,
            DensityMode = raw.DensityMode is "compact" or "balanced" or "airy" ? raw.DensityMode : fallback.DensityMode,
            HeroLayout = raw.HeroLayout is "split" or "centered" or "banner" ? raw.HeroLayout : fallback.HeroLayout,
            GridColumns = Math.Clamp(raw.GridColumns, 2, 4),
            ShowLookbookBadge = raw.ShowLookbookBadge,
            HeroEyebrow = SafeTextLine(raw.HeroEyebrow, 80),
            HeroHeadline = SafeTextLine(raw.HeroHeadline, 140)
        };
    }

    private static string SafeFontFamily(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;

        var trimmed = candidate.Trim();
        return AllowedFontFamilies.TryGetValue(trimmed, out var canonical)
            ? canonical
            : fallback;
    }

    private static string SafeIdentifier(string? candidate, string fallback, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;

        var filtered = new string(candidate.Trim()
            .Where(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '-' or '_')
            .Take(maxLen)
            .ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? fallback : filtered;
    }

    private static string? SafeTextLine(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = new string(value.Trim()
            .Where(c => !char.IsControl(c))
            .Take(maxLen)
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static (string Card, string Btn, string Pill) ResolveRadii(string mode) => mode switch
    {
        "SHARP" => ("2px", "2px", "4px"),
        "ROUND_TWELVE" => ("16px", "12px", "999px"),
        _ => ("8px", "6px", "999px")
    };

    private static (string Pad, string Gap) ResolveDensity(string mode) => mode switch
    {
        "compact" => ("0.95rem", "1rem"),
        "airy" => ("1.75rem", "2rem"),
        _ => ("1.25rem", "1.45rem")
    };

    private static string ResolveDisplayFontStack(string font) => font switch
    {
        "Montserrat" => "'Montserrat', 'Plus Jakarta Sans', sans-serif",
        "Space Grotesk" => "'Space Grotesk', 'Inter', sans-serif",
        _ => $"'{font}', Georgia, 'Times New Roman', serif"
    };

    private static string ResolveBodyFontStack(string font) =>
        $"'{font}', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";

    private static string SafeHex(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return fallback;
        var s = candidate.Trim();
        if (s.Length != 7 || s[0] != '#') return fallback;
        for (var i = 1; i < 7; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return fallback;
        }
        return s.ToUpperInvariant();
    }

    private static string ShiftHexBrightness(string hex, int delta)
    {
        if (!TryParseRgb(hex, out var r, out var g, out var b))
            return hex;

        var nr = Math.Clamp(r + delta, 0, 255);
        var ng = Math.Clamp(g + delta, 0, 255);
        var nb = Math.Clamp(b + delta, 0, 255);
        return $"#{nr:X2}{ng:X2}{nb:X2}";
    }

    private static string HexToRgba(string hex, double alpha)
    {
        if (!TryParseRgb(hex, out var r, out var g, out var b))
            return hex;

        return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {alpha:0.##})");
    }

    private static bool TryParseRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var clean = SafeHex(hex, "");
        if (clean.Length != 7) return false;
        r = int.Parse(clean.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        g = int.Parse(clean.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        b = int.Parse(clean.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }
}
