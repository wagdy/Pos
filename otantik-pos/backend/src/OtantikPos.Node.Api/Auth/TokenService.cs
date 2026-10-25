using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Node.Infrastructure.Identity;

namespace OtantikPos.Node.Api.Auth;

public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider time)
{
    public const string Issuer = "otantik-pos-node";
    public const string Audience = "otantik-pos-node";

    private static readonly JsonWebTokenHandler Handler = new();

    public (string Token, DateTime ExpiresAtUtc) Issue(StaffMember staff)
    {
        var expiresAtUtc = time.GetUtcNow().UtcDateTime.AddHours(options.Value.TokenLifetimeHours);

        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expiresAtUtc,
            Subject = new ClaimsIdentity(
            [
                new Claim(StaffClaims.Id, staff.Id),
                new Claim(StaffClaims.Name, staff.FullName),
                new Claim(StaffClaims.Role, staff.Role.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(options.Value), SecurityAlgorithms.HmacSha256),
        });

        return (token, expiresAtUtc);
    }

    // An employee's, for the self-service portal: their id, the role Employee, and when their PIN
    // was set. A shift's length, as a till's.
    // Their HR & Payroll tabs too, a claim each ("hr_tabs": ["Now", "Attendance"]), for the portal
    // to show; every request checks them against the ones they have now (AuthenticationSetup).
    public (string Token, DateTime ExpiresAtUtc) IssueEmployee(Guid employeeId, string name, DateTime pinSetAtUtc, HrTabs hrTabs = HrTabs.None)
    {
        var expiresAtUtc = time.GetUtcNow().UtcDateTime.AddHours(options.Value.TokenLifetimeHours);
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expiresAtUtc,
            Subject = new ClaimsIdentity(
            [
                new Claim(StaffClaims.Id, employeeId.ToString()),
                new Claim(StaffClaims.Name, name),
                new Claim(StaffClaims.Role, PortalClaims.Role),
                new Claim(PortalClaims.PinSetAt, pinSetAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                .. HrTabSet.Split(hrTabs).Select(tab => new Claim(PortalClaims.HrTabsClaim, tab.ToString())),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(options.Value), SecurityAlgorithms.HmacSha256),
        });

        return (token, expiresAtUtc);
    }

    public static SymmetricSecurityKey SigningKey(AuthOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));
}
