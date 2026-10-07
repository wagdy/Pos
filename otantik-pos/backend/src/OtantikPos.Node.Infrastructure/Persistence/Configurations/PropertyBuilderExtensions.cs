using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OtantikPos.Node.Infrastructure.Persistence.Configurations;

internal static class PropertyBuilderExtensions
{
    // Stock quantities are in grams, millilitres or pieces, and a recipe can call for 2.5 g.
    // Money's two decimal places would round that away.
    public static PropertyBuilder<decimal> IsQuantity(this PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 3);

    public static PropertyBuilder<decimal?> IsQuantity(this PropertyBuilder<decimal?> property) =>
        property.HasPrecision(18, 3);

    // The cost of one gram, millilitre or piece: 0.36 a gram for beef at 360 a kg. Money's two
    // places would make it 0.36 and lose the rest of a 359.99 kg.
    public static PropertyBuilder<decimal?> IsUnitCost(this PropertyBuilder<decimal?> property) =>
        property.HasPrecision(18, 6);

    // Stored by name, as the delivery system stores them. A row stays readable, and adding an
    // enum value can never change what existing rows mean.
    public static PropertyBuilder<TEnum> IsEnumName<TEnum>(this PropertyBuilder<TEnum> property) =>
        property.HasConversion<string>().HasMaxLength(32);
}
