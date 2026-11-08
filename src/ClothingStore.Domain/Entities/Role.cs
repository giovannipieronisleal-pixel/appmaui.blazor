using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Domain.Entities;

/// <summary>
/// Representa um perfil de acesso (Role) do sistema (ex.: Admin, Employee).
/// </summary>
public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DefaultPermissionsJson { get; set; } = "{}";

    public Role()
    {
    }

    public Role(Guid id, string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("O nome do perfil de acesso é obrigatório.");

        Id = id;
        Name = name.Trim();
        Description = description?.Trim();
    }
}
