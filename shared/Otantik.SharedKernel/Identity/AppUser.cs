using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;

namespace Otantik.SharedKernel.Identity;

public class AppUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public string? Address { get; set; }
    public UserRole Role { get; set; } = UserRole.Customer;

    // Named CustomRole/CustomRoleId (not Role/RoleId) to avoid colliding with the
    // property above - Role here is the coarse UserRole enum (Customer, Admin, the captains) that
    // drives the JWT role claim and every [Authorize(Roles=...)] check; CustomRole is an
    // optional, finer-grained permission set that only narrows what an Admin account can
    // do (null means full access - see AuthService.ResolveAdminModuleNamesAsync).
    public int? CustomRoleId { get; set; }
    public Role? CustomRole { get; set; }

    // Soft delete, not a hard Remove() - a customer is linked to Orders, OrderReviews and
    // a LoyaltyProfile by the time anyone would want to "delete" them, and Postgres would
    // reject a hard delete (or worse, cascade it) rather than lose that history. IsDeleted
    // hides the account from Customer Insights, the Scanner, and the "Registered Customer"
    // order-creation search, and blocks its own login - while every historical Order/
    // OrderReview/LoyaltyProfile row referencing it stays exactly as it was. Only ever
    // meaningful for Role == Customer; staff accounts are never soft-deleted.
    public bool IsDeleted { get; set; }

    [JsonIgnore]
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
