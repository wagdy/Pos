namespace OtantikPos.Node.Infrastructure.Printing;

public sealed class PrintingOptions
{
    public const string Section = "Printing";
    public const string Kitchen = "Kitchen";
    public const string Receipt = "Receipt";

    // Keyed by printer name: "Kitchen" for tickets, "Receipt" for the customer's bill at the
    // till. They can be the same physical printer.
    public Dictionary<string, PrinterOptions> Printers { get; set; } = new();

    // Printed at the top and bottom of every receipt: the restaurant's name and address, a
    // thank-you, a tax registration number.
    public List<string> ReceiptHeader { get; set; } = ["Otantik"];
    public List<string> ReceiptFooter { get; set; } = ["Thank you!"];
}

public sealed class PrinterOptions
{
    // The printer's LAN address. Give it a fixed IP (a DHCP reservation) or printing will
    // start failing the first time the router hands it a new one.
    public string Host { get; set; } = string.Empty;

    // 9100 is the raw ESC/POS port on practically every network thermal printer.
    public int Port { get; set; } = 9100;

    // 48 for 80 mm paper, 32 for 58 mm, in the printer's default font.
    public int CharactersPerLine { get; set; } = 48;

    // The single-byte code page text is encoded in, and the ESC t number that selects the same
    // page on the printer; the two must agree. Defaults: Windows-1252, table 16 on Epson and
    // most compatibles. Arabic cannot be printed this way: it needs the ticket sent as an image.
    public int CodePage { get; set; } = 1252;
    public byte CodeTable { get; set; } = 16;
}
