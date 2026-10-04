
namespace Otantik.SharedKernel.Identity;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public AdminModules Modules { get; set; }

    // JSON-serialized List<string> of granular sub-permissions (e.g. "Orders.Create"),
    // mirroring the PromoCode.TargetIds precedent for storing a string list without a new
    // join table. Null/empty for a module means "no restriction recorded" - full access to
    // everything under that module, so every role created before this feature shipped keeps
    // working with zero migration/backfill. See Otantik.SharedKernel.Identity.GranularPermissions.
    public string? GranularPermissionsJson { get; set; }
}
