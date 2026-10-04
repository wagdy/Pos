namespace Otantik.SharedKernel.Customers;

// What loyalty points are worth off a bill: Discount = Points / 10 by default.
//
// The rate is a system setting, the delivery system's LoyaltySettings.RedemptionValuePer100Points:
// L.E granted per 100 points, which an admin edits in its Campaign Manager. Its default of 10 is
// Points / 10. The till receives it with the menu and the tax rate, so a customer's points are
// worth the same at the till as online at any setting.
//
// The arithmetic is the delivery system's own (LoyaltyPointsValue), rounding and all, because
// the two must agree to the piastre: a balance shown at the till as worth L.E 4.13 has to be
// redeemable online for L.E 4.13 too.
public static class LoyaltyRedemption
{
    // Points / 10.
    public const decimal DefaultValuePer100Points = 10m;

    // Rounded to 2dp, half away from zero, as the delivery system rounds: at a rate of 12.5,
    // 33 points are worth 4.13, not banker's 4.12. At the default rate nothing needs rounding.
    // Worth nothing at a rate of 0, which an admin can set to switch redemption off.
    public static decimal DiscountFor(int points, decimal valuePer100Points) =>
        points <= 0 || valuePer100Points <= 0
            ? 0m
            : Math.Round(points / 100m * valuePer100Points, 2, MidpointRounding.AwayFromZero);

    // The most points that can go against an amount without the discount exceeding it.
    // Floored, as the delivery system's cap is: a 55.55 bill takes at most 555 points (55.50)
    // at the default rate, never 556 (55.60).
    public static int MaxPointsFor(decimal amount, decimal valuePer100Points) =>
        amount <= 0 || valuePer100Points <= 0 ? 0 : (int)Math.Floor(amount / (valuePer100Points / 100m));
}
