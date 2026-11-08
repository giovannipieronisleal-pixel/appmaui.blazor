using System.Text.Json;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;

namespace appmaui.blazor.Services;

/// <summary>
/// Gateway administrativo para o aplicativo .NET MAUI.
/// Gerencia a sessão autenticada no SecureStorage do dispositivo, comunica-se com a ASP.NET Core Web API
/// e garante sincronização contínua com o banco de dados compartilhado.
/// </summary>
public sealed class MauiAdminGateway
{
    private const string SessionStorageKey = "clothingstore_auth_session_v1";

    private readonly AdminApiClient _apiClient;
    private readonly AuthService _authService;
    private readonly ProductManagementService _productService;
    private readonly StockManagementService _stockService;
    private readonly EmployeeAndSettingsService _employeeAndSettingsService;

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
            // Se o SecureStorage não estiver disponível ou a conta foi desativada, exige novo login.
        }
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        LoginResponse response;
        try
        {
            response = await _apiClient.LoginAsync(request);
        }
        catch (HttpRequestException)
        {
            response = await _authService.LoginAsync(request);
            _apiClient.SetSession(response.AccessToken, response.Employee);
        }

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

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        try
        {
            return await _apiClient.ForgotPasswordAsync(request);
        }
        catch (HttpRequestException)
        {
            return await _authService.RequestPasswordResetAsync(request);
        }
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        try
        {
            await _apiClient.ResetPasswordAsync(request);
        }
        catch (HttpRequestException)
        {
            await _authService.ResetPasswordAsync(request);
        }
    }

    // Dashboard
    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync()
    {
        EnsureAuthenticated();
        try
        {
            return await _apiClient.GetDashboardSummaryAsync();
        }
        catch (HttpRequestException)
        {
            return await _stockService.GetDashboardSummaryAsync();
        }
    }

    // Categorias & Produtos
    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync()
    {
        EnsureAuthenticated();
        try
        {
            return await _apiClient.GetCategoriesAsync();
        }
        catch (HttpRequestException)
        {
            return await _productService.GetAllCategoriesAsync();
        }
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.CreateCategoryAsync(request);
        }
        catch (HttpRequestException)
        {
            return await _productService.CreateCategoryAsync(request);
        }
    }

    public async Task<IReadOnlyList<ProductDetailDto>> GetProductsAsync(
        string? search = null,
        Guid? categoryId = null,
        bool includeArchived = false)
    {
        EnsureAuthenticated();
        try
        {
            return await _apiClient.GetProductsAsync(search, categoryId, includeArchived);
        }
        catch (HttpRequestException)
        {
            return await _productService.GetAdminProductsAsync(search, categoryId, includeArchived);
        }
    }

    public async Task<ProductDetailDto> GetProductByIdAsync(Guid productId)
    {
        EnsureAuthenticated();
        try
        {
            return await _apiClient.GetProductByIdAsync(productId);
        }
        catch (HttpRequestException)
        {
            return await _productService.GetProductByIdAsync(productId);
        }
    }

    public async Task<ProductDetailDto> CreateProductAsync(SaveProductRequest request)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.CreateProductAsync(request);
        }
        catch (HttpRequestException)
        {
            return await _productService.CreateProductAsync(request);
        }
    }

    public async Task<ProductDetailDto> UpdateProductAsync(Guid productId, SaveProductRequest request)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.UpdateProductAsync(productId, request);
        }
        catch (HttpRequestException)
        {
            return await _productService.UpdateProductAsync(productId, request);
        }
    }

    public async Task<ProductDetailDto> ToggleProductVisibilityAsync(Guid productId, bool isVisibleOnSite)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.ToggleProductVisibilityAsync(productId, isVisibleOnSite);
        }
        catch (HttpRequestException)
        {
            return await _productService.ToggleVisibilityAsync(productId, isVisibleOnSite);
        }
    }

    public async Task DeleteOrArchiveProductAsync(Guid productId)
    {
        EnsureProductPermission();
        try
        {
            await _apiClient.DeleteOrArchiveProductAsync(productId);
        }
        catch (HttpRequestException)
        {
            await _productService.DeleteOrArchiveProductAsync(productId);
        }
    }

    // Imagens do Produto
    public async Task<ProductImageDto> AddProductImageByUrlAsync(Guid productId, AddProductImageByUrlRequest request)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.AddProductImageByUrlAsync(productId, request);
        }
        catch (HttpRequestException)
        {
            return await _productService.AddProductImageByUrlAsync(productId, request);
        }
    }

    public async Task<ProductImageDto> UploadProductImageFileAsync(
        Guid productId,
        string fileName,
        Stream fileStream,
        string contentType,
        string? altText,
        bool isPrimary)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.UploadProductImageFileAsync(productId, fileName, fileStream, contentType, altText, isPrimary);
        }
        catch (HttpRequestException)
        {
            if (fileStream.CanSeek)
                fileStream.Position = 0;
            return await _productService.UploadProductImageAsync(productId, fileName, fileStream, contentType, altText, isPrimary);
        }
    }

    public async Task SetPrimaryProductImageAsync(Guid productId, Guid imageId)
    {
        EnsureProductPermission();
        try
        {
            await _apiClient.SetPrimaryProductImageAsync(productId, imageId);
        }
        catch (HttpRequestException)
        {
            await _productService.SetPrimaryProductImageAsync(productId, imageId);
        }
    }

    public async Task DeleteProductImageAsync(Guid productId, Guid imageId)
    {
        EnsureProductPermission();
        try
        {
            await _apiClient.DeleteProductImageAsync(productId, imageId);
        }
        catch (HttpRequestException)
        {
            await _productService.DeleteProductImageAsync(productId, imageId);
        }
    }

    // Variantes
    public async Task<ProductVariantDto> AddVariantAsync(Guid productId, SaveProductVariantRequest request)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.AddVariantAsync(productId, request);
        }
        catch (HttpRequestException)
        {
            return await _productService.AddVariantAsync(productId, CurrentEmployee!.EmployeeId, request);
        }
    }

    public async Task<ProductVariantDto> UpdateVariantAsync(Guid variantId, SaveProductVariantRequest request)
    {
        EnsureProductPermission();
        try
        {
            return await _apiClient.UpdateVariantAsync(variantId, request);
        }
        catch (HttpRequestException)
        {
            return await _productService.UpdateVariantAsync(variantId, request);
        }
    }

    public async Task DeleteVariantAsync(Guid variantId)
    {
        EnsureProductPermission();
        try
        {
            await _apiClient.DeleteVariantAsync(variantId);
        }
        catch (HttpRequestException)
        {
            await _productService.DeleteVariantAsync(variantId);
        }
    }

    // Controle de Estoque e Movimentações
    public async Task<StockMovementDto> RegisterStockMovementAsync(CreateStockMovementRequest request)
    {
        EnsureStockPermission();
        try
        {
            return await _apiClient.RegisterStockMovementAsync(request);
        }
        catch (HttpRequestException)
        {
            return await _stockService.RegisterMovementAsync(CurrentEmployee!.EmployeeId, request);
        }
    }

    public async Task<IReadOnlyList<StockMovementDto>> GetStockMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        int limit = 100)
    {
        EnsureAuthenticated();
        try
        {
            return await _apiClient.GetStockMovementsAsync(productId, variantId, limit);
        }
        catch (HttpRequestException)
        {
            return await _stockService.GetMovementsAsync(productId, variantId, null, limit);
        }
    }

    // Funcionários (AdminOnly)
    public async Task<IReadOnlyList<EmployeeProfileDto>> GetEmployeesAsync()
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.GetEmployeesAsync();
        }
        catch (HttpRequestException)
        {
            return await _employeeAndSettingsService.GetEmployeesAsync();
        }
    }

    public async Task<EmployeeProfileDto> CreateEmployeeAsync(CreateEmployeeRequest request)
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.CreateEmployeeAsync(request);
        }
        catch (HttpRequestException)
        {
            return await _employeeAndSettingsService.CreateEmployeeAsync(request);
        }
    }

    public async Task<EmployeeProfileDto> UpdateEmployeeAsync(Guid employeeId, UpdateEmployeeRequest request)
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.UpdateEmployeeAsync(employeeId, request);
        }
        catch (HttpRequestException)
        {
            return await _employeeAndSettingsService.UpdateEmployeeAsync(employeeId, request);
        }
    }

    public async Task<EmployeeProfileDto> SetEmployeeActiveStatusAsync(Guid employeeId, bool isActive)
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.SetEmployeeActiveStatusAsync(employeeId, isActive);
        }
        catch (HttpRequestException)
        {
            return await _employeeAndSettingsService.SetEmployeeActiveStatusAsync(employeeId, isActive);
        }
    }

    // Configurações da Loja (AdminOnly)
    public async Task<AdminStoreSettingsDto> GetStoreSettingsAsync()
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.GetStoreSettingsAsync();
        }
        catch (HttpRequestException)
        {
            return await _employeeAndSettingsService.GetAdminStoreSettingsAsync();
        }
    }

    public async Task<AdminStoreSettingsDto> UpdateStoreSettingsAsync(UpdateStoreSettingsRequest request)
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.UpdateStoreSettingsAsync(request);
        }
        catch (HttpRequestException)
        {
            return await _employeeAndSettingsService.UpdateStoreSettingsAsync(CurrentEmployee!.EmployeeId, request);
        }
    }

    public async Task<AdminStoreSettingsDto> UploadStoreLogoAsync(string fileName, Stream fileStream, string contentType)
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.UploadStoreLogoAsync(fileName, fileStream, contentType);
        }
        catch (HttpRequestException)
        {
            if (fileStream.CanSeek)
                fileStream.Position = 0;
            return await _employeeAndSettingsService.UploadStoreLogoAsync(CurrentEmployee!.EmployeeId, fileName, fileStream, contentType);
        }
    }

    public async Task<AdminStoreSettingsDto> RemoveStoreLogoAsync()
    {
        EnsureAdminPermission();
        try
        {
            return await _apiClient.RemoveStoreLogoAsync();
        }
        catch (HttpRequestException)
        {
            return await _employeeAndSettingsService.RemoveStoreLogoAsync(CurrentEmployee!.EmployeeId);
        }
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
