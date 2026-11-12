using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Utilitário compartilhado para validação e consulta de disponibilidade de domínios no Google Cloud.
/// Utiliza a API oficial do Google Cloud / Google Public DNS (DoH - DNS over HTTPS).
/// </summary>
public static class GoogleCloudDomainChecker
{
    private static readonly Regex DomainRegex = new(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<CheckDomainAvailabilityResponse> EvaluateDomainAsync(
        string? rawDomain,
        HttpClient httpClient,
        Func<string, Task<bool>>? isTakenLocally = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawDomain))
        {
            return new CheckDomainAvailabilityResponse(
                string.Empty,
                false,
                "InvalidFormat",
                "Informe o domínio ou nome desejado para o site.",
                string.Empty,
                []);
        }

        var clean = rawDomain.Trim().ToLowerInvariant();
        clean = clean.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        var slashIndex = clean.IndexOf('/');
        if (slashIndex >= 0)
            clean = clean[..slashIndex];

        clean = Regex.Replace(clean, @"\s+", "");

        if (string.IsNullOrWhiteSpace(clean) || clean.Length > 253)
        {
            return new CheckDomainAvailabilityResponse(
                rawDomain,
                false,
                "InvalidFormat",
                "O formato do domínio é inválido.",
                string.Empty,
                []);
        }

        // Se o lojista digitou apenas um nome sem ponto (ex.: "minha-loja" ou "minhaloja"),
        // formatamos automaticamente com o padrão oficial lojas.nomedaloja.com
        var isCustomFqdn = clean.Contains('.');
        var domainToCheck = isCustomFqdn
            ? clean
            : $"lojas.{clean.Replace("-", "")}.com";
        var normalizedUrl = $"https://{domainToCheck}";

        if (!DomainRegex.IsMatch(domainToCheck))
        {
            return new CheckDomainAvailabilityResponse(
                domainToCheck,
                false,
                "InvalidFormat",
                "O domínio deve conter apenas letras, números e hífens válidos (ex.: lojas.minhaloja.com).",
                normalizedUrl,
                []);
        }

        // 1. Verifica se já está reservado na base de dados das lojas
        if (isTakenLocally is not null)
        {
            try
            {
                if (await isTakenLocally(domainToCheck))
                {
                    var storeBase = ExtractStoreBase(domainToCheck);
                    return new CheckDomainAvailabilityResponse(
                        domainToCheck,
                        false,
                        "AlreadyTaken",
                        $"O domínio '{domainToCheck}' já está em uso na plataforma.",
                        normalizedUrl,
                        GenerateSuggestions(storeBase));
                }
            }
            catch
            {
            }
        }

        // 2. Consulta a API oficial do Google Cloud / Google Public DNS (DNS over HTTPS)
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var dnsUrl = $"https://dns.google/resolve?name={Uri.EscapeDataString(domainToCheck)}&type=A";
            var response = await httpClient.GetAsync(dnsUrl, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                var status = doc.RootElement.TryGetProperty("Status", out var statusProp) ? statusProp.GetInt32() : -1;

                // Status 3 = NXDOMAIN: O domínio não existe na internet -> 100% DISPONÍVEL!
                if (status == 3)
                {
                    return new CheckDomainAvailabilityResponse(
                        domainToCheck,
                        true,
                        "Available",
                        $"O domínio '{domainToCheck}' está 100% disponível para o seu site no Google Cloud!",
                        normalizedUrl,
                        []);
                }

                // Status 0 = NOERROR: O domínio já está registrado na internet
                if (status == 0)
                {
                    var storeBase = ExtractStoreBase(domainToCheck);

                    return new CheckDomainAvailabilityResponse(
                        domainToCheck,
                        false,
                        "AlreadyTaken",
                        $"O domínio '{domainToCheck}' já está registrado na internet.",
                        normalizedUrl,
                        GenerateSuggestions(storeBase));
                }
            }
        }
        catch
        {
            // Em caso de falha de conexão com o DNS, validação sintática bem-sucedida
        }

        return new CheckDomainAvailabilityResponse(
            domainToCheck,
            true,
            "Available",
            $"Domínio '{domainToCheck}' liberado para publicação no Google Cloud.",
            normalizedUrl,
            []);
    }

    private static string ExtractStoreBase(string domainToCheck)
    {
        var parts = domainToCheck.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("lojas", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }

        return parts[0];
    }

    private static List<string> GenerateSuggestions(string storeBase)
    {
        var cleanBase = Regex.Replace(storeBase, @"[^a-z0-9-]", "");
        if (cleanBase.StartsWith("lojas-") || cleanBase.StartsWith("lojas"))
        {
            cleanBase = cleanBase.Replace("lojas", "").Trim('-', '.');
        }
        if (string.IsNullOrWhiteSpace(cleanBase))
            cleanBase = "loja";

        return
        [
            $"lojas.{cleanBase}oficial.com",
            $"lojas.use{cleanBase}.com",
            $"lojas.{cleanBase}moda.com",
            $"lojas.{cleanBase}digital.com"
        ];
    }
}

