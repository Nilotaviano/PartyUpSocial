using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Converters;

public sealed class TextValueConverter<T>() : ValueConverter<T, string>(
    value => value.Value,
    value => Create(value))
    where T : struct, ITextValue<T>
{
    private static T Create(string value) => T.Create(value);
}
