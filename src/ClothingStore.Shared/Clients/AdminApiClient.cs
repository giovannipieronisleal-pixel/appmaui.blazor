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
    private readonly HttpClient _httpClient;
    private string? _accessToken;

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
        var response = await _httpClient.GetAsync("/api/auth/setup-status", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AuthSetupStatusDto>(cancellationToken: ct))!;
    }

    public async Task<LoginResponse> RegisterInitialAdminByEmailAsync(RegisterInitialAdminRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/register-initial-admin", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var loginResult = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(loginResult.AccessToken, loginResult.Employee);
        return loginResult;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/login", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var loginResult = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(loginResult.AccessToken, loginResult.Employee);
        return loginResult;
    }

    public async Task<RequestEmailLoginCodeResponse> RequestEmailLoginCodeAsync(RequestEmailLoginCodeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/email-code/request", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestEmailLoginCodeResponse>(cancellationToken: ct))!;
    }

    public async Task<LoginResponse> LoginWithEmailCodeAsync(VerifyEmailLoginCodeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/email-code/verify", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var loginResult = (await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))!;
        SetSession(loginResult.AccessToken, loginResult.Employee);
        return loginResult;
    }

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/forgot-password", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>(cancellationToken: ct))!;
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/reset-password", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    public async Task<EmployeeProfileDto> RefreshProfileAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/api/auth/me", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        var profile = (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
        CurrentEmployee = profile;
        AuthStateChanged?.Invoke();
        return profile;
    }

    // Dashboard
    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/api/dashboard/summary", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<DashboardSummaryDto>(cancellationToken: ct))!;
    }

    // Categorias & Produtos
    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/api/categories", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<CategoryDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/categories", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(cancellationToken: ct))!;
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid categoryId, SaveCategoryRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/categories/{categoryId}", request, ct);
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
        var response = await _httpClient.GetAsync(url, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<ProductDetailDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<ProductDetailDto> GetProductByIdAsync(Guid productId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"/api/products/{productId}", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/products", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task<ProductDetailDto> UpdateProductAsync(Guid productId, SaveProductRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/products/{productId}", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task<ProductDetailDto> ToggleProductVisibilityAsync(Guid productId, bool isVisibleOnSite, CancellationToken ct = default)
    {
        var response = await _httpClient.PatchAsync($"/api/products/{productId}/visibility?isVisibleOnSite={isVisibleOnSite.ToString().ToLowerInvariant()}", null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(cancellationToken: ct))!;
    }

    public async Task DeleteOrArchiveProductAsync(Guid productId, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync($"/api/products/{productId}", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    // Imagens do Produto
    public async Task<ProductImageDto> AddProductImageByUrlAsync(Guid productId, AddProductImageByUrlRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/products/{productId}/images", request, ct);
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

        var response = await _httpClient.PostAsync($"/api/products/{productId}/images/upload", content, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductImageDto>(cancellationToken: ct))!;
    }

    public async Task SetPrimaryProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var response = await _httpClient.PatchAsync($"/api/products/{productId}/images/{imageId}/primary", null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    public async Task DeleteProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync($"/api/products/{productId}/images/{imageId}", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    // Variantes
    public async Task<ProductVariantDto> AddVariantAsync(Guid productId, SaveProductVariantRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/products/{productId}/variants", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductVariantDto>(cancellationToken: ct))!;
    }

    public async Task<ProductVariantDto> UpdateVariantAsync(Guid variantId, SaveProductVariantRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/variants/{variantId}", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProductVariantDto>(cancellationToken: ct))!;
    }

    public async Task DeleteVariantAsync(Guid variantId, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync($"/api/variants/{variantId}", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
    }

    // Estoque e Movimentações
    public async Task<StockMovementDto> RegisterStockMovementAsync(CreateStockMovementRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/stock/movements", request, ct);
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

        var response = await _httpClient.GetAsync($"/api/stock/movements?{string.Join("&", query)}", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<StockMovementDto>>(cancellationToken: ct) ?? [];
    }

    // Funcionários (AdminOnly)
    public async Task<IReadOnlyList<EmployeeProfileDto>> GetEmployeesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/api/employees", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<List<EmployeeProfileDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<EmployeeProfileDto> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/employees", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
    }

    public async Task<EmployeeProfileDto> UpdateEmployeeAsync(Guid employeeId, UpdateEmployeeRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/employees/{employeeId}", request, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
    }

    public async Task<EmployeeProfileDto> SetEmployeeActiveStatusAsync(Guid employeeId, bool isActive, CancellationToken ct = default)
    {
        var response = await _httpClient.PatchAsync($"/api/employees/{employeeId}/status?isActive={isActive.ToString().ToLowerInvariant()}", null, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EmployeeProfileDto>(cancellationToken: ct))!;
    }

    // Configurações da Loja (AdminOnly)
    public async Task<AdminStoreSettingsDto> GetStoreSettingsAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/api/settings", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    public async Task<AdminStoreSettingsDto> UpdateStoreSettingsAsync(UpdateStoreSettingsRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/settings", request, ct);
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

        var response = await _httpClient.PostAsync("/api/settings/logo", content, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    public async Task<AdminStoreSettingsDto> RemoveStoreLogoAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync("/api/settings/logo", ct);
        await EnsureSuccessOrThrowAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AdminStoreSettingsDto>(cancellationToken: ct))!;
    }

    // Sincronização em Nuvem (Google Cloud Run / 24h Online)
    public async Task<bool> CheckCloudStatusAsync(string? customUrl = null, CancellationToken ct = default)
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(customUrl) ? "/api/cloud/status" : $"{customUrl.TrimEnd('/')}/api/cloud/status";
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
        var response = await _httpClient.PostAsJsonAsync("/api/cloud/sync-state", payload, ct);
        await EnsureSuccessOrThrowAsync(response, ct);
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
}

