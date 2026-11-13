using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Buscador gratuito de domínios e subdomínios reais via Google Public DNS (DoH)
/// com cálculo automático de plano mais barato quando o usuário opta por um domínio gratuito.
/// </summary>
public static class FreeDomainFinder
{
    public const decimal DefaultMonthlyDiscountWithFreeDomain = 10.00m;

    private static readonly Regex InvalidSlugRegex = new(@"[^a-z0-9-]", RegexOptions.Compiled);
    private static readonly Regex MultiHyphenRegex = new(@"-{2,}", RegexOptions.Compiled);

    private sealed record FreeDomainProviderSpec(
        string Suffix,
        string ProviderName,
        string RegistrationMode,
        string? SetupUrl);

    private static readonly FreeDomainProviderSpec[] PublicFreeProviders =
    [
        new(".duckdns.org", "DuckDNS (Subdomínio + API Gratuita)", "DuckDnsApi", "https://www.duckdns.org"),
        new(".dpdns.org", "DigitalPlat (.dpdns.org Grátis)", "ExternalFreePanel", "https://dash.domain.digitalplat.org"),
        new(".pages.dev", "Cloudflare Pages (HTTPS Grátis)", "ExternalFreePanel", "https://dash.cloudflare.com"),
        new(".workers.dev", "Cloudflare Workers (HTTPS Grátis)", "ExternalFreePanel", "https://dash.cloudflare.com"),
        new(".fly.dev", "Fly.io Nuvem Gratuita (HTTPS)", "ExternalFreePanel", "https://fly.io")
    ];

    public static string NormalizeDomainSlug(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return "minhaloja";

        var cleaned = rawInput.Trim()
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

        var slashIdx = cleaned.IndexOf('/');
        if (slashIdx >= 0)
            cleaned = cleaned[..slashIdx];

        if (cleaned.StartsWith("lojas.", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[6..];

        var firstDot = cleaned.IndexOf('.');
        if (firstDot > 0)
            cleaned = cleaned[..firstDot];

        var decomposed = cleaned.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        }

        var ascii = sb.ToString().Normalize(NormalizationForm.FormC);
        ascii = InvalidSlugRegex.Replace(ascii, "-");
        ascii = MultiHyphenRegex.Replace(ascii, "-").Trim('-');

        if (ascii.Length < 3)
            ascii = (ascii + "loja").Trim('-');

        if (ascii.Length > 36)
            ascii = ascii[..36].Trim('-');

        return string.IsNullOrWhiteSpace(ascii) ? "minhaloja" : ascii;
    }

    /// <summary>
    /// Calcula o preço mensal mais barato quando o usuário/admin escolhe usar um domínio gratuito
    /// em vez de exigir compra/registro de domínio próprio pago.
    /// </summary>
    public static (decimal CheaperMonthlyPrice, decimal SavingsPerMonth) CalculateCheaperPlanWithFreeDomain(
        decimal regularMonthlyPrice)
    {
        if (regularMonthlyPrice <= 0m)
            return (0m, 0m);

        decimal discount;
        if (regularMonthlyPrice > 29.90m)
        {
            discount = Math.Min(DefaultMonthlyDiscountWithFreeDomain, regularMonthlyPrice - 29.90m);
        }
        else
        {
            discount = Math.Round(regularMonthlyPrice * 0.15m, 2, MidpointRounding.AwayFromZero);
        }

        discount = Math.Max(0m, Math.Round(discount, 2, MidpointRounding.AwayFromZero));
        var finalPrice = Math.Max(0m, Math.Round(regularMonthlyPrice - discount, 2, MidpointRounding.AwayFromZero));
        return (finalPrice, discount);
    }

    public static async Task<FreeDomainSearchResponse> SearchAvailableFreeDomainsAsync(
        string? storeNameOrKeyword,
        HttpClient httpClient,
        Func<string, Task<bool>>? isTakenLocally = null,
        string? ownerCustomRootDomain = null,
        CancellationToken ct = default)
    {
        var baseSlug = NormalizeDomainSlug(storeNameOrKeyword);
        var compactSlug = baseSlug.Replace("-", string.Empty);
        if (compactSlug.Length < 3)
            compactSlug = baseSlug;

        var checks = new List<Task<FreeDomainCandidateDto>>();

        // Se o dono possuir um domínio próprio com wildcard DNS na Cloudflare (opcional), sugere em 1º lugar
        if (!string.IsNullOrWhiteSpace(ownerCustomRootDomain))
        {
            var cleanRoot = ownerCustomRootDomain.Trim().ToLowerInvariant()
                .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim('.', '/');

            if (cleanRoot.Contains('.'))
            {
                var wildcardDomain = $"{compactSlug}.{cleanRoot}";
                checks.Add(CheckSingleCandidateAsync(
                    wildcardDomain,
                    "Seu Domínio Wildcard (R$ 0,00 por loja)",
                    "InstantIncluded",
                    null,
                    httpClient,
                    isTakenLocally,
                    treatDnsErrorAsAvailable: true,
                    ct));
            }
        }

        // Provedores gratuitos reais verificados ao vivo na internet via DNS público (DoH)
        foreach (var spec in PublicFreeProviders)
        {
            var slugForProvider = spec.Suffix == ".duckdns.org" || spec.Suffix == ".dpdns.org"
                ? compactSlug
                : baseSlug;

            var domain = $"{slugForProvider}{spec.Suffix}";
            checks.Add(CheckSingleCandidateAsync(
                domain,
                spec.ProviderName,
                spec.RegistrationMode,
                spec.SetupUrl,
                httpClient,
                isTakenLocally,
                treatDnsErrorAsAvailable: false,
                ct));
        }

        var results = (await Task.WhenAll(checks)).ToList();

        // Se todos os provedores públicos gratuitos estiverem ocupados na internet, gera variações extras gratuitas automaticamente
        if (!results.Any(r => r.IsAvailable && r.RegistrationMode != "InstantIncluded"))
        {
            var backupTasks = new[]
            {
                CheckSingleCandidateAsync(
                    $"{compactSlug}loja.duckdns.org",
                    "DuckDNS (Sugestão Alternativa)",
                    "DuckDnsApi",
                    "https://www.duckdns.org",
                    httpClient,
                    isTakenLocally,
                    treatDnsErrorAsAvailable: false,
                    ct),
                CheckSingleCandidateAsync(
                    $"use{compactSlug}.dpdns.org",
                    "DigitalPlat (Sugestão Alternativa)",
                    "ExternalFreePanel",
                    "https://dash.domain.digitalplat.org",
                    httpClient,
                    isTakenLocally,
                    treatDnsErrorAsAvailable: false,
                    ct),
                CheckSingleCandidateAsync(
                    $"{baseSlug}-loja.pages.dev",
                    "Cloudflare Pages (Sugestão Alternativa)",
                    "ExternalFreePanel",
                    "https://dash.cloudflare.com",
                    httpClient,
                    isTakenLocally,
                    treatDnsErrorAsAvailable: false,
                    ct)
            };

            results.AddRange(await Task.WhenAll(backupTasks));
        }

        // Endereço gratuito instantâneo incluso no próprio servidor YouLink (sempre disponível se não tomado localmente)
        var platformDomain = $"lojas.{compactSlug}.com";
        var platformTakenLocally = false;
        if (isTakenLocally is not null)
        {
            try
            {
                platformTakenLocally = await isTakenLocally(platformDomain);
            }
            catch
            {
            }
        }

        var instantCandidate = new FreeDomainCandidateDto(
            Domain: platformDomain,
            NormalizedUrl: $"https://{platformDomain}",
            ProviderName: "YouLink Endereço Incluso (Pronto na Hora)",
            RegistrationMode: "InstantIncluded",
            SetupUrl: null,
            IsAvailable: !platformTakenLocally,
            VerifiedLiveDns: true,
            ExtraDomainCostPerYear: 0m,
            MonthlyPlanDiscount: DefaultMonthlyDiscountWithFreeDomain,
            OfferHeadline: $"Olha! O endereço gratuito '{platformDomain}' está livre no servidor — deseja usar ele gratuitamente e ter um plano mais barato?",
            StatusMessage: platformTakenLocally
                ? "Já está em uso por outra loja na plataforma."
                : "Disponível gratuitamente na hora (incluso no servidor sem cadastro externo).");

        var ordered = results
            .OrderByDescending(r => r.IsAvailable)
            .ThenByDescending(r => r.VerifiedLiveDns)
            .ToList();

        ordered.Add(instantCandidate);

        var featured = ordered.FirstOrDefault(r => r.IsAvailable && r.VerifiedLiveDns)
                       ?? ordered.FirstOrDefault(r => r.IsAvailable);

        return new FreeDomainSearchResponse(baseSlug, featured, ordered);
    }

    private static async Task<FreeDomainCandidateDto> CheckSingleCandidateAsync(
        string domain,
        string providerName,
        string registrationMode,
        string? setupUrl,
        HttpClient httpClient,
        Func<string, Task<bool>>? isTakenLocally,
        bool treatDnsErrorAsAvailable,
        CancellationToken ct)
    {
        var normalizedUrl = $"https://{domain}";

        if (isTakenLocally is not null)
        {
            try
            {
                if (await isTakenLocally(domain))
                {
                    return new FreeDomainCandidateDto(
                        domain,
                        normalizedUrl,
                        providerName,
                        registrationMode,
                        setupUrl,
                        IsAvailable: false,
                        VerifiedLiveDns: true,
                        ExtraDomainCostPerYear: 0m,
                        MonthlyPlanDiscount: DefaultMonthlyDiscountWithFreeDomain,
                        OfferHeadline: $"O endereço '{domain}' já está reservado nesta plataforma.",
                        StatusMessage: "Já está em uso por outra loja aqui.");
                }
            }
            catch
            {
            }
        }

        var dnsStatus = await QueryDnsStatusWithFallbackAsync(domain, httpClient, ct);
        if (dnsStatus == 3)
        {
            return new FreeDomainCandidateDto(
                domain,
                normalizedUrl,
                providerName,
                registrationMode,
                setupUrl,
                IsAvailable: true,
                VerifiedLiveDns: true,
                ExtraDomainCostPerYear: 0m,
                MonthlyPlanDiscount: DefaultMonthlyDiscountWithFreeDomain,
                OfferHeadline: $"Olha! Este domínio gratuito está disponível: {domain} — deseja usar ele gratuitamente e ter um plano mais barato?",
                StatusMessage: "Verificado via DNS: livre para usar gratuitamente (R$ 0,00).");
        }

        if (dnsStatus == 0)
        {
            return new FreeDomainCandidateDto(
                domain,
                normalizedUrl,
                providerName,
                registrationMode,
                setupUrl,
                IsAvailable: false,
                VerifiedLiveDns: true,
                ExtraDomainCostPerYear: 0m,
                MonthlyPlanDiscount: DefaultMonthlyDiscountWithFreeDomain,
                OfferHeadline: $"O domínio '{domain}' já pertence a outro site.",
                StatusMessage: "Já registrado na internet. Tente uma sugestão abaixo.");
        }

        return new FreeDomainCandidateDto(
            domain,
            normalizedUrl,
            providerName,
            registrationMode,
            setupUrl,
            IsAvailable: treatDnsErrorAsAvailable,
            VerifiedLiveDns: false,
            ExtraDomainCostPerYear: 0m,
            MonthlyPlanDiscount: DefaultMonthlyDiscountWithFreeDomain,
            OfferHeadline: treatDnsErrorAsAvailable
                ? $"Olha! O endereço '{domain}' está liberado — deseja usar gratuitamente e ter um plano mais barato?"
                : $"Sem conexão DNS no momento para confirmar '{domain}' na internet.",
            StatusMessage: treatDnsErrorAsAvailable
                ? "Disponível no modo local/instantâneo."
                : "Offline: conecte à internet para verificar na hora.");
    }

    private static async Task<int> QueryDnsStatusWithFallbackAsync(
        string domain,
        HttpClient httpClient,
        CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(domain);
        var endpoints = new (string Url, bool AcceptDnsJson)[]
        {
            ($"https://dns.google/resolve?name={encoded}&type=A", false),
            ($"https://cloudflare-dns.com/dns-query?name={encoded}&type=A", true)
        };

        foreach (var (url, acceptDnsJson) in endpoints)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (acceptDnsJson)
                    req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");

                using var response = await httpClient.SendAsync(req, cts.Token);
                if (!response.IsSuccessStatusCode)
                    continue;

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Status", out var statusProp) &&
                    statusProp.ValueKind == JsonValueKind.Number &&
                    statusProp.TryGetInt32(out var status) &&
                    status is 0 or 3)
                {
                    return status;
                }
            }
            catch
            {
                // Tenta o próximo provedor DoH público antes de marcar como indisponível offline.
            }
        }

        return -1;
    }

    /// <summary>
    /// Sincroniza um subdomínio gratuito .duckdns.org via API oficial gratuita do DuckDNS usando o token da conta.
    /// </summary>
    public static async Task<(bool Success, string Message)> UpdateDuckDnsIpAsync(
        string subdomainOrDomain,
        string token,
        string? ipv4,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subdomainOrDomain))
            return (false, "Informe o subdomínio DuckDNS (ex.: minhaloja.duckdns.org).");

        if (string.IsNullOrWhiteSpace(token))
            return (false, "Informe o Token gratuito gerado no site duckdns.org.");

        var cleanSub = subdomainOrDomain.Trim().ToLowerInvariant()
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('/');

        var slashIdx = cleanSub.IndexOf('/');
        if (slashIdx >= 0)
            cleanSub = cleanSub[..slashIdx];

        if (cleanSub.EndsWith(".duckdns.org", StringComparison.OrdinalIgnoreCase))
            cleanSub = cleanSub[..^".duckdns.org".Length];

        cleanSub = NormalizeDomainSlug(cleanSub);

        var ipParam = string.IsNullOrWhiteSpace(ipv4) ? string.Empty : $"&ip={Uri.EscapeDataString(ipv4.Trim())}";
        var url = $"https://www.duckdns.org/update?domains={Uri.EscapeDataString(cleanSub)}&token={Uri.EscapeDataString(token.Trim())}{ipParam}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var resp = await httpClient.GetAsync(url, cts.Token);
            var body = (await resp.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (resp.IsSuccessStatusCode && string.Equals(body, "OK", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"Subdomínio '{cleanSub}.duckdns.org' sincronizado com sucesso via API DuckDNS!");
            }

            return (false, $"O DuckDNS retornou '{(string.IsNullOrEmpty(body) ? resp.StatusCode.ToString() : body)}'. Confira se o domínio foi adicionado na sua conta gratuita em duckdns.org e se o token está correto.");
        }
        catch (Exception ex)
        {
            return (false, $"Não foi possível conectar à API do DuckDNS agora: {ex.Message}");
        }
    }
}
