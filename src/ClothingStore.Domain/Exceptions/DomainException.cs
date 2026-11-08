namespace ClothingStore.Domain.Exceptions;

/// <summary>
/// Exceção lançada quando uma regra de negócio ou invariante de domínio é violada.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}

