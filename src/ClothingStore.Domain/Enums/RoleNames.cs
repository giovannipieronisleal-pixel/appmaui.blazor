namespace ClothingStore.Domain.Enums;

/// <summary>
/// Constantes de papéis (Roles) e Policies de autorização do sistema.
/// </summary>
public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Employee = "Employee";

    public const string PolicyAdminOnly = "AdminOnly";
    public const string PolicyCanManageProducts = "CanManageProducts";
    public const string PolicyCanManageStock = "CanManageStock";
}
