using Microsoft.AspNetCore.Identity;
using OtantikPos.Inventory.Application.Hr;

namespace OtantikPos.Node.Infrastructure.Identity;

// An employee's portal PIN, hashed as a till PIN is: ASP.NET Identity's salted, slow hash.
internal sealed class PinHasher : IPinHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Anyone = new();
    private static readonly Lazy<string> Nobody = new(() => Hasher.HashPassword(Anyone, Guid.NewGuid().ToString("N")));

    public string NobodysHash => Nobody.Value;

    public string Hash(string pin) => Hasher.HashPassword(Anyone, pin);

    public PinCheck Verify(string hash, string pin) => Hasher.VerifyHashedPassword(Anyone, hash, pin) switch
    {
        PasswordVerificationResult.Success => PinCheck.Right,
        PasswordVerificationResult.SuccessRehashNeeded => PinCheck.RightRehash,
        _ => PinCheck.Wrong,
    };
}
