namespace OtantikPos.Node.Infrastructure.Printing;

// A printer holding tickets it cannot print: switched off, out of paper, unplugged, or with no
// address set. Printer is its name in Printing:Printers (Kitchen, Receipt); Problem is the last
// error, for the tooltip.
public sealed record PrinterProblem(string Printer, string Problem, int Waiting, DateTime OldestWaitingSinceUtc);

// The printers the tills should warn about, as the print worker last found them. Without it, a
// kitchen printer out of paper showed nothing at the till: the cashier saw "sent to the kitchen"
// while the tickets piled up, and the only warning was a line in the server's log.
public sealed class PrinterStatus
{
    private readonly Lock _gate = new();
    private IReadOnlyList<PrinterProblem> _current = [];

    public IReadOnlyList<PrinterProblem> Current
    {
        get { lock (_gate) return _current; }
    }

    // Raised on a real change only (a printer failing or printing again, a ticket more or fewer
    // waiting), outside the lock.
    public event Action<IReadOnlyList<PrinterProblem>>? Changed;

    public void Set(IReadOnlyList<PrinterProblem> problems)
    {
        lock (_gate)
        {
            if (_current.SequenceEqual(problems))
                return;
            _current = problems;
        }
        Changed?.Invoke(problems);
    }
}
