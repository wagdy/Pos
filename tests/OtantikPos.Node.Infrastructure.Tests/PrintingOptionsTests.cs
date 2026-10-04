using Microsoft.Extensions.Configuration;
using OtantikPos.Node.Infrastructure.Printing;

namespace OtantikPos.Node.Infrastructure.Tests;

public class PrintingOptionsTests
{
    // The binder adds configured items to a list that already has some. With a default line
    // in the class, every receipt printed the restaurant's name and the thank-you twice.
    [Fact]
    public void The_receipt_header_and_footer_are_exactly_what_is_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Printing:ReceiptHeader:0"] = "Otantik Zamalek",
                ["Printing:ReceiptHeader:1"] = "26 July Street",
                ["Printing:ReceiptFooter:0"] = "Thank you!",
            })
            .Build();

        var options = configuration.GetSection(PrintingOptions.Section).Get<PrintingOptions>()!;

        Assert.Equal(["Otantik Zamalek", "26 July Street"], options.ReceiptHeader);
        Assert.Equal(["Thank you!"], options.ReceiptFooter);
    }
}
