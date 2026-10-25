using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Hr.Domain;
using OtantikPos.Hr.Domain.Access;
using OtantikPos.Node.Api.Hosting;

namespace OtantikPos.Node.Api.Auth;

// An HR & Payroll endpoint's tabs (صلاحيات الموظفين): whoever holds any of them may call it.
//   [HrTab(HrTabs.Attendance)]                  seen on the Attendance tab
//   [HrTab(HrTabs.Employees, Manage = true)]    a change made on the Employees tab
// On the controller, HrTabs.All: someone holding a tab at all.
//
// A manager holds every tab. An accountant (HrView) holds those a manager ticked for them, every
// one until then; a change needs HrManage too. An employee holds those in their portal token,
// checked against the ones they have now on every request (AuthenticationSetup); they change
// things as an accountant does, so Manage passes them and the use case asks a manager. What only
// a manager does keeps its ApprovalDecide policy beside the tab. On the portal's own port only an
// employee holds any.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HrTabAttribute : AuthorizeAttribute
{
    public HrTabAttribute(HrTabs tabs)
    {
        Tabs = tabs;
        Policy = HrTabPolicy.Name(tabs, manage: false);
    }

    public HrTabs Tabs { get; }

    public bool Manage
    {
        get => HrTabPolicy.IsManage(Policy);
        set => Policy = HrTabPolicy.Name(Tabs, value);
    }
}

public sealed record HrTabRequirement(HrTabs Tabs, bool Manage) : IAuthorizationRequirement;

public static class HrTabPolicy
{
    private const string Prefix = "hr-tab:";
    private const string ManageSuffix = ":manage";

    public static string Name(HrTabs tabs, bool manage) =>
        Prefix + ((int)tabs).ToString(CultureInfo.InvariantCulture) + (manage ? ManageSuffix : string.Empty);

    public static bool IsManage(string? name) => name?.EndsWith(ManageSuffix, StringComparison.Ordinal) == true;

    public static HrTabRequirement? Parse(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var manage = IsManage(name);
        var number = name[Prefix.Length..(manage ? name.Length - ManageSuffix.Length : name.Length)];
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var tabs) && tabs != 0
            ? new HrTabRequirement((HrTabs)tabs & HrTabs.All, manage)
            : null;
    }
}

// The HR tab policies made as they are asked for; every other policy as registered.
internal sealed class HrTabPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
        HrTabPolicy.Parse(policyName) is { } requirement
            ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(requirement).Build()
            : await base.GetPolicyAsync(policyName);
}

internal sealed class HrTabHandler(HrCaller caller) : AuthorizationHandler<HrTabRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, HrTabRequirement requirement)
    {
        if (requirement.Manage && !caller.CanManage)
            return;
        if ((await caller.TabsAsync()).HasAny(requirement.Tabs))
            context.Succeed(requirement);
    }
}

// Who is calling HR, and the tabs they hold: read once a request.
public sealed class HrCaller(IHttpContextAccessor accessor, IStaffHrAccessRepository staffAccess, IConfiguration configuration)
{
    private HrTabs? tabs;

    private ClaimsPrincipal User => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public bool IsEmployee => PortalClaims.IsEmployee(User);

    // An employee changes things as an accountant does; staff need HrManage.
    public bool CanManage => IsEmployee || (StaffClaims.RoleOf(User) is { } role && RolePermissions.Has(role, Permissions.HrManage));

    public async Task<HrTabs> TabsAsync(CancellationToken cancellationToken = default) => tabs ??= await ReadAsync(cancellationToken);

    private async Task<HrTabs> ReadAsync(CancellationToken cancellationToken)
    {
        var user = User;
        if (PortalClaims.IsEmployee(user))
            return PortalClaims.HrTabsOf(user);
        if (OnPortalPort || StaffClaims.RoleOf(user) is not { } role || !RolePermissions.Has(role, Permissions.HrView))
            return HrTabs.None;
        if (RolePermissions.Has(role, Permissions.ApprovalDecide))
            return HrTabs.All;
        return StaffClaims.IdOf(user) is { } staffId && await staffAccess.GetByStaffIdAsync(staffId, cancellationToken) is { } row ? row.Tabs : HrTabs.All;
    }

    private bool OnPortalPort => PortalPort.Of(configuration) is { } port && accessor.HttpContext?.Connection.LocalPort == port;
}

// What a caller of HR does not get, whatever the response: what anyone earns, their national ID,
// mobile and ID card, for one without the Employees or Payroll tab (the Attendance tab has the
// names and shifts, not the pay); a department's costs, for one without Departments, Employees
// or Payroll; and what an asset cost, for an employee, as their own custody never has it. Those
// values are sent as null. Managers, holding every tab, get everything.
internal sealed class HrPrivacyFilter(HrCaller caller, IOptions<JsonOptions> json) : IAsyncResultFilter
{
    // An employee's record (EmployeeDto), wherever it is in the response.
    private static readonly string[] Personal = ["monthlySalary", "dailyWage", "dailyRate", "hourlyRate", "nationalId", "mobileNumber", "idPhoto"];

    // A department's or a role's (DepartmentDto, JobRoleDto).
    private static readonly string[] Costs = ["monthlyCost", "dailyCost"];

    // An asset's (AssetItemDto).
    private static readonly string[] AssetCosts = ["unitCost", "totalCost"];

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: { } value } result)
        {
            var tabs = await caller.TabsAsync(context.HttpContext.RequestAborted);
            var hide = new Hidden(
                Pay: !tabs.HasAny(HrTabs.Employees | HrTabs.Payroll),
                Costs: !tabs.HasAny(HrTabs.Departments | HrTabs.Employees | HrTabs.Payroll),
                AssetCosts: caller.IsEmployee);
            if (hide.Any)
            {
                var node = JsonSerializer.SerializeToNode(value, value.GetType(), json.Value.JsonSerializerOptions);
                Strip(node, hide);
                result.Value = node;
                result.DeclaredType = typeof(JsonNode);
            }
        }

        await next();
    }

    private sealed record Hidden(bool Pay, bool Costs, bool AssetCosts)
    {
        public bool Any => Pay || Costs || AssetCosts;
    }

    private static void Strip(JsonNode? node, Hidden hide)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    Strip(item, hide);
                break;
            case JsonObject o:
                if (hide.Pay && o.ContainsKey("monthlySalary") && o.ContainsKey("hiredOn"))
                    Clear(o, Personal);
                if (hide.Costs && o.ContainsKey("monthlyCost") && o.ContainsKey("employed"))
                    Clear(o, Costs);
                if (hide.AssetCosts && o.ContainsKey("unitCost") && o.ContainsKey("quantity"))
                    Clear(o, AssetCosts);
                foreach (var (_, child) in o.ToList())
                    Strip(child, hide);
                break;
        }
    }

    private static void Clear(JsonObject o, string[] names)
    {
        foreach (var name in names)
        {
            if (o.ContainsKey(name))
                o[name] = null;
        }
    }
}
