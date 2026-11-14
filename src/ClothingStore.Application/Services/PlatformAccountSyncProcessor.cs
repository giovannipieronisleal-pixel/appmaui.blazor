using System.Security.Cryptography;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Processador compartilhado e idempotente para sincronizar contas criadas offline no celular/PC
/// para o repositório central (API ou Receptor LAN do YouLink Admin) sem elevar privilégios
/// e sem sobrescrever senhas ou dados de usuários já existentes.
/// </summary>
public static class PlatformAccountSyncProcessor
{
    public const int MaxBatchSize = 50;

    public static async Task<SyncLocalAccountsResultDto> ProcessSyncBatchAsync(
        SyncLocalAccountsRequest request,
        IClothingStoreRepository repository,
        IPasswordHasher passwordHasher,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(passwordHasher);

        if (request.Accounts is null || request.Accounts.Count == 0)
        {
            return new SyncLocalAccountsResultDto(0, 0, "Nenhuma conta pendente para sincronizar.");
        }

        if (request.Accounts.Count > MaxBatchSize)
        {
            throw new DomainException($"Limite máximo de {MaxBatchSize} contas por envio de sincronização.");
        }

        var existingEmployees = await repository.GetEmployeesAsync(ct);
        var hasActiveAdmin = existingEmployees.Any(e =>
            e.IsActive &&
            e.User is { IsActive: true } u &&
            string.Equals(u.Role?.Name, RoleNames.Admin, StringComparison.OrdinalIgnoreCase));

        var adminRole = await repository.GetRoleByNameAsync(RoleNames.Admin, ct);
        var employeeRole = await repository.GetRoleByNameAsync(RoleNames.Employee, ct)
            ?? adminRole
            ?? throw new DomainException("Perfil de acesso padrão não encontrado.");

        var importedCount = 0;
        var skippedExistingCount = 0;

        foreach (var rawItem in request.Accounts)
        {
            if (rawItem is null || string.IsNullOrWhiteSpace(rawItem.FullName))
                continue;

            var normalizedEmail = string.IsNullOrWhiteSpace(rawItem.Email)
                ? null
                : rawItem.Email.Trim().ToLowerInvariant();
            var normalizedUsername = string.IsNullOrWhiteSpace(rawItem.Username)
                ? null
                : rawItem.Username.Trim().TrimStart('@').ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(normalizedEmail) && string.IsNullOrWhiteSpace(normalizedUsername))
                continue;

            if (normalizedEmail is not null && normalizedEmail.Length > 254)
                normalizedEmail = normalizedEmail[..254];

            if (normalizedUsername is not null && normalizedUsername.Length > 60)
                normalizedUsername = normalizedUsername[..60];

            var primaryIdentifier = normalizedEmail ?? normalizedUsername!;

            // Nunca sobrescreve nem altera usuários já existentes no destino
            var byEmail = await repository.GetUserByEmailAsync(primaryIdentifier, ct);
            var byUsername = normalizedUsername is not null
                ? await repository.GetUserByEmailAsync(normalizedUsername, ct)
                : null;

            if (byEmail is not null || byUsername is not null)
            {
                skippedExistingCount++;
                continue;
            }

            // Segurança: se já existe um administrador ativo no servidor/Admin, novas contas sincronizadas sempre entram como Employee
            var targetRole = !hasActiveAdmin && adminRole is not null ? adminRole : employeeRole;
            if (string.Equals(targetRole.Name, RoleNames.Admin, StringComparison.OrdinalIgnoreCase))
                hasActiveAdmin = true;

            // Gera segredo aleatório criptográfico para impedir login com senha vazia ou conhecida via endpoint de sync
            var generatedSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var passwordHash = passwordHasher.HashPassword(generatedSecret);

            var user = new User(primaryIdentifier, passwordHash, targetRole.Id, normalizedUsername)
            {
                Role = targetRole,
                IsActive = rawItem.IsActive
            };

            var fullName = rawItem.FullName.Trim();
            if (fullName.Length > 100)
                fullName = fullName[..100];

            var phone = string.IsNullOrWhiteSpace(rawItem.Phone) ? null : rawItem.Phone.Trim();
            if (phone is not null && phone.Length > 30)
                phone = phone[..30];

            var storeHint = string.IsNullOrWhiteSpace(rawItem.StoreName)
                ? "Conta Sincronizada (Celular/App)"
                : $"Conta Sincronizada ({rawItem.StoreName.Trim()})";
            if (storeHint.Length > 80)
                storeHint = storeHint[..80];

            var employee = new Employee(
                user.Id,
                fullName,
                storeHint,
                phone,
                canManageProducts: false,
                canManageStock: false)
            {
                User = user,
                IsActive = rawItem.IsActive
            };

            await repository.AddEmployeeWithUserAsync(user, employee, ct);
            importedCount++;
        }

        var message = importedCount > 0
            ? $"{importedCount} conta(s) sincronizada(s) com o YouLink Admin" +
              (skippedExistingCount > 0 ? $" ({skippedExistingCount} já existente(s))." : ".")
            : skippedExistingCount > 0
                ? $"Todas as {skippedExistingCount} conta(s) deste aparelho já estão no YouLink Admin."
                : "Nenhuma conta válida encontrada para sincronizar.";

        return new SyncLocalAccountsResultDto(importedCount, skippedExistingCount, message);
    }
}
