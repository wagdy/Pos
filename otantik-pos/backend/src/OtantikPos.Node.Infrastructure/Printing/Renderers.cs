using System.Globalization;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Infrastructure.Printing;

internal static class KitchenTicketRenderer
{
    public static byte[] Render(KitchenTicket ticket, DateTime localTime, PrinterOptions printer)
    {
        var doc = new EscPosDocument(printer).Center();

        if (ticket.Kind == KitchenTicketKind.Void)
        {
            // White on black, so a void cannot be mistaken for a new order at a glance.
            doc.Inverse(true).Large().Line(" ** VOID ** ").Inverse(false);
        }

        doc.Large().Line(ticket.OrderLabel).Normal()
            .Line(localTime.ToString("HH:mm  dd/MM", CultureInfo.InvariantCulture))
            .Left()
            .Rule();

        foreach (var line in ticket.Lines)
        {
            // Double height, bold: read from across the pass.
            doc.Tall().Bold(true).Line($"{line.Quantity} x {line.Name}").Bold(false).Normal();
            foreach (var addOn in line.AddOns)
                doc.Line($"   + {addOn}");
            if (line.Notes is not null)
                doc.Line($"   > {line.Notes}");
        }

        doc.Rule();
        if (ticket.Reason is not null)
            doc.Line($"Reason: {ticket.Reason}");

        return doc.FinishAndCut();
    }
}

// The customer's final receipt, laid out from the shared Order. It shows what was charged
// (TotalAmount) and, separately, anything refunded since. A receipt reprinted after a Void After
// still shows the original sale, with the refund on its own line beneath.
internal static class ReceiptRenderer
{
    public static byte[] Render(Order order, decimal? cashReceived, DateTime localTime, PrinterOptions printer, PrintingOptions options)
    {
        var doc = new EscPosDocument(printer).Center();

        doc.Bold(true);
        foreach (var line in options.ReceiptHeader)
            doc.Line(line);
        doc.Bold(false)
            .Line(Label(order))
            .Line(localTime.ToString("dd/MM/yyyy  HH:mm", CultureInfo.InvariantCulture))
            .Left()
            .Rule();

        // Items voided before payment were never charged and are left off. Items refunded
        // after payment were charged, and stay.
        foreach (var item in order.OrderItems.Where(i => i.VoidType is null or VoidType.AfterPayment))
        {
            var name = item.VariantName is null ? item.MenuItemName : $"{item.MenuItemName} ({item.VariantName})";
            doc.Columns($"{item.Quantity} x {name}", Money(item.LineTotal));
            foreach (var addOn in item.AddOns)
                doc.Line($"   + {addOn.Name}");
        }

        doc.Rule().Columns("Subtotal", Money(order.BilledItemsSubtotal));
        if (order.DiscountAmount > 0)
            doc.Columns($"Discount {order.PromoCodeText}".TrimEnd(), "-" + Money(order.DiscountAmount));
        if (order.TaxAmount > 0)
            doc.Columns("Tax", Money(order.TaxAmount));
        if (order.DeliveryFee > 0)
            doc.Columns("Delivery", Money(order.DeliveryFee - order.DeliveryDiscountAmount));
        if (order.PointsRedeemed > 0)
            doc.Columns($"Points ({order.PointsRedeemed})", "-" + Money(order.PointsDiscountAmount));

        doc.Bold(true).Tall().Columns("TOTAL", Money(order.TotalAmount)).Normal().Bold(false);
        // Visa is only the stored name: the card machine takes any card.
        doc.Columns($"Paid ({(order.PaymentMethod == PaymentMethod.Visa ? "Card" : order.PaymentMethod.ToString())})", Money(order.TotalAmount));
        if (cashReceived is { } received && order.PaymentMethod == PaymentMethod.Cash)
        {
            doc.Columns("Cash received", Money(received));
            doc.Columns("Change", Money(received - order.TotalAmount));
        }
        if (order.RefundedAmount > 0)
            doc.Columns("Refunded", "-" + Money(order.RefundedAmount));

        doc.Rule().Center();
        foreach (var line in options.ReceiptFooter)
            doc.Line(line);

        return doc.FinishAndCut();
    }

    private static string Label(Order order) => order.Type switch
    {
        OrderType.DineIn => $"Table {order.TableNumber} - Order #{order.Id}",
        OrderType.Takeaway => $"Takeaway - Order #{order.Id}",
        _ => $"Delivery - Order #{order.Id}",
    };

    private static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);
}
