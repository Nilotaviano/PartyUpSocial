using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class DomainExceptionTests
{
    [Fact]
    public void Exception_CarriesItsErrorAndMessage()
    {
        DomainException error = new(DomainError.CapacityExceeded, "Session is full.");

        Assert.Equal(DomainError.CapacityExceeded, error.Error);
        Assert.Equal("Session is full.", error.Message);
    }

    [Fact]
    public void Exception_CarriesAnInnerException()
    {
        InvalidOperationException inner = new("inner");

        DomainException error = new(DomainError.Conflict, "Conflicting write.", inner);

        Assert.Equal(DomainError.Conflict, error.Error);
        Assert.Same(inner, error.InnerException);
    }

    [Fact]
    public void DomainError_KeepsItsSeededValuesStable()
    {
        Assert.Equal(0, (int)DomainError.ValidationFailed);
        Assert.Equal(1, (int)DomainError.NotFound);
        Assert.Equal(2, (int)DomainError.Forbidden);
        Assert.Equal(3, (int)DomainError.Conflict);
        Assert.Equal(4, (int)DomainError.Duplicate);
        Assert.Equal(5, (int)DomainError.CapacityExceeded);
        Assert.Equal(6, (int)DomainError.WindowClosed);
        Assert.Equal(7, (int)DomainError.TerminalState);
        Assert.Equal(8, (int)DomainError.InvalidTransition);
        Assert.Equal(9, (int)DomainError.Blocked);
        Assert.Equal(10, (int)DomainError.LimitExceeded);
        Assert.Equal(11, (int)DomainError.RemovalIsPermanent);
        Assert.Equal(12, (int)DomainError.RangeTooLarge);
    }
}
