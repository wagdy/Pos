using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OtantikPos.Node.Infrastructure.Persistence.Configurations;

internal static class PropertyBuilderExtensions
{
    // Stock quantities are in grams, millilitres or pieces, and a recipe can call for 2.5 g.
    // Money's two decimal places would round that away.
    public static PropertyBuilder<decimal> IsQuantity(this PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 3);

    // Stored by name, as the delivery system stores them. A row stays readable, and adding an
    // enum value can never change what existing rows mean.
    public static PropertyBuilder<TEnum> IsEnumName<TEnum>(this PropertyBuilder<TEnum> property) =>
        property.HasConversion<string>().HasMaxLength(32);
}
