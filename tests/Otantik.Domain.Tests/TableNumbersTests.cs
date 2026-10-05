using Otantik.SharedKernel.Orders;

namespace Otantik.Domain.Tests;

public class TableNumbersTests
{
    // Typed on an Arabic keyboard or a Western one, it is the same table.
    [Theory]
    [InlineData("12", "12")]
    [InlineData(" ١٢ ", "12")]
    [InlineData("۷", "7")]
    [InlineData("Terrace ٣", "Terrace 3")]
    [InlineData("L1", "L1")]
    public void A_table_number_is_kept_trimmed_and_in_western_digits(string typed, string kept) =>
        Assert.Equal(kept, TableNumbers.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_table_number_is_none(string? typed) =>
        Assert.Null(TableNumbers.Normalize(typed));
}
