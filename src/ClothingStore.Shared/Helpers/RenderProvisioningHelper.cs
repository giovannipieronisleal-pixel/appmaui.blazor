using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Resultado da busca ou criação automática de um serviço web do YouLink na conta Render do usuário via Render REST API v1.
/// Observação técnica: a API do Render exige uma Render API Key (Bearer rnd_...) gerada pelo usuário no painel do Render
/// (onde ele pode entrar normalmente com "Continue with Google"); tokens OAuth do Google não autenticam diretamente na API do Render.
/// </summary>
public sealed record RenderServiceProvisionResult(
    bool Success,
    string? ServiceUrl,
    string? ServiceId,
    string? ServiceName,
    string? GeneratedSyncSecret,
    bool CreatedNewService,
    string Message);

public static class RenderProvisioningHelper
{
    public const string DefaultRepositoryUrl = "https://github.com/giovannipieronisleal-pixel/yourlink";
    public const string DefaultRepositoryBranch = "master";
    public const string DefaultDockerfilePath = "./src/ClothingStore.Web/Dockerfile";
    public const string RenderApiKeysDashboardUrl = "https://dashboard.render.com/u/settings#api-keys";

    private static readonly Uri RenderApiBase = new("https://api.render.com/v1/");

    public static string SanitizeServiceSlug(string? storeNameOrSlug)
    {
        var raw = WhatsAppLinkBuilder.GenerateStoreSlug(storeNameOrSlug ?? "loja");
        raw = Regex.Replace(raw.ToLowerInvariant(), @"[^a-z0-9\-]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(raw))
            raw = "loja";

        var candidate = $"youlink-{raw}";
        return candidate.Length <= 42 ? candidate : candidate[..42].TrimEnd('-');
    }

    public static string? NormalizeRenderApiKey(string? rawApiKey)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
            return null;

        var trimmed = rawApiKey.Trim();
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["Bearer ".Length..].Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public static async Task<RenderServiceProvisionResult> DiscoverOrCreateYouLinkServiceAsync(
        string? rawApiKey,
        string? storeNameOrSlug,
        string? existingSyncSecret,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        var apiKey = NormalizeRenderApiKey(rawApiKey);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 10)
        {
            return new RenderServiceProvisionResult(
                false, null, null, null, null, false,
                "Cole uma Chave API válida do Render (começando com 'rnd_...') antes de continuar.");
        }

        try
        {
            var existing = await FindExistingYouLinkServiceAsync(apiKey, storeNameOrSlug, httpClient, ct);
            if (existing is not null)
                return existing;

            var ownerId = await GetPrimaryOwnerIdAsync(apiKey, httpClient, ct);
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                return new RenderServiceProvisionResult(
                    false, null, null, null, null, false,
                    "Não encontramos nenhum workspace ativo na sua conta Render ou a Chave API está sem permissão. Crie o site em 1 clique pelo botão Blueprint abaixo.");
            }

            var serviceName = $"{SanitizeServiceSlug(storeNameOrSlug)}-{Guid.NewGuid().ToString("N")[..4]}";
            var syncSecret = string.IsNullOrWhiteSpace(existingSyncSecret)
                ? Guid.NewGuid().ToString("N")
                : existingSyncSecret.Trim();

            var payload = new
            {
                type = "web_service",
                name = serviceName,
                ownerId,
                repo = DefaultRepositoryUrl,
                branch = DefaultRepositoryBranch,
                autoDeploy = "yes",
                serviceDetails = new
                {
                    env = "docker",
                    plan = "free",
                    dockerfilePath = DefaultDockerfilePath,
                    dockerContext = ".",
                    healthCheckPath = "/health",
                    envVars = new[]
                    {
                        new { key = "ASPNETCORE_ENVIRONMENT", value = "Production" },
                        new { key = "YOULINK_SYNC_SECRET", value = syncSecret }
                    }
                }
            };

            using var createReq = new HttpRequestMessage(HttpMethod.Post, new Uri(RenderApiBase, "services"))
            {
                Content = JsonContent.Create(payload)
            };
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            createReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var createResp = await httpClient.SendAsync(createReq, ct);
            var createBody = await createResp.Content.ReadAsStringAsync(ct);

            if (!createResp.IsSuccessStatusCode)
            {
                var errMsg = ExtractApiErrorMessage(createBody);
                return createResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? new RenderServiceProvisionResult(
                        false, null, null, null, null, false,
                        "Chave API do Render inválida ou sem permissão. Gere uma nova em dashboard.render.com/u/settings#api-keys.")
                    : new RenderServiceProvisionResult(
                        false, null, null, null, null, false,
                        $"O Render pediu o passo pelo Blueprint ({errMsg}). Use o botão 'Abrir Render (Blueprint)' ao lado — é só 1 clique lá no site!");
            }

            var parsed = ParseSingleServiceObject(createBody);
            var createdUrl = parsed.Url ?? $"https://{serviceName}.onrender.com";

            return new RenderServiceProvisionResult(
                true,
                createdUrl,
                parsed.Id,
                parsed.Name ?? serviceName,
                syncSecret,
                true,
                $"✓ Serviço '{parsed.Name ?? serviceName}' criado no Render em {createdUrl}! Aguarde cerca de 2–4 min enquanto o primeiro deploy termina e depois toque em Publicar.");
        }
        catch (OperationCanceledException)
        {
            return new RenderServiceProvisionResult(
                false, null, null, null, null, false,
                "Tempo esgotado ao falar com a API do Render. Tente novamente.");
        }
        catch (HttpRequestException ex)
        {
            return new RenderServiceProvisionResult(
                false, null, null, null, null, false,
                $"Sem conexão com a API do Render ({ex.Message}).");
        }
    }

    private static async Task<RenderServiceProvisionResult?> FindExistingYouLinkServiceAsync(
        string apiKey,
        string? storeNameOrSlug,
        HttpClient httpClient,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(RenderApiBase, "services?limit=20"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await httpClient.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new RenderServiceProvisionResult(
                false, null, null, null, null, false,
                "Chave API do Render recusada (HTTP 401/403). Confira se copiou a chave completa iniciada por 'rnd_'.");
        }

        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var preferredSlug = SanitizeServiceSlug(storeNameOrSlug);
        (string? Id, string? Name, string? Url, bool ExactSlugMatch, bool RepoMatch)? best = null;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var svcElem = item.TryGetProperty("service", out var nested) ? nested : item;
            var type = svcElem.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
            if (!string.Equals(type, "web_service", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "web", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parsed = ParseServiceElement(svcElem);
            if (string.IsNullOrWhiteSpace(parsed.Url))
                continue;

            var repo = svcElem.TryGetProperty("repo", out var rProp) ? rProp.GetString() ?? string.Empty : string.Empty;
            var repoMatch = repo.Contains("yourlink", StringComparison.OrdinalIgnoreCase) ||
                            (parsed.Name?.Contains("youlink", StringComparison.OrdinalIgnoreCase) ?? false);
            var exactSlugMatch = string.Equals(parsed.Name, preferredSlug, StringComparison.OrdinalIgnoreCase);

            if (!repoMatch && !exactSlugMatch)
                continue;

            if (best is null || (exactSlugMatch && !best.Value.ExactSlugMatch))
                best = (parsed.Id, parsed.Name, parsed.Url, exactSlugMatch, repoMatch);
        }

        if (best is null)
            return null;

        return new RenderServiceProvisionResult(
            true,
            best.Value.Url,
            best.Value.Id,
            best.Value.Name,
            null,
            false,
            $"✓ Encontramos seu site '{best.Value.Name}' na sua conta Render ({best.Value.Url}) e preenchemos o link automaticamente!");
    }

    private static async Task<string?> GetPrimaryOwnerIdAsync(
        string apiKey,
        HttpClient httpClient,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(RenderApiBase, "owners?limit=5"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var ownerElem = item.TryGetProperty("owner", out var nested) ? nested : item;
            if (ownerElem.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
        }

        return null;
    }

    private static (string? Id, string? Name, string? Url) ParseSingleServiceObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var svcElem = root.TryGetProperty("service", out var nested) ? nested : root;
        return ParseServiceElement(svcElem);
    }

    private static (string? Id, string? Name, string? Url) ParseServiceElement(JsonElement svcElem)
    {
        var id = svcElem.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var name = svcElem.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

        string? url = null;
        if (svcElem.TryGetProperty("serviceDetails", out var details) &&
            details.TryGetProperty("url", out var detailsUrl))
        {
            url = detailsUrl.GetString();
        }
        else if (svcElem.TryGetProperty("url", out var rootUrl))
        {
            url = rootUrl.GetString();
        }

        if (!string.IsNullOrWhiteSpace(url))
            url = url.Trim().TrimEnd('/');

        return (id, name, url);
    }

    private static string ExtractApiErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "erro desconhecido";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString() ?? body;
        }
        catch
        {
        }

        return body.Length > 140 ? body[..140] : body;
    }
}
