using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Verifica o nível real de uma conta Google via Google OAuth 2.0 + Google Drive About API (storageQuota.limit)
/// e Google Workspace Hosted Domain (hd).
/// Nota técnica transparente: o Google não oferece API pública de consumidor para ler o nome comercial "Google AI Pro"
/// nem inclui servidores Cloud Run dentro do Google One; por isso usamos a cota real do Drive (&gt; 15 GB / &gt;= 1 TB)
/// e o domínio Workspace (hd) como prova técnica legítima do nível da conta.
/// </summary>
public static class GoogleAccountTierVerifier
{
    public const string TierNone = "None";
    public const string TierFree15Gb = "GoogleGratuito15GB";
    public const string TierGoogleOneBasic = "GoogleOneBasico";
    public const string TierGoogleOneProOrHigher = "GoogleOneProOuSuperior";
    public const string TierWorkspace = "GoogleWorkspace";

    public const long GoogleFreeQuotaBytes = 16_106_127_360L; // 15 GB (15 * 1024^3)
    public const long GoogleOneProThresholdBytes = 1_099_511_627_776L; // 1 TB (1024^4)

    public const string HostingModePlatformShared = "PlatformShared";
    public const string HostingModeDedicatedOwnServer = "DedicatedOwnServer";

    public const string GoogleOAuthScope = "openid email profile https://www.googleapis.com/auth/drive.readonly";

    public sealed record PkceOAuthPair(string CodeVerifier, string CodeChallenge, string State);

    public sealed record PlanBenefitsBreakdown(
        decimal RegularMonthlyPrice,
        decimal FinalMonthlyPrice,
        decimal TotalMonthlyDiscount,
        decimal FreeDomainDiscount,
        decimal OwnServerDiscount,
        decimal GoogleTierDiscount);

    public static PkceOAuthPair CreatePkcePair()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var stateBytes = RandomNumberGenerator.GetBytes(16);

        var codeVerifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);
        var state = Base64UrlEncode(stateBytes);

        return new PkceOAuthPair(codeVerifier, codeChallenge, state);
    }

    public static string BuildAuthorizationUrl(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string state)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Informe o Google OAuth Client ID.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(redirectUri))
            throw new ArgumentException("Redirect URI inválido.", nameof(redirectUri));

        var query = string.Join("&",
            $"client_id={Uri.EscapeDataString(clientId.Trim())}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri.Trim())}",
            "response_type=code",
            $"scope={Uri.EscapeDataString(GoogleOAuthScope)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}",
            "access_type=online",
            "prompt=select_account%20consent");

        return $"https://accounts.google.com/o/oauth2/v2/auth?{query}";
    }

    public static async Task<string> ExchangeCodeForAccessTokenAsync(
        string clientId,
        string code,
        string codeVerifier,
        string redirectUri,
        HttpClient httpClient,
        string? clientSecret = null,
        CancellationToken ct = default)
    {
        var formFields = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["code"] = code.Trim(),
            ["code_verifier"] = codeVerifier.Trim(),
            ["redirect_uri"] = redirectUri.Trim(),
            ["grant_type"] = "authorization_code"
        };

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            formFields["client_secret"] = clientSecret.Trim();
        }

        using var content = new FormUrlEncodedContent(formFields);
        using var resp = await httpClient.PostAsync("https://oauth2.googleapis.com/token", content, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errMsg = ExtractGoogleError(raw) ?? $"HTTP {(int)resp.StatusCode}";
            throw new InvalidOperationException($"Falha ao trocar o código OAuth do Google por token: {errMsg}");
        }

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("access_token", out var tokenProp) ||
            string.IsNullOrWhiteSpace(tokenProp.GetString()))
        {
            throw new InvalidOperationException("O Google não retornou um access_token válido.");
        }

        return tokenProp.GetString()!;
    }

    public static async Task<GoogleAccountTierVerificationResult> VerifyWithAccessTokenAsync(
        string rawAccessToken,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        var token = (rawAccessToken ?? string.Empty).Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = token[7..].Trim();
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length < 12)
        {
            return new GoogleAccountTierVerificationResult(
                Success: false,
                Email: null,
                DisplayName: null,
                HostedDomain: null,
                DriveQuotaBytes: null,
                DriveUsageBytes: null,
                DetectedTier: TierNone,
                IsPaidGoogleAccount: false,
                QualifiesForDedicatedServerBenefit: false,
                QuotaSummary: "Não verificado",
                Message: "Cole um Access Token Google OAuth válido ou faça login pelo botão Google.",
                VerifiedAt: null);
        }

        using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var userResp = await httpClient.SendAsync(userReq, ct);
        var userRaw = await userResp.Content.ReadAsStringAsync(ct);

        if (!userResp.IsSuccessStatusCode)
        {
            var errDetail = ExtractGoogleError(userRaw) ?? $"HTTP {(int)userResp.StatusCode}";
            return new GoogleAccountTierVerificationResult(
                Success: false,
                Email: null,
                DisplayName: null,
                HostedDomain: null,
                DriveQuotaBytes: null,
                DriveUsageBytes: null,
                DetectedTier: TierNone,
                IsPaidGoogleAccount: false,
                QualifiesForDedicatedServerBenefit: false,
                QuotaSummary: "Token inválido ou expirado",
                Message: $"Não foi possível validar sua conta Google ({errDetail}). Faça login novamente ou gere um novo token.",
                VerifiedAt: null);
        }

        using var userDoc = JsonDocument.Parse(userRaw);
        var userRoot = userDoc.RootElement;
        var email = TryGetString(userRoot, "email");
        var displayName = TryGetString(userRoot, "name");
        var hostedDomain = TryGetString(userRoot, "hd");

        if (string.IsNullOrWhiteSpace(email))
        {
            return new GoogleAccountTierVerificationResult(
                Success: false,
                Email: null,
                DisplayName: displayName,
                HostedDomain: hostedDomain,
                DriveQuotaBytes: null,
                DriveUsageBytes: null,
                DetectedTier: TierNone,
                IsPaidGoogleAccount: false,
                QualifiesForDedicatedServerBenefit: false,
                QuotaSummary: "Escopo 'email' ausente",
                Message: "O token Google não incluiu permissão de leitura de e-mail (openid email).",
                VerifiedAt: null);
        }

        long? quotaBytes = null;
        long? usageBytes = null;
        var drivePermissionOk = true;

        using var driveReq = new HttpRequestMessage(
            HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about?fields=user,storageQuota");
        driveReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var driveResp = await httpClient.SendAsync(driveReq, ct);
        var driveRaw = await driveResp.Content.ReadAsStringAsync(ct);

        if (driveResp.IsSuccessStatusCode)
        {
            using var driveDoc = JsonDocument.Parse(driveRaw);
            if (driveDoc.RootElement.TryGetProperty("storageQuota", out var sq))
            {
                quotaBytes = TryGetLong(sq, "limit");
                usageBytes = TryGetLong(sq, "usage");
            }
        }
        else
        {
            drivePermissionOk = false;
        }

        if (!drivePermissionOk && string.IsNullOrWhiteSpace(hostedDomain))
        {
            return new GoogleAccountTierVerificationResult(
                Success: false,
                Email: email,
                DisplayName: displayName,
                HostedDomain: hostedDomain,
                DriveQuotaBytes: null,
                DriveUsageBytes: null,
                DetectedTier: TierNone,
                IsPaidGoogleAccount: false,
                QualifiesForDedicatedServerBenefit: false,
                QuotaSummary: "Sem leitura do Drive",
                Message: $"E-mail {email} autenticado, porém faltou o escopo 'https://www.googleapis.com/auth/drive.readonly' para verificar se a cota é maior que 15 GB.",
                VerifiedAt: null);
        }

        return ClassifyQuotaAndDomain(email, displayName, hostedDomain, quotaBytes, usageBytes, DateTimeOffset.UtcNow);
    }

    public static GoogleAccountTierVerificationResult ClassifyQuotaAndDomain(
        string email,
        string? displayName,
        string? hostedDomain,
        long? driveQuotaBytes,
        long? driveUsageBytes = null,
        DateTimeOffset? verifiedAt = null)
    {
        var timestamp = verifiedAt ?? DateTimeOffset.UtcNow;
        var cleanEmail = email.Trim();
        var cleanHd = string.IsNullOrWhiteSpace(hostedDomain) ? null : hostedDomain.Trim();
        var quotaSummary = FormatStorageQuota(driveQuotaBytes, !string.IsNullOrWhiteSpace(cleanHd));

        if (!string.IsNullOrWhiteSpace(cleanHd))
        {
            return new GoogleAccountTierVerificationResult(
                Success: true,
                Email: cleanEmail,
                DisplayName: displayName,
                HostedDomain: cleanHd,
                DriveQuotaBytes: driveQuotaBytes,
                DriveUsageBytes: driveUsageBytes,
                DetectedTier: TierWorkspace,
                IsPaidGoogleAccount: true,
                QualifiesForDedicatedServerBenefit: true,
                QuotaSummary: quotaSummary,
                Message: $"Conta Google Workspace ({cleanHd}) verificada para {cleanEmail}! Direito ao benefício de Servidor Próprio e desconto especial ativado.",
                VerifiedAt: timestamp);
        }

        if (driveQuotaBytes.HasValue && driveQuotaBytes.Value >= GoogleOneProThresholdBytes)
        {
            return new GoogleAccountTierVerificationResult(
                Success: true,
                Email: cleanEmail,
                DisplayName: displayName,
                HostedDomain: null,
                DriveQuotaBytes: driveQuotaBytes,
                DriveUsageBytes: driveUsageBytes,
                DetectedTier: TierGoogleOneProOrHigher,
                IsPaidGoogleAccount: true,
                QualifiesForDedicatedServerBenefit: true,
                QuotaSummary: quotaSummary,
                Message: $"Cota paga de {quotaSummary} detectada para {cleanEmail} (compatível com Google One Pro / AI Pro 2 TB). Benefício de Servidor Próprio + desconto mensal desbloqueados!",
                VerifiedAt: timestamp);
        }

        if (driveQuotaBytes.HasValue && driveQuotaBytes.Value > GoogleFreeQuotaBytes)
        {
            return new GoogleAccountTierVerificationResult(
                Success: true,
                Email: cleanEmail,
                DisplayName: displayName,
                HostedDomain: null,
                DriveQuotaBytes: driveQuotaBytes,
                DriveUsageBytes: driveUsageBytes,
                DetectedTier: TierGoogleOneBasic,
                IsPaidGoogleAccount: true,
                QualifiesForDedicatedServerBenefit: false,
                QuotaSummary: quotaSummary,
                Message: $"Conta Google One Básica detectada ({quotaSummary}) para {cleanEmail}. Contas pagas a partir de 1 TB (Google Pro/AI Pro) ou Workspace liberam o selo Pro automático; você também pode conectar seu servidor gratuito no Cloud Run / Render a qualquer momento.",
                VerifiedAt: timestamp);
        }

        return new GoogleAccountTierVerificationResult(
            Success: true,
            Email: cleanEmail,
            DisplayName: displayName,
            HostedDomain: null,
            DriveQuotaBytes: driveQuotaBytes,
            DriveUsageBytes: driveUsageBytes,
            DetectedTier: TierFree15Gb,
            IsPaidGoogleAccount: false,
            QualifiesForDedicatedServerBenefit: false,
            QuotaSummary: quotaSummary,
            Message: $"Conta Google gratuita ({quotaSummary}) verificada para {cleanEmail}. Ela não possui cota paga Google One Pro (1 TB+), mas você ainda pode ativar gratuitamente o modo Meu Servidor Próprio no Google Cloud Run ou Render.",
            VerifiedAt: timestamp);
    }

    public static bool DoesTierQualifyForDedicatedBenefit(string? tier) =>
        string.Equals(tier, TierGoogleOneProOrHigher, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tier, TierWorkspace, StringComparison.OrdinalIgnoreCase);

    public static string GetTierFriendlyLabel(string? tier) =>
        (tier ?? string.Empty).Trim() switch
        {
            var t when string.Equals(t, TierGoogleOneProOrHigher, StringComparison.OrdinalIgnoreCase) => "Google One Pro / AI Pro (1 TB+)",
            var t when string.Equals(t, TierWorkspace, StringComparison.OrdinalIgnoreCase) => "Google Workspace Empresarial",
            var t when string.Equals(t, TierGoogleOneBasic, StringComparison.OrdinalIgnoreCase) => "Google One Básico (> 15 GB)",
            var t when string.Equals(t, TierFree15Gb, StringComparison.OrdinalIgnoreCase) => "Google Gratuito (15 GB)",
            _ => "Não verificado"
        };

    public static PlanBenefitsBreakdown CalculatePlanBenefits(
        decimal regularMonthlyPrice,
        bool usesFreeDomain,
        string? googleAccountTier,
        bool usesDedicatedOwnServer)
    {
        if (regularMonthlyPrice <= 0m)
        {
            return new PlanBenefitsBreakdown(0m, 0m, 0m, 0m, 0m, 0m);
        }

        var freeDomainDiscount = 0m;
        if (usesFreeDomain)
        {
            var (_, domainSavings) = FreeDomainFinder.CalculateCheaperPlanWithFreeDomain(regularMonthlyPrice);
            freeDomainDiscount = domainSavings;
        }

        var ownServerDiscount = usesDedicatedOwnServer && regularMonthlyPrice > 29.90m
            ? 10.00m
            : (usesDedicatedOwnServer ? Math.Round(regularMonthlyPrice * 0.10m, 2, MidpointRounding.AwayFromZero) : 0m);

        var googleTierDiscount = DoesTierQualifyForDedicatedBenefit(googleAccountTier) && regularMonthlyPrice > 29.90m
            ? 5.00m
            : 0m;

        var floorPrice = regularMonthlyPrice > 24.90m ? 19.90m : 0m;
        var maxAllowedDiscount = Math.Max(0m, regularMonthlyPrice - floorPrice);
        var rawTotalDiscount = freeDomainDiscount + ownServerDiscount + googleTierDiscount;
        var effectiveDiscount = Math.Min(rawTotalDiscount, maxAllowedDiscount);
        var finalPrice = Math.Max(0m, Math.Round(regularMonthlyPrice - effectiveDiscount, 2, MidpointRounding.AwayFromZero));

        return new PlanBenefitsBreakdown(
            RegularMonthlyPrice: Math.Round(regularMonthlyPrice, 2, MidpointRounding.AwayFromZero),
            FinalMonthlyPrice: finalPrice,
            TotalMonthlyDiscount: Math.Round(effectiveDiscount, 2, MidpointRounding.AwayFromZero),
            FreeDomainDiscount: freeDomainDiscount,
            OwnServerDiscount: ownServerDiscount,
            GoogleTierDiscount: googleTierDiscount);
    }

    public static string FormatStorageQuota(long? bytes, bool hasWorkspaceDomain = false)
    {
        if (!bytes.HasValue)
            return hasWorkspaceDomain ? "Ilimitado / Pool Corporativo" : "15 GB (padrão)";

        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;
        const double tb = gb * 1024d;

        var value = (double)bytes.Value;
        if (value >= tb)
            return $"{(value / tb).ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))} TB";
        if (value >= gb)
            return $"{(value / gb).ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))} GB";
        return $"{(value / mb).ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"))} MB";
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string? TryGetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? TryGetLong(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
            return n;

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static string? ExtractGoogleError(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("error_description", out var desc) &&
                desc.ValueKind == JsonValueKind.String)
                return desc.GetString();

            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString();
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
            }
        }
        catch
        {
        }

        return null;
    }
}
