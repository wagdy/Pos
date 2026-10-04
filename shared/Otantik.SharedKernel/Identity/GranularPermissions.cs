
namespace Otantik.SharedKernel.Identity;

// The fixed catalog of granular (sub-module) permission strings this app understands,
// grouped by their parent AdminModules flag. Deliberately plain strings, not a nested
// enum - Role.GranularPermissionsJson stores exactly these values, and RoleService
// validates against this catalog rather than a closed enum member set, so adding one
// later is a one-line change here with no schema migration (the underlying column is
// already just a JSON string list).
//
// Only modules with actual sub-tabs/sections in the UI have an entry - a module with no
// entry here is simply granted or denied wholesale, exactly as it worked before this
// feature existed.
public static class GranularPermissions
{
    public static readonly IReadOnlyDictionary<AdminModules, string[]> ByModule = new Dictionary<AdminModules, string[]>
    {
        [AdminModules.Orders] = new[] { "Orders.Create", "Orders.AllOrders", "Orders.ActiveStatus", "Orders.Reports" },
        [AdminModules.Settings] = new[] { "Settings.Branding", "Settings.Contact", "Settings.Checkout", "Settings.Payment" }
    };

    public static readonly IReadOnlySet<string> All = ByModule.Values.SelectMany(p => p).ToHashSet();

    public static bool IsValid(string permission) => All.Contains(permission);

    // The AdminModules flag a permission string belongs to, e.g. "Orders.Create" ->
    // AdminModules.Orders - used to cross-validate that a role can't be granted a
    // sub-permission for a module it doesn't otherwise have.
    public static AdminModules? ModuleFor(string permission) =>
        ByModule.FirstOrDefault(kv => kv.Value.Contains(permission)) is { Key: var module, Value: not null } && All.Contains(permission)
            ? module
            : null;
}
