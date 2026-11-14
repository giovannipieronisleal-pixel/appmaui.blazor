using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Implementação thread-safe e transacional de <see cref="IClothingStoreRepository"/>.
/// Sincroniza os dados em arquivo persistente JSON em tempo real entre a API, o Site Blazor e o App .NET MAUI.
/// </summary>
public sealed class ClothingStoreRepository : IClothingStoreRepository
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storageFilePath;
    private readonly string _fileMutexName;
    private readonly IPasswordHasher _passwordHasher;
    private ClothingStoreDatabaseState _state;
    private DateTime _lastLoadedUtc = DateTime.MinValue;

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
        _fileMutexName = BuildMutexName(_storageFilePath);

        _state = LoadOrInitializeState();
    }

    private ClothingStoreDatabaseState LoadOrInitializeState()
    {
        return ExecuteWithCrossProcessLock(() =>
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
                    var json = ReadFileSharedWithRetry(_storageFilePath);
                    var loaded = JsonSerializer.Deserialize<ClothingStoreDatabaseState>(json, JsonOptions);
                    if (loaded is not null && loaded.Roles.Count > 0)
                    {
                        HydrateNavigationProperties(loaded);
                        var mutated = PurgeLegacyDefaultAdmin(loaded);
                        if (PlatformOwnerSettingsStore.ApplyToStoreSettingsIfMissingOrDefault(_storageFilePath, loaded.StoreSettings))
                        {
                            mutated = true;
                        }

                        if (mutated)
                        {
                            PersistToDisk(loaded);
                        }
                        else
                        {
                            _lastLoadedUtc = File.GetLastWriteTimeUtc(_storageFilePath);
                        }

                        return loaded;
                    }

                    BackupCorruptFile();
                }
            }
            catch
            {
                BackupCorruptFile();
            }

            var seeded = ClothingStoreDatabaseState.CreateInitialSeed(_passwordHasher);
            HydrateNavigationProperties(seeded);
            PlatformOwnerSettingsStore.ApplyToStoreSettingsIfMissingOrDefault(_storageFilePath, seeded.StoreSettings);
            WriteFileAtomicWithRetry(_storageFilePath, JsonSerializer.Serialize(seeded, JsonOptions));
            _lastLoadedUtc = File.GetLastWriteTimeUtc(_storageFilePath);
            return seeded;
        });
    }

    private void BackupCorruptFile()
    {
        try
        {
            if (File.Exists(_storageFilePath))
            {
                var backupPath = $"{_storageFilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                File.Copy(_storageFilePath, backupPath, true);
            }
        }
        catch
        {
            // Cópia de segurança em melhor esforço
        }
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
            if (string.IsNullOrWhiteSpace(user.Username) && !string.IsNullOrWhiteSpace(user.Email))
            {
                user.Username = User.DeriveDefaultUsername(user.Email);
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

        state.Categories ??= [];
        var categoriesById = state.Categories.ToDictionary(c => c.Id);
        foreach (var product in state.Products)
        {
            if (categoriesById.TryGetValue(product.CategoryId, out var cat))
            {
                product.Category = cat;
            }

            foreach (var v in product.Variants)
            {
                v.ProductId = product.Id;
            }

            foreach (var img in product.Images)
            {
                img.ProductId = product.Id;
            }
        }

        if (string.IsNullOrWhiteSpace(state.StoreSettings.StoreSlug))
        {
            state.StoreSettings.StoreSlug = Category.GenerateSlug(state.StoreSettings.StoreName);
        }

        state.Orders ??= [];
        state.PlatformSubscriptionOrders ??= [];
    }

    private static readonly Guid LegacyDefaultAdminUserId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid LegacyDefaultAdminEmployeeId = Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111");

    private bool PurgeLegacyDefaultAdmin(ClothingStoreDatabaseState state)
    {
        var legacyUserIds = state.Users
            .Where(u =>
                u.Id == LegacyDefaultAdminUserId ||
                ((string.Equals(u.Email, "admin", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(u.Username, "admin", StringComparison.OrdinalIgnoreCase)) &&
                 _passwordHasher.VerifyPassword("admin", u.PasswordHash)))
            .Select(u => u.Id)
            .ToHashSet();

        var removedUsers = legacyUserIds.Count > 0
            ? state.Users.RemoveAll(u => legacyUserIds.Contains(u.Id))
            : 0;
        var removedEmployees = state.Employees.RemoveAll(e =>
            legacyUserIds.Contains(e.UserId) || e.Id == LegacyDefaultAdminEmployeeId);

        if (removedUsers == 0 && removedEmployees == 0)
        {
            return false;
        }

        // Se o banco já tinha outro usuário real cadastrado antes da remoção do admin legado,
        // garante que pelo menos um usuário ativo possua perfil Administrador para não travar a loja.
        var adminRole = state.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, ClothingStore.Domain.Enums.RoleNames.Admin, StringComparison.OrdinalIgnoreCase));

        if (adminRole is not null &&
            state.Users.Any(u => u.IsActive) &&
            !state.Users.Any(u => u.IsActive && u.RoleId == adminRole.Id))
        {
            var earliestActiveUser = state.Users
                .Where(u => u.IsActive)
                .OrderBy(u => u.CreatedAt)
                .First();
            earliestActiveUser.RoleId = adminRole.Id;
            earliestActiveUser.Role = adminRole;

            var employee = state.Employees.FirstOrDefault(e => e.UserId == earliestActiveUser.Id);
            if (employee is not null)
            {
                employee.CanManageProducts = true;
                employee.CanManageStock = true;
                employee.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        HydrateNavigationProperties(state);
        return true;
    }

    private void PersistToDisk(ClothingStoreDatabaseState state)
    {
        try
        {
            ExecuteWithCrossProcessLock(() =>
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                WriteFileAtomicWithRetry(_storageFilePath, json);
                _lastLoadedUtc = File.GetLastWriteTimeUtc(_storageFilePath);
                return true;
            });
        }
        catch
        {
        }
    }

    private void RefreshFromDiskIfModified()
    {
        try
        {
            ExecuteWithCrossProcessLock(() =>
            {
                if (File.Exists(_storageFilePath))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(_storageFilePath);
                    if (lastWrite > _lastLoadedUtc)
                    {
                        var json = ReadFileSharedWithRetry(_storageFilePath);
                        var loaded = JsonSerializer.Deserialize<ClothingStoreDatabaseState>(json, JsonOptions);
                        if (loaded is not null && loaded.Roles.Count > 0)
                        {
                            HydrateNavigationProperties(loaded);
                            var purged = PurgeLegacyDefaultAdmin(loaded);
                            _state = loaded;
                            if (purged)
                            {
                                PersistToDisk(loaded);
                            }
                            else
                            {
                                _lastLoadedUtc = lastWrite;
                            }
                        }
                    }
                }

                return true;
            });
        }
        catch
        {
        }
    }

    private static string BuildMutexName(string storagePath)
    {
        var fullPath = Path.GetFullPath(storagePath);
        if (OperatingSystem.IsWindows())
        {
            fullPath = fullPath.ToLowerInvariant();
        }

        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fullPath));
        return "Local\\YouLinkDb_" + Convert.ToHexString(hashBytes, 0, 16);
    }

    private T ExecuteWithCrossProcessLock<T>(Func<T> action)
    {
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, _fileMutexName);
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
        }
        catch
        {
            mutex?.Dispose();
            mutex = null;
            acquired = false;
        }

        try
        {
            return action();
        }
        finally
        {
            if (mutex is not null)
            {
                if (acquired)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }

                mutex.Dispose();
            }
        }
    }

    private static string ReadFileSharedWithRetry(string path)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                throw;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(15 * attempt);
            }
        }
    }

    private static void WriteFileAtomicWithRetry(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var targetDir = string.IsNullOrWhiteSpace(directory) ? "." : directory;
        var tempPath = Path.Combine(targetDir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            const int maxMoveAttempts = 3;
            for (var attempt = 1; attempt <= maxMoveAttempts; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt < maxMoveAttempts)
                    {
                        Thread.Sleep(10 * attempt);
                    }
                }
            }

            // Fallback NTFS para quando um leitor externo mantém handle aberto com FileShare.ReadWrite | FileShare.Delete
            // e impede o MoveFileExW do Windows de substituir o inode em uso.
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            using var sharedWriteStream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            sharedWriteStream.Position = 0;
            sharedWriteStream.Write(bytes, 0, bytes.Length);
            sharedWriteStream.SetLength(bytes.Length);
            sharedWriteStream.Flush(flushToDisk: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    public async Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
            var normalized = email.Trim().ToLowerInvariant();
            var found = _state.Users.FirstOrDefault(u =>
                string.Equals(u.Email, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(u.Username, normalized, StringComparison.OrdinalIgnoreCase));

            if (found is not null)
                return found;

            // Permite login por Nome de Usuário, Nome Completo ou E-mail
            var matchedEmp = _state.Employees.FirstOrDefault(e =>
                string.Equals(e.FullName, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Category.GenerateSlug(e.FullName), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Category.GenerateSlug(e.FullName).Replace("-", "."), normalized, StringComparison.OrdinalIgnoreCase));

            if (matchedEmp is not null)
            {
                return _state.Users.FirstOrDefault(u => u.Id == matchedEmp.UserId);
            }

            return null;
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
            HydrateNavigationProperties(_state);
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
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
            RefreshFromDiskIfModified();
            if (PlatformOwnerSettingsStore.ApplyToStoreSettingsIfMissingOrDefault(_storageFilePath, _state.StoreSettings))
            {
                PersistToDisk(_state);
            }
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
            PlatformOwnerSettingsStore.SaveFromStoreSettings(_storageFilePath, settings);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<PlanActivationCode>> GetPlanActivationCodesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            return _state.PlanActivationCodes
                .OrderByDescending(c => c.CreatedAt)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PlanActivationCode?> GetPlanActivationCodeByCodeAsync(string code, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
            return _state.PlanActivationCodes.FirstOrDefault(c =>
                string.Equals(c.Code, normalized, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PlanActivationCode?> GetPlanActivationCodeByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            return _state.PlanActivationCodes.FirstOrDefault(c => c.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SavePlanActivationCodeAsync(PlanActivationCode activationCode, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            var idx = _state.PlanActivationCodes.FindIndex(c => c.Id == activationCode.Id);
            if (idx >= 0)
            {
                _state.PlanActivationCodes[idx] = activationCode;
            }
            else
            {
                _state.PlanActivationCodes.Add(activationCode);
            }
            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<StoreOrder>> GetOrdersAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            _state.Orders ??= [];
            return _state.Orders
                .OrderByDescending(o => o.CreatedAt)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoreOrder?> GetOrderByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            _state.Orders ??= [];
            return _state.Orders.FirstOrDefault(o => o.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveOrderAsync(StoreOrder order, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            _state.Orders ??= [];
            var idx = _state.Orders.FindIndex(o => o.Id == order.Id);
            if (idx >= 0)
            {
                _state.Orders[idx] = order;
            }
            else
            {
                _state.Orders.Add(order);
            }

            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<PlatformSubscriptionOrder>> GetPlatformSubscriptionOrdersAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            _state.PlatformSubscriptionOrders ??= [];
            return _state.PlatformSubscriptionOrders
                .OrderByDescending(o => o.CreatedAt)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PlatformSubscriptionOrder?> GetPlatformSubscriptionOrderByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            _state.PlatformSubscriptionOrders ??= [];
            return _state.PlatformSubscriptionOrders.FirstOrDefault(o => o.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PlatformSubscriptionOrder?> GetPlatformSubscriptionOrderByNumberAsync(string orderNumber, CancellationToken ct = default)
    {
        var normalized = (orderNumber ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            _state.PlatformSubscriptionOrders ??= [];
            return _state.PlatformSubscriptionOrders.FirstOrDefault(o =>
                string.Equals(o.OrderNumber, normalized, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SavePlatformSubscriptionOrderAsync(PlatformSubscriptionOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModified();
            _state.PlatformSubscriptionOrders ??= [];
            var idx = _state.PlatformSubscriptionOrders.FindIndex(o => o.Id == order.Id);
            if (idx >= 0)
                _state.PlatformSubscriptionOrders[idx] = order;
            else
                _state.PlatformSubscriptionOrders.Add(order);

            PersistToDisk(_state);
        }
        finally
        {
            _lock.Release();
        }
    }
}
