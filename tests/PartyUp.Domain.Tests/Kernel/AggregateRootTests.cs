using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class AggregateRootTests
{
    [Fact]
    public void DequeueEvents_IsEmptyForAFreshAggregate()
    {
        Assert.Empty(new TestAggregate().DequeueEvents());
    }

    [Fact]
    public void DequeueEvents_ReturnsRaisedEventsInOrder()
    {
        TestAggregate aggregate = new();
        TestEvent first = new(1);
        TestEvent second = new(2);

        aggregate.RaisePublic(first);
        aggregate.RaisePublic(second);

        Assert.Equal<IDomainEvent>([first, second], aggregate.DequeueEvents());
    }

    [Fact]
    public void DequeueEvents_DrainsTheQueue()
    {
        TestAggregate aggregate = new();
        aggregate.RaisePublic(new TestEvent(1));

        Assert.Single(aggregate.DequeueEvents());
        Assert.Empty(aggregate.DequeueEvents());
    }

    [Fact]
    public void DequeueEvents_ReturnsASnapshotUnaffectedByLaterRaises()
    {
        TestAggregate aggregate = new();
        aggregate.RaisePublic(new TestEvent(1));

        IReadOnlyList<IDomainEvent> drained = aggregate.DequeueEvents();
        aggregate.RaisePublic(new TestEvent(2));

        Assert.Single(drained);
        Assert.Single(aggregate.DequeueEvents());
    }

    [Fact]
    public void Raise_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new TestAggregate().RaisePublic(null!));
    }

    [Fact]
    public void EachAggregateInstance_OwnsItsQueue()
    {
        TestAggregate first = new();
        TestAggregate second = new();

        first.RaisePublic(new TestEvent(1));

        Assert.Single(first.DequeueEvents());
        Assert.Empty(second.DequeueEvents());
    }

    private sealed record TestEvent(int Ordinal) : IDomainEvent;

    private sealed class TestAggregate : AggregateRoot
    {
        public void RaisePublic(IDomainEvent domainEvent) => Raise(domainEvent);
    }
}
