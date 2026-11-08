using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Funcionário ou proprietário da loja com permissões operacionais granulares.
/// </summary>
public sealed class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
    public bool CanManageProducts { get; set; }
    public bool CanManageStock { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Employee()
    {
    }

    public Employee(
        Guid userId,
        string fullName,
        string? jobTitle,
        string? phone,
        bool canManageProducts,
        bool canManageStock = true)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("O nome completo do funcionário é obrigatório.");

        UserId = userId;
        FullName = fullName.Trim();
        JobTitle = jobTitle?.Trim();
        Phone = phone?.Trim();
        CanManageProducts = canManageProducts;
        CanManageStock = canManageStock;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool HasProductManagementPermission(string roleName) =>
        IsActive && (string.Equals(roleName, RoleNames.Admin, StringComparison.OrdinalIgnoreCase) || CanManageProducts);

    public bool HasStockManagementPermission(string roleName) =>
        IsActive && (string.Equals(roleName, RoleNames.Admin, StringComparison.OrdinalIgnoreCase) || CanManageStock);

    public void SetActiveStatus(bool isActive)
    {
        IsActive = isActive;
        if (User is not null)
        {
            User.IsActive = isActive;
        }
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

