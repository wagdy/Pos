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
//   [HrTab(HrTabs.Attendance, Decide = true)]   a change only a manager makes, or an employee
//                                               holding the tab
// On the controller, HrTabs.All: someone holding a tab at all.
//
// A manager holds every tab. An accountant (HrView) holds those a manager ticked for them, every
// one until then; a change needs HrManage too, and what only a manager does stays a manager's.
// An employee holds those in their portal token, checked against the ones they have now on
// every request (AuthenticationSetup). In their tabs they change what an accountant changes, the
// use case asking a manager where it asks one (an employee's record, an advance, an asset), and
// make at once what only a manager makes otherwise (Decide): a punch put right, a department, a
// holiday. Their PINs and who holds which tab stay a manager's (ApprovalDecide). On the portal's
// own port only an employee holds any.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HrTabAttribute : AuthorizeAttribute
{
    private bool manage;
    private bool decide;

    public HrTabAttribute(HrTabs tabs)
    {
        Tabs = tabs;
        Policy = HrTabPolicy.Name(new HrTabRequirement(tabs, false, false));
    }

    public HrTabs Tabs { get; }

    public bool Manage
    {
        get => manage;
        set
        {
            manage = value;
            Policy = HrTabPolicy.Name(new HrTabRequirement(Tabs, manage, decide));
        }
    }

    public bool Decide
    {
        get => decide;
        set
        {
            decide = value;
            Policy = HrTabPolicy.Name(new HrTabRequirement(Tabs, manage, decide));
        }
    }
}

public sealed record HrTabRequirement(HrTabs Tabs, bool Manage, bool Decide = false) : IAuthorizationRequirement;

// "hr-tab:8", "hr-tab:8:manage", "hr-tab:8:decide": the tabs as their number, and what is asked.
public static class HrTabPolicy
{
    private const string Prefix = "hr-tab:";

    public static string Name(HrTabRequirement r) =>
        Prefix + ((int)r.Tabs).ToString(CultureInfo.InvariantCulture) + (r.Manage ? ":manage" : string.Empty) + (r.Decide ? ":decide" : string.Empty);

    public static HrTabRequirement? Parse(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = name[Prefix.Length..].Split(':');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var tabs) || tabs == 0)
            return null;
        var asked = parts.Skip(1).ToHashSet(StringComparer.Ordinal);
        if (asked.Except(["manage", "decide"]).Any())
            return null;
        return new HrTabRequirement((HrTabs)tabs & HrTabs.All, asked.Contains("manage"), asked.Contains("decide"));
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
        if ((requirement.Manage && !caller.CanManage) || (requirement.Decide && !caller.CanDecide))
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

    // What only a manager does, in the tabs: a manager, or an employee in a tab of theirs.
    public bool CanDecide => IsEmployee || (StaffClaims.RoleOf(User) is { } role && RolePermissions.Has(role, Permissions.ApprovalDecide));

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

    // An asset's (AssetItemDto), and a voucher's (AssetTransactionDto).
    private static readonly string[] AssetCosts = ["unitCost", "totalCost", "cost"];

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
