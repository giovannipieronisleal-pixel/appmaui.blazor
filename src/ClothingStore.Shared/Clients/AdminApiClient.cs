using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Clients;

/// <summary>
/// Cliente HTTP administrativo para o aplicativo .NET MAUI gerenciar autenticação,
/// dashboard, produtos, variantes, estoque, funcionários e configurações da loja.
/// </summary>
public sealed class AdminApiClient
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private string? _accessToken;

    private static ByteArrayContent CreateBufferedJsonContent<T>(T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, WebJsonOptions);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private Task<HttpResponseMessage> PostBufferedJsonAsync<T>(string relativePath, T payload, CancellationToken ct) =>
        _httpClient.PostAsync(Endpoint(relativePath), CreateBufferedJsonContent(payload), ct);

    public EmployeeProfileDto? CurrentEmployee { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_accessToken) && CurrentEmployee is not null;

    public event Action? AuthStateChanged;

    public AdminApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void SetSession(string accessToken, EmployeeProfileDto employee)
    {
        _accessToken = accessToken;
        CurrentEmployee = employee;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        AuthStateChanged?.Invoke();
    }

    public void ClearSession()
    {
        _accessToken = null;
        CurrentEmployee = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
        AuthStateChanged?.Invoke();
    }

    // Autenticação e Entrada por E-mail
    public async Task<AuthSetupStatusDto> GetSetupStatusAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/auth/setup-status"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AuthSetupStatusDto>(cancellationToken: ct))!;
    }

    public async Task<LoginResponse> RegisterInitialAdminByEmailAsync(RegisterInitialAdminRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/auth/register-initial-admin"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var loginResult = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(loginResult.AccessToken, loginResult.Employee);
        return loginResult;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var response = await PostBufferedJsonAsync("/api/auth/login", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var loginResult = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(loginResult.AccessToken, loginResult.Employee);
        return loginResult;
    }

    public async Task<RequestEmailLoginCodeResponse> RequestEmailLoginCodeAsync(RequestEmailLoginCodeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/auth/email-code/request"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestEmailLoginCodeResponse>(cancellationToken: ct))!;
    }

    public async Task<LoginResponse> LoginWithEmailCodeAsync(VerifyEmailLoginCodeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/auth/email-code/verify"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var loginResult = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(loginResult.AccessToken, loginResult.Employee);
        return loginResult;
    }

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/auth/forgot-password"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>(cancellationToken: ct))!;
    }

    private Uri? _overrideBaseUri;

    public string? CurrentBaseAddress =>
        (_overrideBaseUri ?? _httpClient.BaseAddress)?.ToString().TrimEnd('/');

    public bool UpdateBaseAddress(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            _overrideBaseUri = null;
            return true;
        }

        var trimmed = rawUrl.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (!trimmed.EndsWith('/'))
            trimmed += "/";

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        _overrideBaseUri = parsed;
        return true;
    }

    private string Endpoint(string relativePath)
    {
        if (_overrideBaseUri is null)
            return relativePath;

        return new Uri(_overrideBaseUri, relativePath.TrimStart('/')).ToString();
    }

    public async Task<LoginResponse> RegisterUserAccountAsync(RegisterUserAccountRequest request, CancellationToken ct = default)
    {
        var response = await PostBufferedJsonAsync("/api/auth/register", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(login.AccessToken, login.Employee);
        return login;
    }

    public async Task<LoginChallengeResponse> LoginWithChallengeAsync(LoginRequest request, CancellationToken ct = default)
    {
        var response = await PostBufferedJsonAsync("/api/auth/login-challenge", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var challenge = (await response.Content.ReadFromJsonAsync<LoginChallengeResponse>(cancellationToken: ct))!;
        if (!challenge.RequiresTwoFactor && challenge.AuthResult is not null)
        {
            SetSession(challenge.AuthResult.AccessToken, challenge.AuthResult.Employee);
        }
        return challenge;
    }

    public async Task<LoginResponse> VerifyTwoFactorLoginAsync(VerifyTwoFactorLoginRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/auth/verify-2fa"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(login.AccessToken, login.Employee);
        return login;
    }

    public async Task<ApiCompatibilityStatusDto?> GetCompatibilityStatusAsync(string? clientAppVersion = null, CancellationToken ct = default)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(clientAppVersion)
                ? string.Empty
                : $"?clientVersion={Uri.EscapeDataString(clientAppVersion.Trim())}";
            var response = await _httpClient.GetAsync(Endpoint($"/api/app/compatibility{query}"), ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<ApiCompatibilityStatusDto>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Reachable, string NormalizedUrl, string? StoreName, string? ErrorMessage)> TestServerConnectionAsync(
        string? candidateUrl = null,
        CancellationToken ct = default)
    {
        var raw = string.IsNullOrWhiteSpace(candidateUrl) ? CurrentBaseAddress : candidateUrl.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return (false, string.Empty, null, "Informe o endereço do servidor.");

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        var normalized = raw.TrimEnd('/');
        if (!Uri.TryCreate(normalized + "/", UriKind.Absolute, out var targetBase))
            return (false, normalized, null, "URL inválida.");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var infoResp = await _httpClient.GetAsync(new Uri(targetBase, "api/public/store-info"), cts.Token);
            if (infoResp.IsSuccessStatusCode)
            {
                var info = await infoResp.Content.ReadFromJsonAsync<PublicStoreSettingsDto>(cancellationToken: cts.Token);
                return (true, normalized, info?.StoreName, null);
            }

            var healthResp = await _httpClient.GetAsync(new Uri(targetBase, "health"), cts.Token);
            if (healthResp.IsSuccessStatusCode)
                return (true, normalized, null, null);

            return (false, normalized, null, $"Servidor respondeu HTTP {(int)infoResp.StatusCode}.");
        }
        catch (OperationCanceledException)
        {
            return (false, normalized, null, "Tempo esgotado ao conectar ao servidor informado. Verifique o endereço ou tente novamente.");
        }
        catch (HttpRequestException ex)
        {
            return (false, normalized, null, $"Falha de rede ao conectar ao servidor: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, normalized, null, ex.Message);
        }
    }

    public async Task<PlatformAccountsSyncEnvelopeDto> GetPlatformAccountsSyncAsync(
        string? syncSecret = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint("/api/platform-sync/accounts"));
        if (!string.IsNullOrWhiteSpace(syncSecret))
            req.Headers.Add("X-YouLink-Sync-Key", syncSecret.Trim());

        var response = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlatformAccountsSyncEnvelopeDto>(cancellationToken: ct))!;
    }

    public async Task<SyncLocalAccountsResultDto> SyncLocalAccountsAsync(
        SyncLocalAccountsRequest request,
        string? syncSecret = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint("/api/platform-sync/sync-accounts"))
        {
            Content = CreateBufferedJsonContent(request)
        };
        if (!string.IsNullOrWhiteSpace(syncSecret))
            req.Headers.Add("X-YouLink-Sync-Key", syncSecret.Trim());

        var response = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SyncLocalAccountsResultDto>(cancellationToken: ct))!;
    }

    public async Task<AdminStoreSettingsDto> ApplyRemoteVipAsync(
        RemoteApplyVipRequest request,
        string? syncSecret = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint("/api/platform-sync/apply-vip"))
        {
            Content = CreateBufferedJsonContent(request)
        };
        if (!string.IsNullOrWhiteSpace(syncSecret))
            req.Headers.Add("X-YouLink-Sync-Key", syncSecret.Trim());

        var response = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    public async Task<RemoteToggleUserActiveResponse> ToggleRemoteUserActiveAsync(
        Guid userId,
        string? syncSecret = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint($"/api/platform-sync/toggle-user-active/{userId}"));
        if (!string.IsNullOrWhiteSpace(syncSecret))
            req.Headers.Add("X-YouLink-Sync-Key", syncSecret.Trim());

        var response = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RemoteToggleUserActiveResponse>(cancellationToken: ct))!;
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/auth/reset-password"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    public async Task<EmployeeProfileDto> RefreshProfileAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/auth/me"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var profile = (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
        CurrentEmployee = profile;
        AuthStateChanged?.Invoke();
        return profile;
    }

    // Dashboard
    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/dashboard/summary"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<DashboardSummaryDto>(cancellationToken: ct))!;
    }

    // Categorias & Produtos
    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/categories"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<CategoryDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/categories"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(cancellationToken: ct))!;
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid categoryId, SaveCategoryRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync(Endpoint($"/api/categories/{categoryId}"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(cancellationToken: ct))!;
    }

    public async Task<IReadOnlyList<ProductDetailDto>> GetProductsAsync(
        string? search = null,
        Guid? categoryId = null,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
            query.Add($"search={Uri.EscapeDataString(search)}");
        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
            query.Add($"categoryId={categoryId.Value}");
        if (includeArchived)
            query.Add("includeArchived=true");

        var url = query.Count == 0 ? "/api/products" : $"/api/products?{string.Join("&", query)}";
        var response = await _httpClient.GetAsync(Endpoint(url), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<ProductDetailDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<ProductDetailDto> GetProductByIdAsync(Guid productId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint($"/api/products/{productId}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/products"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task<ProductDetailDto> UpdateProductAsync(Guid productId, SaveProductRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync(Endpoint($"/api/products/{productId}"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task<ProductDetailDto> ToggleProductVisibilityAsync(Guid productId, bool isVisibleOnSite, CancellationToken ct = default)
    {
        var response = await _httpClient.PatchAsync(Endpoint($"/api/products/{productId}/visibility?isVisibleOnSite={isVisibleOnSite.ToString().ToLowerInvariant()}"), null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task DeleteOrArchiveProductAsync(Guid productId, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync(Endpoint($"/api/products/{productId}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    // Imagens do Produto
    public async Task<ProductImageDto> AddProductImageByUrlAsync(Guid productId, AddProductImageByUrlRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint($"/api/products/{productId}/images"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductImageDto>(cancellationToken: ct))!;
    }

    public async Task<ProductImageDto> UploadProductImageFileAsync(
        Guid productId,
        string fileName,
        Stream fileStream,
        string contentType,
        string? altText = null,
        bool isPrimary = false,
        CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);
        content.Add(streamContent, "file", fileName);
        if (!string.IsNullOrWhiteSpace(altText))
            content.Add(new StringContent(altText), "altText");
        content.Add(new StringContent(isPrimary.ToString()), "isPrimary");

        var response = await _httpClient.PostAsync(Endpoint($"/api/products/{productId}/images/upload"), content, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductImageDto>(cancellationToken: ct))!;
    }

    public async Task SetPrimaryProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var response = await _httpClient.PatchAsync(Endpoint($"/api/products/{productId}/images/{imageId}/primary"), null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    public async Task DeleteProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync(Endpoint($"/api/products/{productId}/images/{imageId}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    // Variantes
    public async Task<ProductVariantDto> AddVariantAsync(Guid productId, SaveProductVariantRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint($"/api/products/{productId}/variants"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductVariantDto>(cancellationToken: ct))!;
    }

    public async Task<ProductVariantDto> UpdateVariantAsync(Guid variantId, SaveProductVariantRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync(Endpoint($"/api/variants/{variantId}"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductVariantDto>(cancellationToken: ct))!;
    }

    public async Task DeleteVariantAsync(Guid variantId, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync(Endpoint($"/api/variants/{variantId}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    // Estoque e Movimentações
    public async Task<StockMovementDto> RegisterStockMovementAsync(CreateStockMovementRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/stock/movements"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StockMovementDto>(cancellationToken: ct))!;
    }

    public async Task<IReadOnlyList<StockMovementDto>> GetStockMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"limit={limit}" };
        if (productId.HasValue)
            query.Add($"productId={productId.Value}");
        if (variantId.HasValue)
            query.Add($"variantId={variantId.Value}");

        var response = await _httpClient.GetAsync(Endpoint($"/api/stock/movements?{string.Join("&", query)}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<StockMovementDto>>(cancellationToken: ct) ?? [];
    }

    // Funcionários (AdminOnly)
    public async Task<IReadOnlyList<EmployeeProfileDto>> GetEmployeesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/employees"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<EmployeeProfileDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<EmployeeProfileDto> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/employees"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
    }

    public async Task<EmployeeProfileDto> UpdateEmployeeAsync(Guid employeeId, UpdateEmployeeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync(Endpoint($"/api/employees/{employeeId}"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
    }

    public async Task<EmployeeProfileDto> SetEmployeeActiveStatusAsync(Guid employeeId, bool isActive, CancellationToken ct = default)
    {
        var response = await _httpClient.PatchAsync(Endpoint($"/api/employees/{employeeId}/status?isActive={isActive.ToString().ToLowerInvariant()}"), null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
    }

    // Configurações da Loja (AdminOnly)
    public async Task<AdminStoreSettingsDto> GetStoreSettingsAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/settings"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    public async Task<AdminStoreSettingsDto> UpdateStoreSettingsAsync(UpdateStoreSettingsRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync(Endpoint("/api/settings"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    public async Task<AdminStoreSettingsDto> UploadStoreLogoAsync(
        string fileName,
        Stream fileStream,
        string contentType,
        CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType);
        content.Add(streamContent, "file", fileName);

        var response = await _httpClient.PostAsync(Endpoint("/api/settings/logo"), content, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    public async Task<AdminStoreSettingsDto> RemoveStoreLogoAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync(Endpoint("/api/settings/logo"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    // Sincronização em Nuvem (Google Cloud Run / 24h Online)
    public async Task<bool> CheckCloudStatusAsync(string? customUrl = null, CancellationToken ct = default)
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(customUrl) ? Endpoint("/api/cloud/status") : $"{customUrl.TrimEnd('/')}/api/cloud/status";
            var response = await _httpClient.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task SyncCloudStateAsync(CloudCatalogSyncPayload payload, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/cloud/sync-state"), payload, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    public async Task<CheckDomainAvailabilityResponse> CheckDomainAvailabilityAsync(string domain, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/cloud/check-domain"), new CheckDomainAvailabilityRequest { Domain = domain }, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CheckDomainAvailabilityResponse>(cancellationToken: ct))!;
    }

    // Ativação de Plano via Código Gift Card
    public async Task<RedeemActivationCodeResponse> RedeemActivationCodeAsync(RedeemActivationCodeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/settings/redeem-code"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RedeemActivationCodeResponse>(cancellationToken: ct))!;
    }

    public async Task<GoogleAccountTierVerificationResult> VerifyGoogleAccountByTokenAsync(
        VerifyGoogleAccountTokenRequest request,
        CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/settings/verify-google-account"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<GoogleAccountTierVerificationResult>(cancellationToken: ct))!;
    }

    public async Task<AdminStoreSettingsDto> ClearGoogleAccountVerificationAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync(Endpoint("/api/settings/verify-google-account"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    // Pedidos Pix, CEP e Simulador de Correios
    public async Task<IReadOnlyList<StoreOrderDto>> GetOrdersAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/orders"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<StoreOrderDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<StoreOrderDto> ConfirmOrderPixAsync(Guid orderId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync(Endpoint($"/api/orders/{orderId}/confirm-pix"), null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StoreOrderDto>(cancellationToken: ct))!;
    }

    public async Task<StoreOrderDto> UpdateOrderFulfillmentAsync(Guid orderId, UpdateOrderFulfillmentRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync(Endpoint($"/api/orders/{orderId}/fulfillment"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StoreOrderDto>(cancellationToken: ct))!;
    }

    public async Task<StoreOrderDto> CancelOrderAsync(Guid orderId, string? reason = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(reason)
            ? Endpoint($"/api/orders/{orderId}/cancel")
            : Endpoint($"/api/orders/{orderId}/cancel?reason={Uri.EscapeDataString(reason)}");
        var response = await _httpClient.PostAsync(url, null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StoreOrderDto>(cancellationToken: ct))!;
    }

    public async Task<SalesReportDto> GetSalesReportAsync(string period = "30d", CancellationToken ct = default)
    {
        var qs = Uri.EscapeDataString(string.IsNullOrWhiteSpace(period) ? "30d" : period.Trim());
        var response = await _httpClient.GetAsync(Endpoint($"/api/orders/sales-report?period={qs}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SalesReportDto>(cancellationToken: ct))!;
    }

    public async Task<StoreOrderDto> RegisterManualSaleAsync(RegisterManualSaleRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(Endpoint("/api/orders/manual-sale"), request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StoreOrderDto>(cancellationToken: ct))!;
    }

    public async Task<CorreiosQuoteDto> SimulateCorreiosAsync(
        string? originCep,
        string destCep,
        int weightGrams = 450,
        int lengthCm = 24,
        int widthCm = 18,
        int heightCm = 8,
        CancellationToken ct = default)
    {
        var qs = $"destCep={Uri.EscapeDataString(destCep)}&weightGrams={weightGrams}&lengthCm={lengthCm}&widthCm={widthCm}&heightCm={heightCm}";
        if (!string.IsNullOrWhiteSpace(originCep))
            qs += $"&originCep={Uri.EscapeDataString(originCep)}";

        var response = await _httpClient.GetAsync(Endpoint($"/api/shipping/simulate-correios?{qs}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CorreiosQuoteDto>(cancellationToken: ct))!;
    }

    // Compra automática de planos via Pix (liberação da chave somente ao clicar em comprar + confirmação no Admin)
    public async Task<PlatformSubscriptionOrderDto> CreatePlatformPlanOrderAsync(
        CreatePlatformPlanOrderRequest request,
        CancellationToken ct = default)
    {
        var response = await PostBufferedJsonAsync("/api/platform-sync/plan-orders", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlatformSubscriptionOrderDto>(cancellationToken: ct))!;
    }

    public async Task<PlatformPlanOrderStatusDto> GetPlatformPlanOrderStatusAsync(
        string orderNumber,
        string claimToken,
        CancellationToken ct = default)
    {
        var qs = $"orderNumber={Uri.EscapeDataString(orderNumber)}&claimToken={Uri.EscapeDataString(claimToken)}";
        var response = await _httpClient.GetAsync(Endpoint($"/api/platform-sync/plan-orders/status?{qs}"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlatformPlanOrderStatusDto>(cancellationToken: ct))!;
    }

    public async Task<IReadOnlyList<PlatformSubscriptionOrderDto>> GetPlatformPlanOrdersAsync(
        string? syncSecret,
        CancellationToken ct = default) =>
        await SendSyncRequestAsync<List<PlatformSubscriptionOrderDto>>(HttpMethod.Get, "/api/platform-sync/plan-orders", syncSecret, content: null, ct);

    public Task<PlatformSubscriptionOrderDto> ApprovePlatformPlanOrderRemoteAsync(
        Guid orderId,
        string? syncSecret,
        string? adminName = null,
        CancellationToken ct = default)
    {
        var qs = string.IsNullOrWhiteSpace(adminName) ? string.Empty : $"?adminName={Uri.EscapeDataString(adminName)}";
        return SendSyncRequestAsync<PlatformSubscriptionOrderDto>(HttpMethod.Post, $"/api/platform-sync/plan-orders/{orderId}/approve{qs}", syncSecret, content: null, ct);
    }

    public Task<PlatformSubscriptionOrderDto> CancelPlatformPlanOrderRemoteAsync(
        Guid orderId,
        string? syncSecret,
        CancellationToken ct = default) =>
        SendSyncRequestAsync<PlatformSubscriptionOrderDto>(HttpMethod.Post, $"/api/platform-sync/plan-orders/{orderId}/cancel", syncSecret, content: null, ct);

    public Task<AdminStoreSettingsDto> PushOwnerPricingSyncAsync(
        UpdateOwnerPricingSyncRequest request,
        string? syncSecret,
        CancellationToken ct = default) =>
        SendSyncRequestAsync<AdminStoreSettingsDto>(HttpMethod.Post, "/api/platform-sync/owner-pricing", syncSecret, CreateBufferedJsonContent(request), ct);

    public async Task<PlatformGiftCardRedeemResponse> RedeemPlatformGiftCardAsync(
        PlatformGiftCardRedeemRequest request,
        CancellationToken ct = default)
    {
        var response = await PostBufferedJsonAsync("/api/platform-sync/gift-cards/redeem", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlatformGiftCardRedeemResponse>(cancellationToken: ct))!;
    }

    public async Task<IReadOnlyList<PlatformGiftCardSyncDto>> GetPlatformGiftCardsSyncAsync(
        string? syncSecret,
        CancellationToken ct = default) =>
        await SendSyncRequestAsync<List<PlatformGiftCardSyncDto>>(HttpMethod.Get, "/api/platform-sync/gift-cards", syncSecret, content: null, ct);

    public async Task<IReadOnlyList<PlatformGiftCardSyncDto>> SyncPlatformGiftCardsAsync(
        IReadOnlyList<PlatformGiftCardSyncDto> giftCards,
        string? syncSecret,
        CancellationToken ct = default) =>
        await SendSyncRequestAsync<List<PlatformGiftCardSyncDto>>(
            HttpMethod.Post,
            "/api/platform-sync/gift-cards/sync",
            syncSecret,
            CreateBufferedJsonContent(new SyncPlatformGiftCardsRequest(giftCards.ToList())),
            ct);

    public async Task<PlatformPlanCatalogDto> GetPlatformPlanCatalogAsync(
        CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(Endpoint("/api/platform-sync/plan-catalog"), ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlatformPlanCatalogDto>(cancellationToken: ct))!;
    }

    public Task<MercadoPagoOAuthStartResponse> StartPlatformMercadoPagoOAuthAsync(
        string? syncSecret,
        CancellationToken ct = default) =>
        SendSyncRequestAsync<MercadoPagoOAuthStartResponse>(
            HttpMethod.Post,
            "/api/platform-sync/mercadopago/oauth/start",
            syncSecret,
            content: null,
            ct);

    public async Task DisconnectPlatformMercadoPagoAsync(
        string? syncSecret,
        CancellationToken ct = default) =>
        await SendSyncRequestAsync<JsonElement>(
            HttpMethod.Post,
            "/api/platform-sync/mercadopago/disconnect",
            syncSecret,
            content: null,
            ct);

    private async Task<T> SendSyncRequestAsync<T>(
        HttpMethod method,
        string relativePath,
        string? syncSecret,
        HttpContent? content,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Endpoint(relativePath))
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(syncSecret))
            req.Headers.Add("X-YouLink-Sync-Key", syncSecret.Trim());

        var response = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var raw = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var errProp))
            {
                throw new InvalidOperationException(errProp.GetString() ?? "Erro na requisição.");
            }
        }
        catch (JsonException)
        {
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Sessão expirada ou não autenticada. Faça login novamente.");

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Você não possui permissão para realizar esta operação.");

        throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
    }

    public static bool IsEndpointUnavailableError(Exception ex) =>
        ex is InvalidOperationException inv &&
        (inv.Message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase) ||
         inv.Message.Contains("Endpoint não encontrado", StringComparison.OrdinalIgnoreCase) ||
         inv.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase));
}

