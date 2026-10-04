using Otantik.SharedKernel.Customers;

namespace OtantikPos.Node.Infrastructure.Persistence;

// The settings the till needs from the delivery system: RestaurantSettings' tax rate and
// LoyaltySettings' redemption rate. A single row.
public sealed class RestaurantSettingsCopy
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public decimal TaxPercentage { get; set; }

    // L.E per 100 points redeemed; 10 is Points / 10. See LoyaltyRedemption.
    public decimal RedemptionValuePer100Points { get; set; } = LoyaltyRedemption.DefaultValuePer100Points;

    public DateTime SyncedAt { get; set; }
}
