using System.Diagnostics;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Common;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

/// <summary>
/// Bateria de Testes de Carga Massiva e Estresse Extremo:
/// Simula milhares de pessoas (clientes navegando, vendas concorrentes, lojistas e logins em alta escala).
/// </summary>
[TestClass]
public class MassiveScaleStressTests
{
    private static readonly Guid AdminEmpId = Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111");

    private sealed class StressEnv : IDisposable
    {
        public string DbFile { get; } = Path.Combine(Path.GetTempPath(), $"cs_stress_{Guid.NewGuid():N}.json");
        public string Uploads { get; } = Path.Combine(Path.GetTempPath(), $"cs_stress_up_{Guid.NewGuid():N}");
        public Pbkdf2PasswordHasher Hasher { get; } = new();
        public ClothingStoreRepository Repo { get; }
        public AuthService Auth { get; }
        public CatalogService Catalog { get; }
        public ProductManagementService Products { get; }
        public StockManagementService Stock { get; }
        public EmployeeAndSettingsService Admin { get; }
        private readonly HttpClient _http = new();

        public StressEnv()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "SuperSecretStressTestingKey_1234567890_ScaleTest!"
                })
                .Build();

            Repo = new ClothingStoreRepository(Hasher, DbFile);
            var images = new SupabaseImageStorageService(_http, config, Uploads);
            Auth = new AuthService(Repo, Hasher, new JwtTokenService(config));
            Catalog = new CatalogService(Repo);
            Products = new ProductManagementService(Repo, images);
            Stock = new StockManagementService(Repo);
            Admin = new EmployeeAndSettingsService(Repo, Hasher, images);
        }

        public void Dispose()
        {
            _http.Dispose();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(DbFile) + "*"))
            {
                try { File.Delete(f); } catch { }
            }
            if (File.Exists(DbFile)) { try { File.Delete(DbFile); } catch { } }
            if (Directory.Exists(Uploads)) { try { Directory.Delete(Uploads, true); } catch { } }
        }
    }

    [TestMethod]
    public async Task StressTest_ThousandsOfConcurrentShoppers_BrowsingAndFiltering()
    {
        using var env = new StressEnv();

        // 1. Obtém as categorias já existentes e cria produtos para o catálogo de teste
        var allCats = await env.Products.GetAllCategoriesAsync();
        var cat1 = allCats.Count > 0 ? allCats[0] : await env.Products.CreateCategoryAsync(new SaveCategoryRequest { Name = "Cat Stress 1" });
        var cat2 = allCats.Count > 1 ? allCats[1] : await env.Products.CreateCategoryAsync(new SaveCategoryRequest { Name = "Cat Stress 2" });

        var prod1 = await env.Products.CreateProductAsync(new SaveProductRequest
        {
            CategoryId = cat1.Id,
            Name = "Vestido Longo Floral Verão",
            Description = "Tecido leve",
            BasePrice = 189.90m,
            IsVisibleOnSite = true
        }, AdminEmpId);

        var prod2 = await env.Products.CreateProductAsync(new SaveProductRequest
        {
            CategoryId = cat2.Id,
            Name = "Camisa Social Slim Fit",
            Description = "Algodão nobre",
            BasePrice = 149.90m,
            IsVisibleOnSite = true
        }, AdminEmpId);

        await env.Products.AddVariantAsync(prod1.Id, AdminEmpId, new SaveProductVariantRequest
        {
            Color = "Azul",
            Size = "M",
            CustomSku = "SKU-V-M",
            InitialStockQuantity = 25,
            LowStockThreshold = 3,
            IsActive = true
        });

        await env.Products.AddVariantAsync(prod2.Id, AdminEmpId, new SaveProductVariantRequest
        {
            Color = "Branco",
            Size = "G",
            CustomSku = "SKU-C-G",
            InitialStockQuantity = 40,
            LowStockThreshold = 5,
            IsActive = true
        });

        const int totalSimulatedUsers = 2000;
        var sw = Stopwatch.StartNew();

        var successCount = 0;
        var queries = new[]
        {
            new CatalogQueryFilter(Search: "Vestido"),
            new CatalogQueryFilter(Search: "Camisa"),
            new CatalogQueryFilter(CategorySlug: cat1.Slug),
            new CatalogQueryFilter(Color: "Azul"),
            new CatalogQueryFilter(Size: "G"),
            new CatalogQueryFilter(OnlyInStock: true),
            new CatalogQueryFilter()
        };

        // Simula 2.000 clientes navegando e filtrando simultaneamente em paralelo
        await Parallel.ForEachAsync(Enumerable.Range(0, totalSimulatedUsers), new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (i, ct) =>
        {
            var filter = queries[i % queries.Length];
            var products = await env.Catalog.GetCatalogProductsAsync(filter, ct);
            var filters = await env.Catalog.GetCatalogFiltersAsync(ct);
            var store = await env.Catalog.GetPublicStoreSettingsAsync(ct);

            if (products is not null && filters is not null && store is not null)
            {
                Interlocked.Increment(ref successCount);
            }
        });

        sw.Stop();

        Assert.AreEqual(totalSimulatedUsers, successCount, "Todas as 2.000 requisições simultâneas de catálogo devem responder com sucesso.");
        Assert.IsTrue(sw.ElapsedMilliseconds < 15000, $"2.000 acessos concorrentes devem executar em menos de 15s. Levou {sw.ElapsedMilliseconds}ms.");
    }

    [TestMethod]
    public async Task StressTest_HundredsOfConcurrentStockMovements_ExactAtomicBalance()
    {
        using var env = new StressEnv();

        var cat = await env.Products.CreateCategoryAsync(new SaveCategoryRequest
        {
            Name = "Básicos",
            Description = "Roupas básicas",
            DisplayOrder = 1,
            IsActive = true
        });

        var prod = await env.Products.CreateProductAsync(new SaveProductRequest
        {
            CategoryId = cat.Id,
            Name = "Camiseta Algodão Egípcio",
            Description = "Básica",
            BasePrice = 89.90m,
            IsVisibleOnSite = true
        }, AdminEmpId);

        var variant = await env.Products.AddVariantAsync(prod.Id, AdminEmpId, new SaveProductVariantRequest
        {
            Color = "Preto",
            Size = "M",
            CustomSku = "SKU-BASE-M",
            InitialStockQuantity = 1000,
            LowStockThreshold = 5,
            IsActive = true
        });

        const int initialStock = 1000;
        const int totalTransactions = 200;
        var successfulOutCount = 0;
        var successfulInCount = 0;

        var tasks = new List<Task>();

        for (int i = 0; i < totalTransactions; i++)
        {
            var isExit = (i % 2 == 0);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (isExit)
                    {
                        await env.Stock.RegisterMovementAsync(AdminEmpId, new CreateStockMovementRequest
                        {
                            ProductVariantId = variant.Id,
                            MovementType = "OUT",
                            Quantity = 1,
                            Reason = "Venda Concorrente"
                        });
                        Interlocked.Increment(ref successfulOutCount);
                    }
                    else
                    {
                        await env.Stock.RegisterMovementAsync(AdminEmpId, new CreateStockMovementRequest
                        {
                            ProductVariantId = variant.Id,
                            MovementType = "IN",
                            Quantity = 2,
                            Reason = "Reposição Concorrente"
                        });
                        Interlocked.Increment(ref successfulInCount);
                    }
                }
                catch
                {
                }
            }));
        }

        await Task.WhenAll(tasks);

        var productInDb = await env.Repo.GetProductByIdAsync(prod.Id);
        var finalVariant = productInDb!.Variants.First(v => v.Id == variant.Id);
        var expectedBalance = initialStock - (successfulOutCount * 1) + (successfulInCount * 2);

        Assert.AreEqual(expectedBalance, finalVariant.StockQuantity, "O saldo final de estoque deve bater exatamente com a contabilidade atômica.");
        Assert.IsTrue(finalVariant.StockQuantity >= 0, "O estoque jamais pode ficar negativo sob concorrência.");
    }

    [TestMethod]
    public async Task StressTest_HundredsOfConcurrentStoreSettingsUpdates_FileIntegrityKept()
    {
        using var env = new StressEnv();

        const int totalUpdates = 100;
        var successUpdates = 0;

        await Parallel.ForEachAsync(Enumerable.Range(1, totalUpdates), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
        {
            try
            {
                await env.Admin.UpdateStoreSettingsAsync(AdminEmpId, new UpdateStoreSettingsRequest
                {
                    StoreName = $"Loja Concorrente #{i}",
                    WhatsappNumber = $"552199999{i:D4}",
                    StoreDescription = $"Descrição da loja {i} sob estresse de concorrência.",
                    DefaultLowStockThreshold = (i % 10) + 1,
                    OnlineBaseUrl = $"https://lojas.loja{i}.com"
                }, ct);

                Interlocked.Increment(ref successUpdates);
            }
            catch
            {
            }
        });

        Assert.AreEqual(totalUpdates, successUpdates, "Todas as gravações concorrentes de configurações devem ter êxito sem corromper o arquivo.");

        var settingsInDb = await env.Repo.GetStoreSettingsAsync();
        Assert.IsNotNull(settingsInDb);
        Assert.IsTrue(settingsInDb.StoreName.StartsWith("Loja Concorrente #"));
        Assert.IsTrue(File.Exists(env.DbFile));
    }

    [TestMethod]
    public async Task StressTest_HundredsOfConcurrentLogins_And_BruteForceLockout()
    {
        using var env = new StressEnv();

        // Cria conta de lojista vítima de teste
        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
            FullName: "Lojista Estresse",
            Email: "lojista_estresse@gmail.com",
            Password: "SenhaValida123!",
            Phone: "21988887777"));

        // 1. Simula ataque concorrente de força bruta disparando 10 senhas incorretas em paralelo
        await Parallel.ForEachAsync(Enumerable.Range(1, 10), new ParallelOptions { MaxDegreeOfParallelism = 10 }, async (i, ct) =>
        {
            try
            {
                await env.Auth.LoginWithChallengeAsync(new LoginRequest("lojista_estresse@gmail.com", $"SenhaErrada_{i}!"), ct);
            }
            catch
            {
                // Exceção esperada de credenciais inválidas ou bloqueio
            }
        });

        // A conta deve estar bloqueada por excesso de tentativas (> 5 tentativas)
        var user = await env.Repo.GetUserByEmailAsync("lojista_estresse@gmail.com");
        Assert.IsNotNull(user);
        ArgumentNullException.ThrowIfNull(user);
        Assert.IsTrue(user.FailedLoginAttempts >= 5, $"Deve ter acumulado pelo menos 5 falhas. Acumulou: {user.FailedLoginAttempts}");
        Assert.IsTrue(user.IsLockedOut(DateTimeOffset.UtcNow), "A conta deve estar bloqueada por segurança.");

        // 2. Simula múltiplos usuários distintos autenticando com sucesso simultaneamente
        const int validUsersCount = 15;
        for (int i = 1; i <= validUsersCount; i++)
        {
            await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(
                FullName: $"Lojista Valido {i}",
                Email: $"lojista_valido_{i}@gmail.com",
                Password: "SenhaCorreta999!"));
        }

        var successCount = 0;
        await Parallel.ForEachAsync(Enumerable.Range(1, validUsersCount), new ParallelOptions { MaxDegreeOfParallelism = 15 }, async (i, ct) =>
        {
            var res = await env.Auth.LoginWithChallengeAsync(new LoginRequest($"lojista_valido_{i}@gmail.com", "SenhaCorreta999!"), ct);
            if (res?.AuthResult is not null)
            {
                Interlocked.Increment(ref successCount);
            }
        });

        Assert.AreEqual(validUsersCount, successCount, "Todos os lojistas legítimos devem autenticar simultaneamente sem colisão.");
    }

    [TestMethod]
    public async Task StressTest_HundredsOfConcurrentDomainChecks_NoDeadlocks()
    {
        using var client = new HttpClient();
        const int totalChecks = 50;
        var completedChecks = 0;

        await Parallel.ForEachAsync(Enumerable.Range(1, totalChecks), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
        {
            var domainName = $"loja-teste-{i}";
            var result = await GoogleCloudDomainChecker.EvaluateDomainAsync(domainName, client, isTakenLocally: _ => Task.FromResult(false), ct: ct);
            if (result is not null && !string.IsNullOrWhiteSpace(result.Domain))
            {
                Interlocked.Increment(ref completedChecks);
            }
        });

        Assert.AreEqual(totalChecks, completedChecks, "Todas as consultas de domínio concorrentes devem completar com sucesso.");
    }
}
