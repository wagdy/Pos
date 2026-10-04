namespace Otantik.SharedKernel.Identity;

[Flags]
public enum AdminModules
{
    None = 0,
    Orders = 1 << 0,
    MenuItems = 1 << 1,
    Settings = 1 << 2,
    Staff = 1 << 3,
    Customers = 1 << 4,
    Crm = 1 << 5,
    Campaigns = 1 << 6,
    Scanner = 1 << 7,
    PromoCodes = 1 << 8,
    Reviews = 1 << 9,
    Reports = 1 << 10
}

// Single source of truth for the JWT claim type carrying granted admin modules - the
// writer (JwtTokenService) and every reader (PermissionAuthorizationHandler,
// OrdersAccessAuthorizationHandler) must agree on this exact string.
public static class AdminModuleClaims
{
    public const string ClaimType = "modules";
}

// Same idea as AdminModuleClaims but for granular sub-permission strings (e.g.
// "Orders.Create") - a separate claim type so a reader can tell "no modules claims"
// (legacy token) apart from "no granular-permission claims" (a role with modules but no
// recorded sub-permission restrictions) without ambiguity.
public static class GranularPermissionClaims
{
    public const string ClaimType = "permissions";
}
