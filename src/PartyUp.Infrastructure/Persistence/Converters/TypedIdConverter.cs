using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Converters;

public sealed class TypedIdConverter<T>() : ValueConverter<T, Guid>(
    value => value.Value,
    value => From(value))
    where T : struct, ITypedId<T>
{
    private static T From(Guid value) => T.From(value);
}
