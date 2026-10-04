namespace Otantik.SharedKernel.Orders;

// How an order's money adds up, as the delivery system's OrderService.CreateAsync computes it,
// so that the till and the website charge the same amount for the same order:
//
//   taxable = items - promo discount
//   tax     = taxable × TaxPercentage%, rounded to 2 places
//   total   = taxable + tax + delivery fee - delivery discount - points
//
// Points come off after tax. In the delivery system's words, points are "a payment against the
// bill, not a reduction in the price of the food".
//
// The rounding is Math.Round's default, banker's rounding, because that is what the delivery
// system uses for tax. Do not "fix" it to AwayFromZero here alone, or a bill whose tax lands
// exactly on a half piastre is a piastre different online and at the till.
public static class OrderPricing
{
    // Re-computes TaxAmount and TotalAmount from the items still on the bill. Promo discount,
    // delivery fee, delivery discount and points are kept as they are.
    //
    // For an unpaid order only. Once paid, TotalAmount is what was charged and never changes;
    // a refund is recorded in RefundedAmount instead.
    public static void Reprice(Order order, decimal taxPercentage)
    {
        var taxable = TaxableAmount(order);
        order.TaxAmount = Math.Round(taxable * (taxPercentage / 100m), 2);
        order.TotalAmount = TotalBeforePoints(order) - order.PointsDiscountAmount;
    }

    // What the customer owes before any points: the most that points may cover.
    public static decimal TotalBeforePoints(Order order) =>
        TaxableAmount(order) + order.TaxAmount + order.DeliveryFee - order.DeliveryDiscountAmount;

    private static decimal TaxableAmount(Order order) =>
        order.BilledItemsSubtotal - order.DiscountAmount;
}
