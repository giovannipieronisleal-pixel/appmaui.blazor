using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Estrutura de estado persistente do banco de dados (sincronizada em disco/Supabase).
/// Inicializa limpa (sem produtos ou categorias de exemplo) e com o login Admin padrão.
/// </summary>
public sealed class ClothingStoreDatabaseState
{
    public List<Role> Roles { get; set; } = [];
    public List<User> Users { get; set; } = [];
    public List<Employee> Employees { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<Product> Products { get; set; } = [];
    public List<StockMovement> StockMovements { get; set; } = [];
    public StoreSettings StoreSettings { get; set; } = new();

    public static ClothingStoreDatabaseState CreateInitialSeed(IPasswordHasher passwordHasher)
    {
        var state = new ClothingStoreDatabaseState();

        // 1. Perfis de Acesso (Roles)
        var adminRole = new Role(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RoleNames.Admin,
            "Proprietário / Administrador com acesso total");

        var employeeRole = new Role(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            RoleNames.Employee,
            "Funcionário com acesso operacional a produtos e estoque");

        state.Roles.AddRange([adminRole, employeeRole]);

        // 2. Login Admin Padrão (login: admin / senha: admin)
        var adminUser = new User("admin", passwordHasher.HashPassword("admin"), adminRole.Id)
        {
            Id = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111")
        };

        var adminEmployee = new Employee(
            adminUser.Id,
            "Administrador",
            "Proprietário(a) / Administrador(a)",
            "5511900000000",
            canManageProducts: true,
            canManageStock: true)
        {
            Id = Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111")
        };

        state.Users.Add(adminUser);
        state.Employees.Add(adminEmployee);

        // 3. Configurações Iniciais Limpas da Loja
        state.StoreSettings = new StoreSettings(
            storeName: "Minha Loja",
            whatsappNumber: "5511900000000",
            storeDescription: "Catálogo digital oficial da loja.",
            logoStoragePath: null,
            logoPublicUrl: null,
            defaultLowStockThreshold: 3);

        return state;
    }
}
