namespace PartyUp.Domain.Kernel;

public interface ITypedId<TSelf>
    where TSelf : struct, ITypedId<TSelf>
{
    Guid Value { get; }

    // CA1716 forbids a virtual/interface member named New, so the uniform New() factory lives on
    // each concrete id type instead of here.
    static abstract TSelf From(Guid value);
}
