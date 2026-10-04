using System.Text;

namespace OtantikPos.Node.Infrastructure.Printing;

// A small builder over the ESC/POS commands both tickets and receipts use.
//
// Text goes out in a single-byte code page (PrinterOptions.CodePage), so a character that page
// lacks prints as '?'. Arabic is one of those: printing Arabic names needs the document
// rendered to a bitmap and sent as a raster image (GS v 0) instead of text.
internal sealed class EscPosDocument
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;

    private readonly MemoryStream _output = new();
    private readonly Encoding _encoding;

    static EscPosDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public EscPosDocument(PrinterOptions printer)
    {
        Width = printer.CharactersPerLine;
        _encoding = Encoding.GetEncoding(printer.CodePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        Bytes(Esc, (byte)'@');                    // reset
        Bytes(Esc, (byte)'t', printer.CodeTable); // code page
    }

    public int Width { get; }

    public EscPosDocument Center() => Bytes(Esc, (byte)'a', 1);
    public EscPosDocument Left() => Bytes(Esc, (byte)'a', 0);
    public EscPosDocument Bold(bool on) => Bytes(Esc, (byte)'E', (byte)(on ? 1 : 0));
    public EscPosDocument Large() => Bytes(Gs, (byte)'!', 0x11);       // double width and height
    public EscPosDocument Tall() => Bytes(Gs, (byte)'!', 0x01);        // double height
    public EscPosDocument Normal() => Bytes(Gs, (byte)'!', 0x00);
    public EscPosDocument Inverse(bool on) => Bytes(Gs, (byte)'B', (byte)(on ? 1 : 0));

    public EscPosDocument Line(string text = "")
    {
        _output.Write(_encoding.GetBytes(text + "\n"));
        return this;
    }

    public EscPosDocument Rule() => Line(new string('-', Width));

    // "Burger x2 ........ 270.00": text on the left, an amount right-aligned.
    public EscPosDocument Columns(string left, string right)
    {
        var room = Width - right.Length - 1;
        if (left.Length > room)
            left = left[..Math.Max(0, room)];
        return Line(left.PadRight(room) + " " + right);
    }

    public byte[] FinishAndCut()
    {
        Bytes(Esc, (byte)'d', 4);   // feed past the cutter
        Bytes(Gs, (byte)'V', 1);    // partial cut
        return _output.ToArray();
    }

    private EscPosDocument Bytes(params byte[] bytes)
    {
        _output.Write(bytes);
        return this;
    }
}
