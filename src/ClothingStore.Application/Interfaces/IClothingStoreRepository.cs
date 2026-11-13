using ClothingStore.Domain.Entities;

namespace ClothingStore.Application.Interfaces;

/// <summary>
/// Repositório unificado e transacional para persistência das entidades do sistema
/// (compatível com PostgreSQL/Supabase e persistência local sincronizada).
/// </summary>
public interface IClothingStoreRepository
{
    // Roles & Users & Employees
    Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default);
    Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default);
    Task<User?> GetUserByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct = default);
    Task<Employee?> GetEmployeeByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<Employee?> GetEmployeeByIdAsync(Guid employeeId, CancellationToken ct = default);
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct = default);
    Task AddEmployeeWithUserAsync(User user, Employee employee, CancellationToken ct = default);
    Task UpdateEmployeeAsync(Employee employee, User? user = null, CancellationToken ct = default);

    // Categories
    Task<IReadOnlyList<Category>> GetCategoriesAsync(bool onlyActive = false, CancellationToken ct = default);
    Task<Category?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default);
    Task<Category?> GetCategoryBySlugAsync(string slug, CancellationToken ct = default);
    Task AddCategoryAsync(Category category, CancellationToken ct = default);
    Task UpdateCategoryAsync(Category category, CancellationToken ct = default);

    // Products, Variants & Images
    Task<IReadOnlyList<Product>> GetProductsAsync(bool includeArchived = false, CancellationToken ct = default);
    Task<Product?> GetProductByIdAsync(Guid id, CancellationToken ct = default);
    Task<Product?> GetProductBySlugOrIdAsync(string slugOrId, CancellationToken ct = default);
    Task<ProductVariant?> GetVariantByIdAsync(Guid variantId, CancellationToken ct = default);
    Task AddProductAsync(Product product, CancellationToken ct = default);
    Task UpdateProductAsync(Product product, CancellationToken ct = default);
    Task DeleteOrArchiveProductAsync(Guid productId, CancellationToken ct = default);

    // Stock Movements
    Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        Guid? employeeId = null,
        int limit = 100,
        CancellationToken ct = default);
    Task RecordStockMovementAsync(ProductVariant variant, StockMovement movement, CancellationToken ct = default);

    // Store Settings & Activation Gift Cards
    Task<StoreSettings> GetStoreSettingsAsync(CancellationToken ct = default);
    Task UpdateStoreSettingsAsync(StoreSettings settings, CancellationToken ct = default);

    Task<IReadOnlyList<PlanActivationCode>> GetPlanActivationCodesAsync(CancellationToken ct = default);
    Task<PlanActivationCode?> GetPlanActivationCodeByCodeAsync(string code, CancellationToken ct = default);
    Task<PlanActivationCode?> GetPlanActivationCodeByIdAsync(Guid id, CancellationToken ct = default);
    Task SavePlanActivationCodeAsync(PlanActivationCode activationCode, CancellationToken ct = default);

    // Pedidos Pix no app / cálculo de envio
    Task<IReadOnlyList<StoreOrder>> GetOrdersAsync(CancellationToken ct = default);
    Task<StoreOrder?> GetOrderByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveOrderAsync(StoreOrder order, CancellationToken ct = default);
}

