using System.Text.Json.Serialization;
namespace Otantik.SharedKernel.Customers;

// 1:1 with AppUser via AppUserId as both PK and FK (see LoyaltyProfileConfiguration) -
// created lazily the first time a customer touches any loyalty endpoint, not at
// registration, so this stays fully decoupled from the existing auth flow.
public class LoyaltyProfile
{
    private const string ReferralCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string AppUserId { get; set; } = string.Empty;

    [JsonIgnore]
    public AppUser AppUser { get; set; } = null!;

    // Field-backed setter: throws if an adjustment would drive the balance negative -
    // callers (redemption) must validate sufficient balance before assigning.
    public int CurrentPoints
    {
        get;
        set => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "CurrentPoints cannot be negative.");
    }

    // Lifetime total is monotonic and can never go negative.
    public int TotalLifetimePoints
    {
        get;
        set => field = value < 0 ? 0 : value;
    }

    // A snapshot of the matching LoyaltyTier's Name at the time it was last resolved (see
    // LoyaltyService.ResolveTierNameAsync) - not a foreign key, so renaming or deleting a
    // tier later never invalidates a customer's already-recorded value. Null means their
    // points don't fall into any currently-configured tier (including "no tiers exist
    // yet" for a brand-new install before an admin sets any up).
    public string? MembershipTier { get; set; }

    public string ReferralCode { get; set; } = GenerateReferralCode();

    // Set once a Google Wallet LoyaltyObject has been created for this profile.
    public bool HasGoogleWalletObject { get; set; }

    // Used as an ETag-ish "has this pass changed" signal for Apple PassKit polling.
    public DateTime LastActivityDate { get; set; } = DateTime.UtcNow;

    public static string GenerateReferralCode()
    {
        Span<char> buffer = stackalloc char[8];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = ReferralCodeAlphabet[Random.Shared.Next(ReferralCodeAlphabet.Length)];
        }

        return new string(buffer);
    }
}
