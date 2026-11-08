using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Estrutura de estado persistente do banco de dados (sincronizada em disco/Supabase)
/// com seed inicial completo da loja de roupas.
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

        // 1. Perfis (Roles)
        var adminRole = new Role(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RoleNames.Admin,
            "Proprietário / Administrador com acesso total");

        var employeeRole = new Role(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            RoleNames.Employee,
            "Funcionário com acesso operacional a produtos e estoque");

        state.Roles.AddRange([adminRole, employeeRole]);

        // 2. Usuários e Funcionários Padrão
        var adminUser = new User("admin@loja.com", passwordHasher.HashPassword("Admin@123"), adminRole.Id)
        {
            Id = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111")
        };
        var adminEmployee = new Employee(
            adminUser.Id,
            "Helena Prado (Proprietária)",
            "Proprietária & Diretora Criativa",
            "5511999999999",
            canManageProducts: true,
            canManageStock: true)
        {
            Id = Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111")
        };

        var staffUser = new User("funcionario@loja.com", passwordHasher.HashPassword("Func@123"), employeeRole.Id)
        {
            Id = Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222")
        };
        var staffEmployee = new Employee(
            staffUser.Id,
            "Lucas Mendes",
            "Consultor de Vendas & Estoque",
            "5511988887777",
            canManageProducts: true,
            canManageStock: true)
        {
            Id = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222")
        };

        state.Users.AddRange([adminUser, staffUser]);
        state.Employees.AddRange([adminEmployee, staffEmployee]);

        // 3. Configurações Personalizadas da Loja
        state.StoreSettings = new StoreSettings(
            storeName: "Aura Vestuário",
            whatsappNumber: "5511999999999",
            storeDescription: "Curadoria de roupas essenciais com design atemporal, tecidos nobres de algodão e linho, pensadas para o conforto diário.",
            logoStoragePath: null,
            logoPublicUrl: null,
            defaultLowStockThreshold: 3);

        // 4. Categorias
        var catCamisetas = new Category("Camisetas", "Camisetas essenciais em algodão Pima e Egípcio", 1)
        {
            Id = Guid.Parse("cccccccc-0001-0001-0001-000000000001")
        };
        var catCamisas = new Category("Camisas & Linho", "Camisas de alfaiataria leve e puro linho", 2)
        {
            Id = Guid.Parse("cccccccc-0002-0002-0002-000000000002")
        };
        var catCalcas = new Category("Calças", "Calças de corte reto, chino e alfaiataria contemporânea", 3)
        {
            Id = Guid.Parse("cccccccc-0003-0003-0003-000000000003")
        };
        var catVestidos = new Category("Vestidos", "Vestidos fluidos e minimalistas para todas as ocasiões", 4)
        {
            Id = Guid.Parse("cccccccc-0004-0004-0004-000000000004")
        };
        var catCasacos = new Category("Casacos & Tricôs", "Blazers, jaquetas e tricôs estruturados", 5)
        {
            Id = Guid.Parse("cccccccc-0005-0005-0005-000000000005")
        };

        state.Categories.AddRange([catCamisetas, catCamisas, catCalcas, catVestidos, catCasacos]);

        // 5. Produto 1: Camiseta Básica (exatamente como no exemplo da especificação)
        var prodCamiseta = new Product(
            catCamisetas.Id,
            "Camiseta Básica",
            "Camiseta essencial confeccionada em malha 100% algodão penteado de gramatura encorpada (200g/m²). Gola canelada estruturada, toque macio e caimento impecável.",
            basePrice: 129.90m,
            isVisibleOnSite: true)
        {
            Id = Guid.Parse("dddddddd-0001-0001-0001-000000000001")
        };

        prodCamiseta.Images.AddRange([
            new ProductImage(
                prodCamiseta.Id,
                "products/camiseta-basica-1.jpg",
                "https://images.unsplash.com/photo-1521572163474-6864f9cf17ab?auto=format&fit=crop&w=900&q=80",
                "Camiseta Básica Frente",
                displayOrder: 1,
                isPrimary: true),
            new ProductImage(
                prodCamiseta.Id,
                "products/camiseta-basica-2.jpg",
                "https://images.unsplash.com/photo-1583743814966-8936f5b7be1a?auto=format&fit=crop&w=900&q=80",
                "Camiseta Básica Detalhe Tecido Preta",
                displayOrder: 2,
                isPrimary: false)
        ]);

        var vPretoP = prodCamiseta.AddVariant("Preto", "P", "#18181B", "CAM-BAS-PRE-P", 3);
        var vPretoM = prodCamiseta.AddVariant("Preto", "M", "#18181B", "CAM-BAS-PRE-M", 3);
        var vPretoG = prodCamiseta.AddVariant("Preto", "G", "#18181B", "CAM-BAS-PRE-G", 3);
        var vBrancoM = prodCamiseta.AddVariant("Branco", "M", "#F8FAFC", "CAM-BAS-BRA-M", 3);

        state.StockMovements.Add(vPretoP.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 3, "Estoque inicial da coleção"));
        state.StockMovements.Add(vPretoM.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 5, "Estoque inicial da coleção"));
        state.StockMovements.Add(vPretoG.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 2, "Estoque inicial da coleção (alerta estoque baixo)"));
        state.StockMovements.Add(vBrancoM.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 8, "Estoque inicial da coleção"));

        // 6. Produto 2: Camisa Puro Linho Areia
        var prodCamisaLinho = new Product(
            catCamisas.Id,
            "Camisa Puro Linho Relaxed",
            "Camisa de manga longa em linho europeu certificado, modelagem levemente ampla, botões madrepérola e frescor natural para dias quentes.",
            basePrice: 349.00m,
            isVisibleOnSite: true)
        {
            Id = Guid.Parse("dddddddd-0002-0002-0002-000000000002")
        };

        prodCamisaLinho.Images.AddRange([
            new ProductImage(
                prodCamisaLinho.Id,
                "products/camisa-linho-1.jpg",
                "https://images.unsplash.com/photo-1596755094514-f87e34085b2c?auto=format&fit=crop&w=900&q=80",
                "Camisa Puro Linho Relaxed",
                displayOrder: 1,
                isPrimary: true),
            new ProductImage(
                prodCamisaLinho.Id,
                "products/camisa-linho-2.jpg",
                "https://images.unsplash.com/photo-1602810318383-e386cc2a3ccf?auto=format&fit=crop&w=900&q=80",
                "Camisa Linho Textura",
                displayOrder: 2,
                isPrimary: false)
        ]);

        var vLinhoAreiaM = prodCamisaLinho.AddVariant("Areia", "M", "#D6C7B2", "CAM-LIN-ARE-M", 3);
        var vLinhoAreiaG = prodCamisaLinho.AddVariant("Areia", "G", "#D6C7B2", "CAM-LIN-ARE-G", 3);
        var vLinhoBrancoP = prodCamisaLinho.AddVariant("Branco", "P", "#F8FAFC", "CAM-LIN-BRA-P", 3);

        state.StockMovements.Add(vLinhoAreiaM.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 6, "Recebimento fornecedor"));
        state.StockMovements.Add(vLinhoAreiaG.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 4, "Recebimento fornecedor"));
        state.StockMovements.Add(vLinhoBrancoP.ApplyStockMovement(staffEmployee.Id, StockMovementType.In, 1, "Última peça em estoque"));

        // 7. Produto 3: Vestido Midi Atelier
        var prodVestido = new Product(
            catVestidos.Id,
            "Vestido Midi Fluido Atelier",
            "Vestido midi em viscose sarjada com alças reguláveis, decote reto minimalista e fenda lateral discreta.",
            basePrice: 429.00m,
            isVisibleOnSite: true)
        {
            Id = Guid.Parse("dddddddd-0003-0003-0003-000000000003")
        };

        prodVestido.Images.Add(
            new ProductImage(
                prodVestido.Id,
                "products/vestido-midi-1.jpg",
                "https://images.unsplash.com/photo-1595777457583-95e059d581b8?auto=format&fit=crop&w=900&q=80",
                "Vestido Midi Fluido Atelier",
                displayOrder: 1,
                isPrimary: true));

        var vVestVerdeP = prodVestido.AddVariant("Verde Oliva", "P", "#3F6212", "VES-MID-OLI-P", 2);
        var vVestVerdeM = prodVestido.AddVariant("Verde Oliva", "M", "#3F6212", "VES-MID-OLI-M", 2);
        var vVestPretoG = prodVestido.AddVariant("Preto", "G", "#18181B", "VES-MID-PRE-G", 2); // Sem estoque (0)

        state.StockMovements.Add(vVestVerdeP.ApplyStockMovement(staffEmployee.Id, StockMovementType.In, 5, "Reposição atelier"));
        state.StockMovements.Add(vVestVerdeM.ApplyStockMovement(staffEmployee.Id, StockMovementType.In, 4, "Reposição atelier"));
        _ = vVestPretoG;

        // 8. Produto 4: Calça Alfaiataria Reta
        var prodCalca = new Product(
            catCalcas.Id,
            "Calça Alfaiataria Reta",
            "Calça de cintura média com pregas frontais, corte reto alongado e acabamento de alfaiataria clássica.",
            basePrice: 389.90m,
            isVisibleOnSite: true)
        {
            Id = Guid.Parse("dddddddd-0004-0004-0004-000000000004")
        };

        prodCalca.Images.Add(
            new ProductImage(
                prodCalca.Id,
                "products/calca-alfaiataria-1.jpg",
                "https://images.unsplash.com/photo-1594633312681-425c7b97ccd1?auto=format&fit=crop&w=900&q=80",
                "Calça Alfaiataria Reta",
                displayOrder: 1,
                isPrimary: true));

        var vCalcaBegeP = prodCalca.AddVariant("Bege", "P", "#D6C7B2", "CAL-ALF-BEG-P", 3);
        var vCalcaPretaM = prodCalca.AddVariant("Preto", "M", "#18181B", "CAL-ALF-PRE-M", 3);

        state.StockMovements.Add(vCalcaBegeP.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 7, "Entrada coleção outono"));
        state.StockMovements.Add(vCalcaPretaM.ApplyStockMovement(adminEmployee.Id, StockMovementType.In, 6, "Entrada coleção outono"));

        state.Products.AddRange([prodCamiseta, prodCamisaLinho, prodVestido, prodCalca]);

        return state;
    }
}
