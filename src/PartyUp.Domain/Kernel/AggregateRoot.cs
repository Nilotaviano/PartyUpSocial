namespace PartyUp.Domain.Kernel;

public abstract class AggregateRoot
{
    // Deliberately a field with no property: EF maps properties by convention and must not see this.
    private readonly List<IDomainEvent> _domainEvents = [];

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public IReadOnlyList<IDomainEvent> DequeueEvents()
    {
        IDomainEvent[] drained = [.. _domainEvents];
        _domainEvents.Clear();
        return drained;
    }
}
