using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
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

    public static SymmetricSecurityKey SigningKey(AuthOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));
}
