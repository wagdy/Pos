namespace OtantikPos.Node.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    // HMAC key for signing till tokens; at least 32 bytes. Generate one per restaurant machine,
    // for example `openssl rand -base64 48`. Anyone who has it can sign in as anyone. It is not
    // the delivery system's key: a till token is good on this machine only.
    public string SigningKey { get; set; } = string.Empty;

    // One shift. A deactivated or re-roled account is refused at once regardless (see
    // AuthenticationSetup), so this is about how often staff re-enter their PIN, not about
    // revocation.
    public int TokenLifetimeHours { get; set; } = 12;

    public BootstrapManagerOptions BootstrapManager { get; set; } = new();
}

// Creates a first, local manager on a machine where nobody has a PIN yet; see
// StaffDirectory.EnsureBootstrapManagerAsync. Remove the PIN from configuration once it has.
public sealed class BootstrapManagerOptions
{
    public string? Name { get; set; }

    public string? Pin { get; set; }
}
