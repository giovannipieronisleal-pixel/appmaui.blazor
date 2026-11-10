namespace appmaui.blazor.Services;

/// <summary>
/// Gerencia os modos Claro (Light) e Escuro (Dark) no aplicativo Android / Windows,
/// persistindo a preferência do usuário e integrando com o tema nativo do sistema Android.
/// </summary>
public sealed class ThemeService
{
    private const string PreferenceKey = "clothingstore_app_theme_mode";

    public event Action? OnThemeChanged;

    /// <summary>
    /// Preferência selecionada: "system" (Automático do Android), "light" (Modo Claro) ou "dark" (Modo Escuro).
    /// </summary>
    public string ThemePreference { get; private set; } = "system";

    public ThemeService()
    {
        try
        {
            ThemePreference = Preferences.Default.Get(PreferenceKey, "system");
            ApplyMauiTheme(ThemePreference);
        }
        catch
        {
            ThemePreference = "system";
        }
    }

    /// <summary>
    /// Retorna verdadeiro se o tema efetivo atual for o Modo Escuro.
    /// </summary>
    public bool IsDarkMode
    {
        get
        {
            if (ThemePreference == "dark")
                return true;
            if (ThemePreference == "light")
                return false;

            // Se for "system", detecta pelo MAUI / Android
            return Application.Current?.RequestedTheme == AppTheme.Dark;
        }
    }

    /// <summary>
    /// Classe CSS aplicada no HTML do Blazor: "theme-dark", "theme-light" ou string vazia para detecção nativa por media query.
    /// </summary>
    public string CurrentThemeClass => ThemePreference switch
    {
        "dark" => "theme-dark",
        "light" => "theme-light",
        _ => "" // Modo sistema: utiliza @media (prefers-color-scheme: dark)
    };

    public string ThemeDisplayName => ThemePreference switch
    {
        "dark" => "Modo Escuro (OLED)",
        "light" => "Modo Claro",
        _ => "Automático (Sistema Android)"
    };

    public void SetThemePreference(string preference)
    {
        if (preference != "system" && preference != "light" && preference != "dark")
            preference = "system";

        ThemePreference = preference;

        try
        {
            Preferences.Default.Set(PreferenceKey, preference);
            ApplyMauiTheme(preference);
        }
        catch
        {
        }

        OnThemeChanged?.Invoke();
    }

    public void ToggleTheme()
    {
        if (IsDarkMode)
        {
            SetThemePreference("light");
        }
        else
        {
            SetThemePreference("dark");
        }
    }

    private static void ApplyMauiTheme(string preference)
    {
        if (Application.Current is null)
            return;

        Application.Current.UserAppTheme = preference switch
        {
            "dark" => AppTheme.Dark,
            "light" => AppTheme.Light,
            _ => AppTheme.Unspecified
        };
    }
}

