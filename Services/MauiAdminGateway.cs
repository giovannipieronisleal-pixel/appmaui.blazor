using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;

namespace appmaui.blazor.Services;

/// <summary>
/// Gateway administrativo para o aplicativo .NET MAUI.
/// Gerencia a sessão autenticada no SecureStorage do dispositivo, comunica-se com a ASP.NET Core Web API
/// e faz fallback instantâneo (sem bloqueio por timeout) para o banco de dados compartilhado quando a API não está rodando separadamente.
/// </summary>
public sealed class MauiAdminGateway
{
    private const string SessionStorageKey = "clothingstore_auth_session_v1";

    private readonly AdminApiClient _apiClient;
    private readonly AuthService _authService;
    private readonly ProductManagementService _productService;
    private readonly StockManagementService _stockService;
    private readonly EmployeeAndSettingsService _employeeAndSettingsService;

    private DateTimeOffset _skipRemoteApiUntil = DateTimeOffset.MinValue;

    public EmployeeProfileDto? CurrentEmployee { get; private set; }
    public string? AccessToken { get; private set; }
    public bool IsAuthenticated => CurrentEmployee is not null && CurrentEmployee.IsActive;
    public bool IsAdmin => CurrentEmployee?.IsAdmin ?? false;
    public bool CanManageProducts => IsAdmin || (CurrentEmployee?.CanManageProducts ?? false);
    public bool CanManageStock => IsAdmin || (CurrentEmployee?.CanManageStock ?? false);

    public event Action? AuthStateChanged;

    public MauiAdminGateway(
        AdminApiClient apiClient,
        AuthService authService,
        ProductManagementService productService,
        StockManagementService stockService,
        EmployeeAndSettingsService employeeAndSettingsService)
    {
        _apiClient = apiClient;
        _authService = authService;
        _productService = productService;
        _stockService = stockService;
        _employeeAndSettingsService = employeeAndSettingsService;
    }

    private bool ShouldTryRemoteApi => DateTimeOffset.UtcNow >= _skipRemoteApiUntil;

    private static bool IsConnectionOrTimeoutError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or TimeoutException;

    private async Task<T> ExecuteWithFallbackAsync<T>(
        Func<Task<T>> remoteCall,
        Func<Task<T>> localFallback)
    {
        if (ShouldTryRemoteApi)
        {
            try
            {
                return await remoteCall();
            }
            catch (Exception ex) when (IsConnectionOrTimeoutError(ex))
            {
                // Marca a API remota como offline por 60 segundos para evitar esperas de timeout a cada clique
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            }
        }

        return await localFallback();
    }

    private async Task ExecuteWithFallbackAsync(
        Func<Task> remoteCall,
        Func<Task> localFallback)
    {
        if (ShouldTryRemoteApi)
        {
            try
            {
                await remoteCall();
                return;
            }
            catch (Exception ex) when (IsConnectionOrTimeoutError(ex))
            {
                _skipRemoteApiUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            }
        }

        await localFallback();
    }

    public async Task TryRestoreSessionAsync()
    {
        if (IsAuthenticated)
            return;

        try
        {
            var rawJson = await SecureStorage.Default.GetAsync(SessionStorageKey);
            if (string.IsNullOrWhiteSpace(rawJson))
                return;

            var saved = JsonSerializer.Deserialize<LoginResponse>(rawJson);
            if (saved is null || saved.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                SecureStorage.Default.Remove(SessionStorageKey);
                return;
            }

            var freshProfile = await _authService.GetCurrentProfileAsync(saved.Employee.UserId);
            AccessToken = saved.AccessToken;
            CurrentEmployee = freshProfile;
            _apiClient.SetSession(saved.AccessToken, freshProfile);
            AuthStateChanged?.Invoke();
        }
        catch
        {
        }
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var response = await ExecuteWithFallbackAsync(
            () => _apiClient.LoginAsync(request),
            async () =>
            {
                var localResp = await _authService.LoginAsync(request);
                _apiClient.SetSession(localResp.AccessToken, localResp.Employee);
                return localResp;
            });

        AccessToken = response.AccessToken;
        CurrentEmployee = response.Employee;

        try
        {
            var serialized = JsonSerializer.Serialize(response);
            await SecureStorage.Default.SetAsync(SessionStorageKey, serialized);
        }
        catch
        {
        }

        AuthStateChanged?.Invoke();
        return response;
    }

    public void Logout()
    {
        AccessToken = null;
        CurrentEmployee = null;
        _apiClient.ClearSession();
        try
        {
            SecureStorage.Default.Remove(SessionStorageKey);
        }
        catch
        {
        }
        AuthStateChanged?.Invoke();
    }

    public Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.ForgotPasswordAsync(request),
            () => _authService.RequestPasswordResetAsync(request));

    public Task ResetPasswordAsync(ResetPasswordRequest request) =>
        ExecuteWithFallbackAsync(
            () => _apiClient.ResetPasswordAsync(request),
            () => _authService.ResetPasswordAsync(request));

    // Dashboard
    public Task<DashboardSummaryDto> GetDashboardSummaryAsync()
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetDashboardSummaryAsync(),
            () => _stockService.GetDashboardSummaryAsync());
    }

    // Categorias & Produtos
    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync()
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetCategoriesAsync(),
            () => _productService.GetAllCategoriesAsync());
    }

    public Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.CreateCategoryAsync(request),
            () => _productService.CreateCategoryAsync(request));
    }

    public Task<IReadOnlyList<ProductDetailDto>> GetProductsAsync(
        string? search = null,
        Guid? categoryId = null,
        bool includeArchived = false)
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetProductsAsync(search, categoryId, includeArchived),
            () => _productService.GetAdminProductsAsync(search, categoryId, includeArchived));
    }

    public Task<ProductDetailDto> GetProductByIdAsync(Guid productId)
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetProductByIdAsync(productId),
            () => _productService.GetProductByIdAsync(productId));
    }

    public Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.CreateProductAsync(request),
            () => _productService.CreateProductAsync(request));
    }

    public Task<ProductDetailDto> UpdateProductAsync(Guid productId, SaveProductRequest request)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UpdateProductAsync(productId, request),
            () => _productService.UpdateProductAsync(productId, request));
    }

    public Task<ProductDetailDto> ToggleProductVisibilityAsync(Guid productId, bool isVisibleOnSite)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.ToggleProductVisibilityAsync(productId, isVisibleOnSite),
            () => _productService.ToggleVisibilityAsync(productId, isVisibleOnSite));
    }

    public Task DeleteOrArchiveProductAsync(Guid productId)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.DeleteOrArchiveProductAsync(productId),
            () => _productService.DeleteOrArchiveProductAsync(productId));
    }

    // Imagens do Produto
    public Task<ProductImageDto> AddProductImageByUrlAsync(Guid productId, AddProductImageByUrlRequest request)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.AddProductImageByUrlAsync(productId, request),
            () => _productService.AddProductImageByUrlAsync(productId, request));
    }

    public Task<ProductImageDto> UploadProductImageFileAsync(
        Guid productId,
        string fileName,
        Stream fileStream,
        string contentType,
        string? altText,
        bool isPrimary)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UploadProductImageFileAsync(productId, fileName, fileStream, contentType, altText, isPrimary),
            () =>
            {
                if (fileStream.CanSeek)
                    fileStream.Position = 0;
                return _productService.UploadProductImageAsync(productId, fileName, fileStream, contentType, altText, isPrimary);
            });
    }

    public Task SetPrimaryProductImageAsync(Guid productId, Guid imageId)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.SetPrimaryProductImageAsync(productId, imageId),
            () => _productService.SetPrimaryProductImageAsync(productId, imageId));
    }

    public Task DeleteProductImageAsync(Guid productId, Guid imageId)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.DeleteProductImageAsync(productId, imageId),
            () => _productService.DeleteProductImageAsync(productId, imageId));
    }

    // Variantes
    public Task<ProductVariantDto> AddVariantAsync(Guid productId, SaveProductVariantRequest request)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.AddVariantAsync(productId, request),
            () => _productService.AddVariantAsync(productId, CurrentEmployee!.EmployeeId, request));
    }

    public Task<ProductVariantDto> UpdateVariantAsync(Guid variantId, SaveProductVariantRequest request)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UpdateVariantAsync(variantId, request),
            () => _productService.UpdateVariantAsync(variantId, request));
    }

    public Task DeleteVariantAsync(Guid variantId)
    {
        EnsureProductPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.DeleteVariantAsync(variantId),
            () => _productService.DeleteVariantAsync(variantId));
    }

    // Controle de Estoque e Movimentações
    public Task<StockMovementDto> RegisterStockMovementAsync(CreateStockMovementRequest request)
    {
        EnsureStockPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.RegisterStockMovementAsync(request),
            () => _stockService.RegisterMovementAsync(CurrentEmployee!.EmployeeId, request));
    }

    public Task<IReadOnlyList<StockMovementDto>> GetStockMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        int limit = 100)
    {
        EnsureAuthenticated();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetStockMovementsAsync(productId, variantId, limit),
            () => _stockService.GetMovementsAsync(productId, variantId, null, limit));
    }

    // Funcionários (AdminOnly)
    public Task<IReadOnlyList<EmployeeProfileDto>> GetEmployeesAsync()
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetEmployeesAsync(),
            () => _employeeAndSettingsService.GetEmployeesAsync());
    }

    public Task<EmployeeProfileDto> CreateEmployeeAsync(CreateEmployeeRequest request)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.CreateEmployeeAsync(request),
            () => _employeeAndSettingsService.CreateEmployeeAsync(request));
    }

    public Task<EmployeeProfileDto> UpdateEmployeeAsync(Guid employeeId, UpdateEmployeeRequest request)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UpdateEmployeeAsync(employeeId, request),
            () => _employeeAndSettingsService.UpdateEmployeeAsync(employeeId, request));
    }

    public Task<EmployeeProfileDto> SetEmployeeActiveStatusAsync(Guid employeeId, bool isActive)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.SetEmployeeActiveStatusAsync(employeeId, isActive),
            () => _employeeAndSettingsService.SetEmployeeActiveStatusAsync(employeeId, isActive));
    }

    // Configurações da Loja (AdminOnly)
    public Task<AdminStoreSettingsDto> GetStoreSettingsAsync()
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.GetStoreSettingsAsync(),
            () => _employeeAndSettingsService.GetAdminStoreSettingsAsync());
    }

    public Task<AdminStoreSettingsDto> UpdateStoreSettingsAsync(UpdateStoreSettingsRequest request)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UpdateStoreSettingsAsync(request),
            () => _employeeAndSettingsService.UpdateStoreSettingsAsync(CurrentEmployee!.EmployeeId, request));
    }

    public Task<AdminStoreSettingsDto> UploadStoreLogoAsync(string fileName, Stream fileStream, string contentType)
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.UploadStoreLogoAsync(fileName, fileStream, contentType),
            () =>
            {
                if (fileStream.CanSeek)
                    fileStream.Position = 0;
                return _employeeAndSettingsService.UploadStoreLogoAsync(CurrentEmployee!.EmployeeId, fileName, fileStream, contentType);
            });
    }

    public Task<AdminStoreSettingsDto> RemoveStoreLogoAsync()
    {
        EnsureAdminPermission();
        return ExecuteWithFallbackAsync(
            () => _apiClient.RemoveStoreLogoAsync(),
            () => _employeeAndSettingsService.RemoveStoreLogoAsync(CurrentEmployee!.EmployeeId));
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new DomainException("Sessão não autenticada. Faça login para continuar.");
    }

    private void EnsureProductPermission()
    {
        EnsureAuthenticated();
        if (!CanManageProducts)
            throw new DomainException("Seu perfil não possui permissão para cadastrar ou alterar produtos.");
    }

    private void EnsureStockPermission()
    {
        EnsureAuthenticated();
        if (!CanManageStock)
            throw new DomainException("Seu perfil não possui permissão para movimentar o estoque.");
    }

    private void EnsureAdminPermission()
    {
        EnsureAuthenticated();
        if (!IsAdmin)
            throw new DomainException("Acesso restrito ao perfil Administrador / Proprietário.");
    }
}
