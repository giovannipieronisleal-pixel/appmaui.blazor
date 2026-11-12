using ClothingStore.Application.Services;
using ClothingStore.Domain.Common;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClothingStore.UnitTests;

/// <summary>
/// Testes extremos: limites, entradas maliciosas, concorrência, corrupção de dados e segurança.
/// </summary>
[TestClass]
public class ExtremeTests
{
    private sealed class Env : IDisposable
    {
        public string DbFile { get; } = Path.Combine(Path.GetTempPath(), $"cs_extreme_{Guid.NewGuid():N}.json");
        public string Uploads { get; } = Path.Combine(Path.GetTempPath(), $"cs_extreme_up_{Guid.NewGuid():N}");
        public Pbkdf2PasswordHasher Hasher { get; } = new();
        public ClothingStoreRepository Repo { get; }
        public AuthService Auth { get; }
        public ProductManagementService Products { get; }
        public StockManagementService Stock { get; }
        public EmployeeAndSettingsService Admin { get; }
        private readonly HttpClient _http = new();

        public Env()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!"
                })
                .Build();

            Repo = new ClothingStoreRepository(Hasher, DbFile);
            var images = new SupabaseImageStorageService(_http, config, Uploads);
            Auth = new AuthService(Repo, Hasher, new JwtTokenService(config));
            Products = new ProductManagementService(Repo, images);
            Stock = new StockManagementService(Repo);
            Admin = new EmployeeAndSettingsService(Repo, Hasher, images);
        }

        public void Dispose()
        {
            _http.Dispose();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(DbFile) + "*"))
                File.Delete(f);
            if (File.Exists(DbFile)) File.Delete(DbFile);
            if (Directory.Exists(Uploads)) Directory.Delete(Uploads, true);
        }
    }

    // ---------------------------------------------------------------
    // 1. CADASTRO / LOGIN
    // ---------------------------------------------------------------

    [TestMethod]
    public async Task Register_FirstRealAccount_BecomesAdmin_SoOnboardingCanSaveStore()
    {
        using var env = new Env();
        var resp = await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Marina Costa", "marina@gmail.com", "1234"));
        Assert.IsTrue(resp.Employee.IsAdmin, "A primeira conta real (além do admin padrão) precisa ser Administradora para configurar a loja no pop-up inicial.");

        // O pop-up de boas-vindas salva as configurações da loja: deve funcionar
        await env.Admin.UpdateStoreSettingsAsync(resp.Employee.EmployeeId, new UpdateStoreSettingsRequest
        {
            StoreName = "Aura Modas",
            StoreSlug = "aura-modas",
            WhatsappNumber = "11988887777",
            StoreDescription = "Moda feminina",
            DefaultLowStockThreshold = 3
        });
    }

    [TestMethod]
    public async Task Register_LaterAccounts_HaveNoPermissionsUntilAdminApproves()
    {
        using var env = new Env();
        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Dona", "dona@loja.com", "1234"));
        var stranger = await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Estranho", "estranho@x.com", "1234"));

        Assert.IsFalse(stranger.Employee.IsAdmin);
        Assert.IsFalse(stranger.Employee.CanManageProducts, "Conta criada livremente não pode mexer em produtos.");
        Assert.IsFalse(stranger.Employee.CanManageStock, "Conta criada livremente não pode mexer em estoque.");
    }

    [TestMethod]
    public async Task Register_DuplicateLogin_CaseAndWhitespaceInsensitive_IsRejected()
    {
        using var env = new Env();
        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("A", "Maria@Gmail.com", "1234"));
        await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("B", "  maria@gmail.COM  ", "1234")));
        await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("C", "ADMIN", "1234")));
    }

    [TestMethod]
    public async Task Register_InvalidInputs_AreRejected()
    {
        using var env = new Env();
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("", "x@x.com", "1234")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("   ", "x@x.com", "1234")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Nome", "   ", "1234")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Nome", "x@x.com", "123")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Nome", "x@x.com", "    ")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(new string('A', 5000), "x@x.com", "1234")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Nome", new string('a', 500) + "@x.com", "1234")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Nome", "x@x.com", new string('p', 1000))));
    }

    [TestMethod]
    public async Task Register_MaliciousAndUnicodeStrings_AreStoredLiterallyAndLoginWorks()
    {
        using var env = new Env();
        var evilName = "<script>alert('x')</script> Robert'); DROP TABLE users;-- 👗✨ Ção";
        var resp = await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest(evilName, "evil@x.com", "s3nh@ çom espaço"));
        Assert.AreEqual(evilName, resp.Employee.FullName);

        var login = await env.Auth.LoginAsync(new LoginRequest("evil@x.com", "s3nh@ çom espaço"));
        Assert.AreEqual(evilName, login.Employee.FullName);

        // Persistência sobrevive a reabrir o arquivo
        var repo2 = new ClothingStoreRepository(env.Hasher, env.DbFile);
        var user = await repo2.GetUserByEmailAsync("evil@x.com");
        Assert.IsNotNull(user);
    }

    [TestMethod]
    public async Task Login_ByFullName_IsNotAllowed_AvoidsAmbiguity()
    {
        using var env = new Env();
        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Maria Silva", "maria1@x.com", "senha1"));
        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Maria Silva", "maria2@x.com", "senha2"));

        // Login deve ser apenas por usuário ou e-mail exatos, nunca pelo nome (ambíguo)
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.LoginAsync(new LoginRequest("Maria Silva", "senha2")));

        var ok = await env.Auth.LoginAsync(new LoginRequest("MARIA2@x.com", "senha2"));
        Assert.AreEqual("maria2@x.com", ok.Employee.Email);
    }

    [TestMethod]
    public async Task Login_BruteForce_LocksAccount_EvenWithCorrectPasswordAfterwards()
    {
        using var env = new Env();
        for (int i = 0; i < User.MaxFailedAttemptsBeforeLockout; i++)
        {
            await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.LoginAsync(new LoginRequest("admin", "errada" + i)));
        }

        var ex = await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.LoginAsync(new LoginRequest("admin", "admin")));
        Assert.IsTrue(ex.Message.Contains("bloqueada"), ex.Message);
    }

    [TestMethod]
    public async Task Login_NullOrEmptyValues_DoNotCrash()
    {
        using var env = new Env();
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.LoginAsync(new LoginRequest("", "")));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.LoginAsync(new LoginRequest(null!, null!)));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.LoginWithChallengeAsync(new LoginRequest("admin", null!)));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest(null!, null!)));
    }

    // ---------------------------------------------------------------
    // 2. PERFIL / PIN / 2FA
    // ---------------------------------------------------------------

    [TestMethod]
    public async Task Profile_ChangeEmailToAnotherUsersLogin_IsRejected()
    {
        using var env = new Env();
        var a = await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("A", "a@x.com", "1234"));
        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("B", "b@x.com", "1234"));

        await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.UpdateUserProfileAsync(a.Employee.UserId, new UpdateUserProfileRequest("A", "B@X.com")));
        await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.UpdateUserProfileAsync(a.Employee.UserId, new UpdateUserProfileRequest("A", "admin")));

        // Trocar para o próprio e-mail (só mudando maiúsculas) é permitido
        var same = await env.Auth.UpdateUserProfileAsync(a.Employee.UserId, new UpdateUserProfileRequest("A Novo", "A@x.com"));
        Assert.AreEqual("a@x.com", same.Email);
        Assert.AreEqual("A Novo", same.FullName);
    }

    [TestMethod]
    public async Task Pin_InvalidFormats_AndWrongPassword_AreRejected()
    {
        using var env = new Env();
        var s = await env.Auth.LoginAsync(new LoginRequest("admin", "admin"));
        var uid = s.Employee.UserId;

        foreach (var bad in new[] { "", "123", "12345", "12a4", "١٢٣٤", " 1234", "12.4", "-123" })
        {
            await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.SetSecurityPinAsync(uid, new SetSecurityPinRequest("admin", bad)),
                $"PIN inválido aceito: '{bad}'");
        }

        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.SetSecurityPinAsync(uid, new SetSecurityPinRequest("errada", "1234")));
        await env.Auth.SetSecurityPinAsync(uid, new SetSecurityPinRequest("admin", "0000"));
        var profile = await env.Auth.GetCurrentProfileAsync(uid);
        Assert.IsTrue(profile.HasSecurityPin);
    }

    [TestMethod]
    public void Totp_MatchesRfc6238ReferenceVector()
    {
        // RFC 6238, segredo ASCII "12345678901234567890", T=59s -> 94287082 (8 dígitos) => 287082 (6 dígitos)
        var secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.AreEqual("287082", TwoFactorHelper.ComputeTotp(secret, 59 / 30));
        // Tolerante a minúsculas e espaços ao digitar a chave
        Assert.AreEqual("287082", TwoFactorHelper.ComputeTotp("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", 1));
        Assert.IsFalse(TwoFactorHelper.VerifyTotp(secret, "abcdef"));
        Assert.IsFalse(TwoFactorHelper.VerifyTotp(secret, ""));
        Assert.IsFalse(TwoFactorHelper.VerifyTotp("", "123456"));
    }

    [TestMethod]
    public async Task TwoFactor_BackupCodeIsSingleUse_WrongCodesLockAccount_DisableNeedsPassword()
    {
        using var env = new Env();
        var s = await env.Auth.LoginAsync(new LoginRequest("admin", "admin"));
        var setup = await env.Auth.GenerateTwoFactorSetupAsync(s.Employee.UserId);

        // Código errado na ativação não ativa
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.EnableTwoFactorAsync(s.Employee.UserId,
            new EnableTwoFactorRequest(setup.SecretKey, "000000", setup.BackupCodes)));

        var otp = TwoFactorHelper.GenerateCurrentTotp(setup.SecretKey);
        await env.Auth.EnableTwoFactorAsync(s.Employee.UserId, new EnableTwoFactorRequest(setup.SecretKey, otp, setup.BackupCodes));

        // LoginAsync "simples" nunca pode furar o 2FA
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Auth.LoginAsync(new LoginRequest("admin", "admin")));

        // Código de backup funciona uma única vez
        var backup = setup.BackupCodes[1];
        await env.Auth.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("admin", backup));
        await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("admin", backup)));

        // O backup consumido continua consumido após reabrir o banco (persistido)
        var repo2 = new ClothingStoreRepository(env.Hasher, env.DbFile);
        var userReloaded = await repo2.GetUserByEmailAsync("admin");
        Assert.AreEqual(3, userReloaded!.TwoFactorBackupCodes.Count);

        // Desativar exige senha correta
        await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.DisableTwoFactorAsync(s.Employee.UserId, new DisableTwoFactorRequest("errada")));

        // Códigos 2FA errados em sequência bloqueiam a conta
        for (int i = 0; i < User.MaxFailedAttemptsBeforeLockout; i++)
        {
            await Assert.ThrowsExceptionAsync<DomainException>(() =>
                env.Auth.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("admin", "11111" + i)));
        }
        var lockEx = await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("admin", TwoFactorHelper.GenerateCurrentTotp(setup.SecretKey))));
        Assert.IsTrue(lockEx.Message.Contains("bloqueada"), lockEx.Message);
    }

    [TestMethod]
    public async Task TwoFactor_VerifyWithoutPasswordStep_ForAccountWithout2FA_IsRejected()
    {
        using var env = new Env();
        // Conta sem 2FA: chamar a verificação 2FA direto não pode logar ninguém
        await Assert.ThrowsExceptionAsync<DomainException>(() =>
            env.Auth.VerifyTwoFactorLoginAsync(new VerifyTwoFactorLoginRequest("admin", "123456")));
    }

    // ---------------------------------------------------------------
    // 3. LOJA / WHATSAPP / SLUG
    // ---------------------------------------------------------------

    [TestMethod]
    public void WhatsApp_EdgeCases()
    {
        Assert.AreEqual("5511988887777", StoreSettings.SanitizeWhatsappNumber("11988887777"));
        Assert.AreEqual("5511988887777", StoreSettings.SanitizeWhatsappNumber("+55 (11) 98888-7777"));
        Assert.AreEqual("552133334444", StoreSettings.SanitizeWhatsappNumber("(21) 3333-4444"));
        // DDD 55 (Rio Grande do Sul) também precisa receber o +55 do Brasil
        Assert.AreEqual("5555991234567", StoreSettings.SanitizeWhatsappNumber("55991234567"));
        Assert.AreEqual("5555991234567", StoreSettings.SanitizeWhatsappNumber("5555991234567"));

        Assert.ThrowsException<DomainException>(() => StoreSettings.SanitizeWhatsappNumber(""));
        Assert.ThrowsException<DomainException>(() => StoreSettings.SanitizeWhatsappNumber("abc"));
        Assert.ThrowsException<DomainException>(() => StoreSettings.SanitizeWhatsappNumber("123456789"));
        Assert.ThrowsException<DomainException>(() => StoreSettings.SanitizeWhatsappNumber("1234567890123456"));
    }

    [TestMethod]
    public void Slug_EdgeCases()
    {
        Assert.AreEqual("marina-cia-cao", WhatsAppLinkBuilder.GenerateStoreSlug("  Marina & Cia   Ção!!  "));
        Assert.AreEqual("minha-loja", WhatsAppLinkBuilder.GenerateStoreSlug("👗✨🔥"));
        Assert.AreEqual("minha-loja", WhatsAppLinkBuilder.GenerateStoreSlug("   "));
        Assert.AreEqual("minha-loja", WhatsAppLinkBuilder.GenerateStoreSlug(null));
        Assert.AreEqual(Category.GenerateSlug("Marina & Cia Ção"), WhatsAppLinkBuilder.GenerateStoreSlug("Marina & Cia Ção"),
            "App e domínio precisam gerar o mesmo link para a mesma loja.");
        var longSlug = WhatsAppLinkBuilder.GenerateStoreSlug(new string('a', 3000));
        Assert.IsTrue(longSlug.Length <= 80, $"Slug gigante: {longSlug.Length} caracteres");
    }

    [TestMethod]
    public async Task StoreSettings_InvalidValues_AreRejected()
    {
        using var env = new Env();
        var s = await env.Auth.LoginAsync(new LoginRequest("admin", "admin"));
        UpdateStoreSettingsRequest Make(string name, string wa, string desc, int low) => new()
        {
            StoreName = name, WhatsappNumber = wa, StoreDescription = desc, DefaultLowStockThreshold = low
        };

        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Admin.UpdateStoreSettingsAsync(s.Employee.EmployeeId, Make("", "11988887777", "x", 3)));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Admin.UpdateStoreSettingsAsync(s.Employee.EmployeeId, Make("Loja", "", "x", 3)));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Admin.UpdateStoreSettingsAsync(s.Employee.EmployeeId, Make("Loja", "11988887777", "", 3)));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Admin.UpdateStoreSettingsAsync(s.Employee.EmployeeId, Make("Loja", "11988887777", "x", -1)));
        await Assert.ThrowsExceptionAsync<DomainException>(() => env.Admin.UpdateStoreSettingsAsync(s.Employee.EmployeeId, Make(new string('L', 1000), "11988887777", "x", 3)));

        // Nenhuma tentativa inválida pode ter estragado as configurações salvas
        var current = await env.Admin.GetAdminStoreSettingsAsync();
        Assert.AreEqual("Minha Loja", current.StoreName);
    }

    // ---------------------------------------------------------------
    // 4. ESTOQUE
    // ---------------------------------------------------------------

    [TestMethod]
    public void Stock_HugeQuantities_DoNotOverflow()
    {
        var product = new Product(Guid.NewGuid(), "Meia", "x", 10m);
        var v = product.AddVariant("Preto", "U");
        var emp = Guid.NewGuid();

        Assert.ThrowsException<DomainException>(() => v.ApplyStockMovement(emp, StockMovementType.In, int.MaxValue));
        v.ApplyStockMovement(emp, StockMovementType.In, 1_000_000);
        Assert.ThrowsException<DomainException>(() => v.ApplyStockMovement(emp, StockMovementType.Adjustment, int.MaxValue));
        Assert.IsTrue(v.StockQuantity >= 0);
        Assert.ThrowsException<DomainException>(() => v.ApplyStockMovement(emp, StockMovementType.Out, 0));
        Assert.ThrowsException<DomainException>(() => v.ApplyStockMovement(emp, StockMovementType.In, -5));
        Assert.ThrowsException<DomainException>(() => v.ApplyStockMovement(Guid.Empty, StockMovementType.In, 1));
    }

    [TestMethod]
    public async Task Stock_100ParallelMovements_NoLostUpdates()
    {
        using var env = new Env();
        var s = await env.Auth.LoginAsync(new LoginRequest("admin", "admin"));
        var product = await env.Products.CreateProductAsync(new SaveProductRequest
        {
            CategoryName = "Testes", Name = "Camiseta Stress", Description = "x", BasePrice = 10m, IsVisibleOnSite = true
        }, s.Employee.EmployeeId);
        var variant = await env.Products.AddVariantAsync(product.Id, s.Employee.EmployeeId, new SaveProductVariantRequest
        {
            Color = "Azul", Size = "M", InitialStockQuantity = 0, LowStockThreshold = 1, IsActive = true
        });

        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            env.Stock.RegisterMovementAsync(s.Employee.EmployeeId, new CreateStockMovementRequest
            {
                ProductVariantId = variant.Id, MovementType = "IN", Quantity = 1, Reason = "stress"
            })));
        await Task.WhenAll(tasks);

        var reloaded = new ClothingStoreRepository(env.Hasher, env.DbFile);
        var v = await reloaded.GetVariantByIdAsync(variant.Id);
        Assert.AreEqual(100, v!.StockQuantity, "Movimentações simultâneas se perderam.");

        // 150 saídas simultâneas de 1 unidade: exatamente 100 devem passar e o saldo nunca fica negativo
        var outs = Enumerable.Range(0, 150).Select(_ => Task.Run(async () =>
        {
            try
            {
                await env.Stock.RegisterMovementAsync(s.Employee.EmployeeId, new CreateStockMovementRequest
                {
                    ProductVariantId = variant.Id, MovementType = "OUT", Quantity = 1, Reason = "venda"
                });
                return true;
            }
            catch (DomainException) { return false; }
        })).ToList();
        var results = await Task.WhenAll(outs);
        Assert.AreEqual(100, results.Count(r => r), "Vendeu mais do que havia em estoque.");

        var final = await new ClothingStoreRepository(env.Hasher, env.DbFile).GetVariantByIdAsync(variant.Id);
        Assert.AreEqual(0, final!.StockQuantity);
    }

    // ---------------------------------------------------------------
    // 5. BANCO DE DADOS / ARQUIVO
    // ---------------------------------------------------------------

    [TestMethod]
    public async Task Database_CorruptedFile_IsBackedUp_NotSilentlyWiped()
    {
        using var env = new Env();
        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("Dona", "dona@loja.com", "1234"));

        // Simula queda de energia no meio da gravação (arquivo truncado)
        var json = await File.ReadAllTextAsync(env.DbFile);
        await File.WriteAllTextAsync(env.DbFile, json[..(json.Length / 2)]);

        _ = new ClothingStoreRepository(env.Hasher, env.DbFile);

        var backups = Directory.GetFiles(Path.GetDirectoryName(env.DbFile)!, Path.GetFileName(env.DbFile) + ".corrupt-*");
        Assert.IsTrue(backups.Length >= 1, "O arquivo corrompido foi sobrescrito sem backup: todos os dados da loja seriam perdidos.");
    }

    [TestMethod]
    public async Task Database_ConcurrentWritesFromTwoInstances_KeepBothUsers()
    {
        using var env = new Env();
        var repoB = new ClothingStoreRepository(env.Hasher, env.DbFile);
        var authB = new AuthService(repoB, env.Hasher, new JwtTokenService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SecretKey"] = "TestSecretKeyForUnitTesting_1234567890_ABCDEF!" }).Build()));

        await env.Auth.RegisterUserAccountAsync(new RegisterUserAccountRequest("App", "app@x.com", "1234"));
        await authB.RegisterUserAccountAsync(new RegisterUserAccountRequest("Site", "site@x.com", "1234"));

        var check = new ClothingStoreRepository(env.Hasher, env.DbFile);
        Assert.IsNotNull(await check.GetUserByEmailAsync("app@x.com"));
        Assert.IsNotNull(await check.GetUserByEmailAsync("site@x.com"));
    }

    // ---------------------------------------------------------------
    // 6. VALIDAÇÃO DE DOMÍNIO NO GOOGLE CLOUD
    // ---------------------------------------------------------------

    [TestMethod]
    public async Task GoogleCloudDomain_Validation_EmptyAndInvalid_AreRejected()
    {
        using var client = new HttpClient();

        var emptyRes = await GoogleCloudDomainChecker.EvaluateDomainAsync(null, client);
        Assert.IsFalse(emptyRes.IsAvailable);
        Assert.AreEqual("InvalidFormat", emptyRes.Status);

        var spaceRes = await GoogleCloudDomainChecker.EvaluateDomainAsync("   ", client);
        Assert.IsFalse(spaceRes.IsAvailable);
        Assert.AreEqual("InvalidFormat", spaceRes.Status);

        var invalidCharRes = await GoogleCloudDomainChecker.EvaluateDomainAsync("loja!@#$.com", client);
        Assert.IsFalse(invalidCharRes.IsAvailable);
        Assert.AreEqual("InvalidFormat", invalidCharRes.Status);
    }

    [TestMethod]
    public async Task GoogleCloudDomain_Normalization_And_LocalCollision_GeneratesSuggestions()
    {
        using var client = new HttpClient();

        // Se passar apenas o nome da loja sem pontos, deve formatar automaticamente como lojas.nomedaloja.com
        var defaultRes = await GoogleCloudDomainChecker.EvaluateDomainAsync("auramodas", client);
        Assert.AreEqual("lojas.auramodas.com", defaultRes.Domain);
        Assert.AreEqual("https://lojas.auramodas.com", defaultRes.NormalizedUrl);

        // Se passar um domínio e simular que já existe na base de dados das lojas:
        var takenRes = await GoogleCloudDomainChecker.EvaluateDomainAsync(
            "https://lojas.minhaloja.com/teste",
            client,
            isTakenLocally: domain => Task.FromResult(domain == "lojas.minhaloja.com"));

        Assert.IsFalse(takenRes.IsAvailable);
        Assert.AreEqual("AlreadyTaken", takenRes.Status);
        Assert.AreEqual("lojas.minhaloja.com", takenRes.Domain);
        Assert.IsTrue(takenRes.Suggestions.Count >= 2);
        Assert.IsTrue(takenRes.Suggestions.Contains("lojas.minhalojaoficial.com"));
    }

    [TestMethod]
    public void Store_DefaultDomainPattern_ShouldBe_Lojas_NomeDaLoja_Com()
    {
        Assert.AreEqual("lojas.auramodas.com", WhatsAppLinkBuilder.GenerateDefaultStoreDomain("Aura Modas"));
        Assert.AreEqual("https://lojas.auramodas.com", WhatsAppLinkBuilder.GenerateDefaultStoreUrl("Aura Modas"));
        Assert.AreEqual("https://lojas.auramodas.com", WhatsAppLinkBuilder.BuildStoreCatalogUrl("https://lojas.auramodas.com", "aura-modas"));
        Assert.AreEqual("https://lojas.auramodas.com", WhatsAppLinkBuilder.BuildStoreCatalogUrl(null, "Aura Modas"));
    }

    [TestMethod]
    public void Store_NameChange_DeactivatesSite_UntilReactivated()
    {
        var settings = new StoreSettings("Aura Modas", "11987654321", "Descrição inicial");
        Assert.IsTrue(settings.IsSiteActive);
        Assert.AreEqual("Aura Modas", settings.PublishedStoreName);

        // Altera o nome da loja
        settings.UpdateSettings("Aura Elegance", "11987654321", "Nova descrição", defaultLowStockThreshold: 5);

        // Regra do usuário: Mudou de nome -> site desativado automaticamente
        Assert.IsFalse(settings.IsSiteActive);
        Assert.IsNotNull(settings.DeactivationReason);
        Assert.IsTrue(settings.DeactivationReason!.Contains("Aura Modas") && settings.DeactivationReason.Contains("Aura Elegance"));

        // Reativa o site
        settings.ActivateSite();
        Assert.IsTrue(settings.IsSiteActive);
        Assert.IsNull(settings.DeactivationReason);
        Assert.AreEqual("Aura Elegance", settings.PublishedStoreName);
    }

    [TestMethod]
    public void Store_ThreeWeeksInactivity_DeactivatesSite()
    {
        var settings = new StoreSettings("Loja Aura", "11987654321", "Descrição");
        Assert.IsTrue(settings.IsSiteActive);

        var baseline = settings.LastActivityAt;

        // Com 20 dias (menos de 3 semanas = 21 dias): permanece ativo
        var day20 = baseline.AddDays(20);
        var changedAt20 = settings.CheckAndEnforceInactivity(day20);
        Assert.IsFalse(changedAt20);
        Assert.IsTrue(settings.IsSiteActive);

        // Com 21.5 dias (mais de 3 semanas): desativa automaticamente
        var day22 = baseline.AddDays(22);
        var changedAt22 = settings.CheckAndEnforceInactivity(day22);
        Assert.IsTrue(changedAt22);
        Assert.IsFalse(settings.IsSiteActive);
        Assert.IsNotNull(settings.DeactivationReason);
        Assert.IsTrue(settings.DeactivationReason!.Contains("3 semanas (21 dias)"));

        // Reativar restaura o site
        settings.ActivateSite();
        Assert.IsTrue(settings.IsSiteActive);
        Assert.IsNull(settings.DeactivationReason);
    }
}

