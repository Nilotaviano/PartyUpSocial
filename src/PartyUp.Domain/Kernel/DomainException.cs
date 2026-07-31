namespace PartyUp.Domain.Kernel;

public sealed class DomainException : Exception
{
    public DomainException(DomainError error, string message)
        : base(message) => Error = error;

    public DomainException(DomainError error, string message, Exception innerException)
        : base(message, innerException) => Error = error;

    public DomainError Error { get; }
}
