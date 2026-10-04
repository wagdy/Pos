using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Node.Infrastructure.Identity;

namespace OtantikPos.Node.Api.Auth;

internal static class AuthenticationSetup
{
    public static IServiceCollection AddTillAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AuthOptions.Section);
        var options = section.Get<AuthOptions>() ?? new AuthOptions();

        // The usual floor for HS256. Failing at startup means a misconfigured machine never
        // serves at all, rather than serving forgeable tokens.
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
            throw new InvalidOperationException($"{AuthOptions.Section}:SigningKey must be at least 32 bytes. Generate one with `openssl rand -base64 48`.");

        services.Configure<AuthOptions>(section);
        services.AddSingleton<TokenService>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TokenService.Issuer,
                    ValidAudience = TokenService.Audience,
                    IssuerSigningKey = TokenService.SigningKey(options),
                    NameClaimType = StaffClaims.Name,
                    RoleClaimType = StaffClaims.Role,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };

                jwt.Events = new JwtBearerEvents
                {
                    // Browsers cannot set headers on a WebSocket, so the SignalR client sends the
                    // token in the query string instead. Accepted only on the hub path, the same
                    // as the delivery system does.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"].ToString();
                        if (token.Length > 0 && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            context.Token = token;
                        return Task.CompletedTask;
                    },

                    // A token alone would keep a cashier deactivated in the delivery system signed
                    // in for the rest of the shift, and a demoted manager would keep manager rights
                    // as long. So every request also checks, on the local database, that the
                    // account is still active with the role the token claims. The staff list
                    // syncs from the cloud every few minutes, so a change there lands here soon
                    // after, internet permitting.
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        var staffId = StaffClaims.IdOf(principal);
                        var role = staffId is null
                            ? null
                            : await context.HttpContext.RequestServices.GetRequiredService<StaffDirectory>()
                                .GetActiveRoleAsync(staffId, context.HttpContext.RequestAborted);

                        if (role is null || role != StaffClaims.RoleOf(principal))
                            context.Fail("This account has been deactivated or its role has changed. Sign in again.");
                    },
                };
            });

        var authorization = services.AddAuthorizationBuilder()
            // Signed in unless an endpoint says otherwise, so a new endpoint that forgets
            // [Authorize] is closed by default rather than open.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // One policy per permission, named by the permission itself, for endpoints whose rule
        // does not depend on the record they touch:
        //   [Authorize(Policy = Permissions.InventoryManage)]
        // Granted by the shared RolePermissions table, the one both systems read. Order rules
        // that depend on the order's state are not here; OrderAccessPolicy applies those in
        // the use cases.
        foreach (var permission in AllPermissions())
        {
            authorization.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => StaffClaims.RoleOf(context.User) is { } role && RolePermissions.Has(role, permission)));
        }

        return services;
    }

    public static IReadOnlyList<string> AllPermissions() =>
        typeof(Permissions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
}
