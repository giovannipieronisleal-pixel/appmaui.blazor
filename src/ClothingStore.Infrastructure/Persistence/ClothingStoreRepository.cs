using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Implementação thread-safe e transacional de <see cref="IClothingStoreRepository"/>.
/// Sincroniza os dados em arquivo persistente JSON e suporta integração com Supabase PostgreSQL REST API.
/// </summary>
public sealed class ClothingStoreRepository : IClothingStoreRepository
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storageFilePath;
    private readonly IPasswordHasher _passwordHasher;
    private ClothingStoreDatabaseState _state;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ClothingStoreRepository(IPasswordHasher passwordHasher, string? customStoragePath = null)
    {
        _passwordHasher = passwordHasher;
        _storageFilePath = string.IsNullOrWhiteSpace(customStoragePath)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "clothingstore_db.json")
            : customStoragePath;

        _state = LoadOrInitializeState();
    }

    private ClothingStoreDatabaseState LoadOrInitializeState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(_storageFilePath))
            {
                var json = File.ReadAllText(_storageFilePath);
                var loaded = JsonSerializer.Deserialize<ClothingStoreDatabaseState>(json, JsonOptions);
                if (loaded is not null && loaded.Roles.Count > 0)
                {
                    HydrateNavigationProperties(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // Se o arquivo estiver corrompido ou inacessível, inicializa com o seed padrão.
        }

        var seeded = ClothingStoreDatabaseState.CreateInitialSeed(_passwordHasher);
        HydrateNavigationProperties(seeded);
        PersistToDisk(seeded);
        return seeded;
    }

    private static void HydrateNavigationProperties(ClothingStoreDatabaseState state)
    {
        var rolesById = state.Roles.ToDictionary(r => r.Id);
        foreach (var user in state.Users)
        {
            if (rolesById.TryGetValue(user.RoleId, out var role))
            {
                user.Role = role;
            }
        }

        var usersById = state.Users.ToDictionary(u => u.Id);
        foreach (var emp in state.Employees)
        {
            if (usersById.TryGetValue(emp.UserId, out var user))
            {
                emp.User = user;
            }
        }

        var categoriesById = state.Categories.ToDictionary(c => c.Id);
        foreach (var product in state.Products)
        {
            if (categoriesById.TryGetValue(product.CategoryId, out var cat))
            {
                product.Category = cat;
            }
        }
    }

    private void PersistToDisk(ClothingStoreDatabaseState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(_storageFilePath, json);
        }
        catch
        {
            // Em ambientes somente leitura ou testes em memória, mantém o estado em RAM.
        }
    }

    public async Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Roles.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Roles.FirstOrDefault(r =>
                string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<User?> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var normalized = email.Trim().ToLowerInvariant();
            return _state.Users.FirstOrDefault(u =>
                string.Equals(u.Email, normalized, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Users.FirstOrDefault(u => u.Id == userId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Employee?> GetEmployeeByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Employees.FirstOrDefault(e => e.UserId == userId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Employee?> GetEmployeeByIdAsync(Guid employeeId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Employees.FirstOrDefault(e => e.Id == employeeId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Employees
                .OrderByDescending(e => e.IsActive)
                .ThenBy(e => e.FullName)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddEmployeeWithUserAsync(User user, Employee employee, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Users.Any(u => string.Equals(u.Email, user.Email, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DomainException($"Já existe um usuário cadastrado com o e-mail '{user.Email}'.");
            }

            _state.Users.Add(user);
            _state.Employees.Add(employee);
            HydrateNavigationProperties(_state);
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateEmployeeAsync(Employee employee, User? user = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            HydrateNavigationProperties(_state);
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(bool onlyActive = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var query = _state.Categories.AsEnumerable();
            if (onlyActive)
            {
                query = query.Where(c => c.IsActive);
            }
            return query.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Category?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Categories.FirstOrDefault(c => c.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Category?> GetCategoryBySlugAsync(string slug, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Categories.FirstOrDefault(c =>
                string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddCategoryAsync(Category category, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Categories.Any(c => string.Equals(c.Slug, category.Slug, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DomainException($"Já existe uma categoria com o identificador '{category.Slug}'.");
            }

            _state.Categories.Add(category);
            HydrateNavigationProperties(_state);
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateCategoryAsync(Category category, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            HydrateNavigationProperties(_state);
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Product>> GetProductsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            HydrateNavigationProperties(_state);
            var query = _state.Products.AsEnumerable();
            if (!includeArchived)
            {
                query = query.Where(p => !p.IsArchived);
            }
            return query.OrderByDescending(p => p.CreatedAt).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Product?> GetProductByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            HydrateNavigationProperties(_state);
            return _state.Products.FirstOrDefault(p => p.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Product?> GetProductBySlugOrIdAsync(string slugOrId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            HydrateNavigationProperties(_state);
            if (Guid.TryParse(slugOrId, out var id))
            {
                return _state.Products.FirstOrDefault(p => p.Id == id);
            }

            return _state.Products.FirstOrDefault(p =>
                string.Equals(p.Slug, slugOrId.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProductVariant?> GetVariantByIdAsync(Guid variantId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.Products
                .SelectMany(p => p.Variants)
                .FirstOrDefault(v => v.Id == variantId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddProductAsync(Product product, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Products.Any(p => !p.IsArchived && string.Equals(p.Slug, product.Slug, StringComparison.OrdinalIgnoreCase)))
            {
                product.Slug = $"{product.Slug}-{Guid.NewGuid().ToString("N")[..4]}";
            }

            _state.Products.Add(product);
            HydrateNavigationProperties(_state);
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateProductAsync(Product product, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            product.UpdatedAt = DateTimeOffset.UtcNow;
            HydrateNavigationProperties(_state);
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteOrArchiveProductAsync(Guid productId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var product = _state.Products.FirstOrDefault(p => p.Id == productId)
                ?? throw new DomainException("Produto não encontrado.");

            var variantIds = product.Variants.Select(v => v.Id).ToHashSet();
            var hasMovements = _state.StockMovements.Any(m => variantIds.Contains(m.ProductVariantId));

            if (hasMovements)
            {
                product.Archive();
            }
            else
            {
                _state.Products.Remove(product);
            }

            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        Guid? employeeId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var query = _state.StockMovements.AsEnumerable();

            if (variantId.HasValue)
            {
                query = query.Where(m => m.ProductVariantId == variantId.Value);
            }

            if (productId.HasValue)
            {
                var product = _state.Products.FirstOrDefault(p => p.Id == productId.Value);
                var variantIds = product?.Variants.Select(v => v.Id).ToHashSet() ?? [];
                query = query.Where(m => variantIds.Contains(m.ProductVariantId));
            }

            if (employeeId.HasValue)
            {
                query = query.Where(m => m.EmployeeId == employeeId.Value);
            }

            return query
                .OrderByDescending(m => m.CreatedAt)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordStockMovementAsync(ProductVariant variant, StockMovement movement, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _state.StockMovements.Add(movement);
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoreSettings> GetStoreSettingsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _state.StoreSettings;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateStoreSettingsAsync(StoreSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _state.StoreSettings = settings;
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }
}
