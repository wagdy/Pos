using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Api.Controllers;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Node.Infrastructure.Tests;
using OtantikPos.Ordering.Application.Reports;
using OtantikPos.Ordering.Domain;
using static OtantikPos.Node.Api.Tests.HttpAssertions;

namespace OtantikPos.Node.Api.Tests;

// One class, so the tests run one after another: each starts a whole node with its own
// database, fake cloud and printers.
public class ApiTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Staff_sign_in_with_a_pin_and_get_the_permissions_of_the_shared_table()
    {
        await using var api = await TestApi.StartAsync();
        var anonymous = api.Anonymous();

        // The synced staff have no PIN yet, so only the bootstrap manager can sign in.
        var boss = Assert.Single((await anonymous.GetFromJsonAsync<List<SignInOption>>("/api/auth/staff", TestApi.Json, Cancel))!);
        Assert.Equal("Boss", boss.FullName);

        var wrong = await anonymous.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest(boss.Id, "0000"), Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Wrong PIN.", await ErrorOf(wrong));

        var manager = await api.SignInAsync(boss.Id, TestApi.BossPin);
        var me = (await manager.GetFromJsonAsync<Me>("/api/auth/me", TestApi.Json, Cancel))!;
        Assert.Equal(UserRole.Manager, me.Role);
        Assert.Equal(RolePermissions.For(UserRole.Manager).Order(StringComparer.Ordinal), me.Permissions);

        // The manager gives Sara, synced from the delivery system, a PIN: she signs in as the
        // cashier she is there, with a cashier's rights.
        var cashier = await api.CashierAsync(manager);
        me = (await cashier.GetFromJsonAsync<Me>("/api/auth/me", TestApi.Json, Cancel))!;
        Assert.Equal((TestApi.CashierId, UserRole.Cashier), (me.Id, me.Role));
        Assert.Contains(Permissions.VoidAfterPayment, me.Permissions);
        Assert.DoesNotContain(Permissions.StaffManage, me.Permissions);

        // What dishes cost is a manager's: not on a cashier's screens, and refused if asked for.
        Assert.DoesNotContain(Permissions.CostingView, me.Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/costing/menu", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/costing/menu", Cancel)).StatusCode);

        // So are counts, spoilage and the variance between counts. A cashier sees stock, not this.
        foreach (var path in new[] { "/api/inventory/stock-counts", "/api/costing/variance", "/api/costing/spoilage", "/api/costing/kpis?year=2026&month=10", "/api/costing/break-even?fromYear=2026&fromMonth=10&toYear=2026&toMonth=10" })
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(path, Cancel)).StatusCode);
        // Wages are a manager's to see and enter.
        var wages = await cashier.PutAsJsonAsync("/api/costing/expenses/2026/10", new { wagesAndBenefits = 1 }, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, wages.StatusCode);
        (await manager.PutAsJsonAsync("/api/costing/expenses/2026/10", new { wagesAndBenefits = 1000, labourHours = 80 }, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/costing/kpis?year=2026&month=10", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/inventory/stock-counts", Cancel)).StatusCode);
        var spoilage = await cashier.PostAsJsonAsync("/api/inventory/spoilage", new { spoilageId = Guid.NewGuid(), lines = Array.Empty<object>() }, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, spoilage.StatusCode);
        // With no counts yet, the variance report says what it needs rather than failing.
        var noCounts = await manager.GetAsync("/api/costing/variance", Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCounts.StatusCode);
        Assert.Contains("Post a stock count first", await noCounts.Content.ReadAsStringAsync(Cancel));

        // A captain takes orders in the delivery app. Even with a PIN, not at the till.
        (await manager.PutAsJsonAsync("/api/staff/staff-captain/pin", new SetPinRequest("1357"), Cancel)).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await anonymous.GetFromJsonAsync<List<SignInOption>>("/api/auth/staff", TestApi.Json, Cancel))!, s => s.Id == "staff-captain");
        var captain = await anonymous.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest("staff-captain", "1357"), Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, captain.StatusCode);
    }

    // Closed by default: a new endpoint that forgets [Authorize] needs a signed-in user anyway,
    // and the only doors open without one are these.
    [Fact]
    public async Task Nothing_but_sign_in_and_health_answers_without_a_token()
    {
        await using var api = await TestApi.StartAsync();
        var anonymous = api.Anonymous();

        foreach (var path in new[] { "/api/orders/open", $"/api/orders/{Guid.NewGuid()}", "/api/menu", "/api/customers/by-phone/0100", "/api/inventory/raw-materials", "/api/staff", "/api/auth/me", "/api/status" })
        {
            var response = await anonymous.GetAsync(path, Cancel);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{path} answered {(int)response.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/ready", Cancel)).StatusCode);

        // An unknown API path is a JSON 404, not the Angular app's index page.
        var unknown = await anonymous.GetAsync("/api/no-such-thing", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);

        var open = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["* /health", "* /health/ready", "* {*path:nonfile}", "GET api/auth/staff", "POST api/auth/sign-in"], open);
    }

    [Fact]
    public async Task Back_office_rights_follow_the_shared_permission_table()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var beef = new { name = "Beef patty", unit = "Piece", reorderLevel = 10 };

        // A cashier sees stock (InventoryView) but does not change it or manage staff.
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/inventory/raw-materials", Cancel)).StatusCode);
        var refused = await cashier.PostAsJsonAsync("/api/inventory/raw-materials", beef, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("You are not allowed to do that.", await ErrorOf(refused));
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/staff", Cancel)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync("/api/inventory/raw-materials", beef, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/staff", Cancel)).StatusCode);
    }

    // The till's whole job over HTTP, with a second till watching over SignalR: it sees each
    // change as it happens, as the same shared Order JSON the endpoints return.
    [Fact]
    public async Task A_dine_in_order_taken_and_paid_over_http_reaches_every_till()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var pushes = new ConcurrentQueue<Order>();
        await using var otherTill = await api.ConnectTillAsync(manager, pushes);

        var menu = await cashier.GetAsync("/api/menu", Cancel);
        Assert.Contains("\"name\":\"Burger\"", await menu.Content.ReadAsStringAsync(Cancel));

        var created = await cashier.PostAsJsonAsync("/api/orders", new { type = "DineIn", tableNumber = "7" }, Cancel);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("\"type\":\"DineIn\"", json);
        Assert.DoesNotContain("\"user\"", json);
        var order = (await created.Content.ReadFromJsonAsync<Order>(TestApi.Json, Cancel))!;
        Assert.Equal($"/api/orders/{order.PublicId}", created.Headers.Location?.AbsolutePath);

        await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{order.PublicId}/items",
            new AddOrderItemRequest(FakeDeliverySystem.Burger, 1, AddOnIds: [FakeDeliverySystem.Cheese], Notes: "well done"), TestApi.Json, Cancel));
        await OrderFrom(cashier.PostAsync($"/api/orders/{order.PublicId}/send-to-kitchen", null, Cancel));
        Assert.True(await TestApi.WaitFor(() => Task.FromResult(api.Kitchen.Printed.Any(t => t.Contains("Table 7") && t.Contains("> well done")))));

        order = await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{order.PublicId}/checkout", new CheckoutRequest(PaymentMethod.Cash), TestApi.Json, Cancel));
        Assert.Equal(OrderStatus.Served, order.Status);
        Assert.Equal(153.90m, order.TotalAmount);   // (120 + 15 cheese) + 14% tax

        Assert.Equal(HttpStatusCode.Accepted, (await cashier.PostAsync($"/api/orders/{order.PublicId}/receipt", null, Cancel)).StatusCode);
        Assert.True(await TestApi.WaitFor(() => Task.FromResult(api.Receipts.Printed.Any(r => r.Contains("153.90")))));

        Assert.True(await TestApi.WaitFor(() => Task.FromResult(pushes.Any(o => o.PublicId == order.PublicId && o.Status == OrderStatus.Served))));
        Assert.Contains(pushes, o => o.PublicId == order.PublicId && o.OrderItems.Count == 0);
    }

    // Requirement 2, end to end: a captain submits a table's order in the delivery app, the
    // cloud pushes it to this machine, and the cashier's screen has it at once, ready to be
    // paid, which only the cashier may do.
    [Fact]
    public async Task A_captains_order_from_the_delivery_app_reaches_the_cashiers_screen()
    {
        await using var api = await TestApi.StartAsync();
        var cashier = await api.CashierAsync(await api.ManagerAsync());
        var pushes = new ConcurrentQueue<Order>();
        await using var till = await api.ConnectTillAsync(cashier, pushes);
        Assert.True(await TestApi.WaitFor(() => Task.FromResult(api.Cloud.ConnectedTills > 0)));

        var captainOrder = new Order
        {
            Type = OrderType.DineIn,
            TableNumber = "12",
            CreatedByUserId = "staff-captain",
            PaymentStatus = PaymentStatus.Pending,
            OrderItems = [new OrderItem { MenuItemId = FakeDeliverySystem.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = 2, SentToKitchenAt = DateTime.UtcNow }],
        };
        await api.Cloud.BroadcastAsync(captainOrder);

        Assert.True(await TestApi.WaitFor(() => Task.FromResult(pushes.Any(o => o.PublicId == captainOrder.PublicId && o.TableNumber == "12"))));
        Assert.Contains((await cashier.GetFromJsonAsync<List<Order>>("/api/orders/open", TestApi.Json, Cancel))!, o => o.PublicId == captainOrder.PublicId);
        Assert.True(await TestApi.WaitFor(() => Task.FromResult(api.Kitchen.Printed.Any(t => t.Contains("Table 12")))));

        var paid = await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{captainOrder.PublicId}/checkout", new CheckoutRequest(PaymentMethod.Visa), TestApi.Json, Cancel));
        Assert.True(OrderRules.IsSettled(paid));
        Assert.Equal(273.60m, paid.TotalAmount);    // 2 x 120 + 14% tax
    }

    // Every refusal is a ProblemDetails with errors[0] written for the cashier to read, under
    // the status code that says what kind of refusal it is.
    [Fact]
    public async Task Refusals_come_back_as_problem_details_the_till_can_show()
    {
        await using var api = await TestApi.StartAsync();
        var cashier = await api.CashierAsync(await api.ManagerAsync());

        var missing = await cashier.GetAsync($"/api/orders/{Guid.Empty}", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal($"Order {Guid.Empty} was not found.", await ErrorOf(missing));

        var walkIn = await OrderFrom(cashier.PostAsJsonAsync("/api/orders", new { type = "DineIn", tableNumber = "3" }, Cancel));
        var badQuantity = await cashier.PostAsJsonAsync($"/api/orders/{walkIn.PublicId}/items", new AddOrderItemRequest(FakeDeliverySystem.Burger, 0), TestApi.Json, Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badQuantity.StatusCode);
        Assert.Equal("Quantity must be between 1 and 100.", await ErrorOf(badQuantity));

        // A rule that depends on the order's state, from OrderAccessPolicy.
        var noCustomer = await cashier.PutAsJsonAsync($"/api/orders/{walkIn.PublicId}/loyalty-points", new ApplyLoyaltyPointsRequest(10), Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, noCustomer.StatusCode);
        Assert.Equal("Look the customer up by mobile number before applying points.", await ErrorOf(noCustomer));

        // The points were spent online between applying them and taking payment.
        api.Cloud.Customers["01001234567"] = new CustomerProfile("customer-1", "Ali Hassan", "01001234567", 500);
        api.Cloud.Balances["customer-1"] = 500;
        var takeaway = await OrderFrom(cashier.PostAsJsonAsync("/api/orders", new { type = "Takeaway", customerPhone = "01001234567" }, Cancel));
        Assert.Equal("customer-1", takeaway.UserId);
        await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{takeaway.PublicId}/items", new AddOrderItemRequest(FakeDeliverySystem.Burger), TestApi.Json, Cancel));
        await OrderFrom(cashier.PutAsJsonAsync($"/api/orders/{takeaway.PublicId}/loyalty-points", new ApplyLoyaltyPointsRequest(300), Cancel));
        api.Cloud.Balances["customer-1"] = 0;
        var spent = await cashier.PostAsJsonAsync($"/api/orders/{takeaway.PublicId}/checkout", new CheckoutRequest(PaymentMethod.Cash), TestApi.Json, Cancel);
        Assert.Equal(HttpStatusCode.Conflict, spent.StatusCode);
        Assert.Equal("The customer no longer has enough points.", await ErrorOf(spent));

        // Points need the internet; the rest of the till does not.
        api.Cloud.Down = true;
        var offline = await cashier.PutAsJsonAsync($"/api/orders/{takeaway.PublicId}/loyalty-points", new ApplyLoyaltyPointsRequest(100), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ErrorOf(offline)));
        await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{walkIn.PublicId}/items", new AddOrderItemRequest(FakeDeliverySystem.Burger), TestApi.Json, Cancel));
    }

    // Offline resilience: when this machine loses the delivery system, every till is told at
    // once (to switch off customer lookup and points), and selling goes on regardless.
    [Fact]
    public async Task The_tills_are_told_when_the_cloud_goes_and_cash_sales_carry_on()
    {
        await using var api = await TestApi.StartAsync();
        var cashier = await api.CashierAsync(await api.ManagerAsync());
        var links = new ConcurrentQueue<DeliverySystemLinkStatus>();
        await using var till = await api.ConnectTillAsync(cashier, new ConcurrentQueue<Order>(), links);

        Assert.True(await TestApi.WaitFor(async () => (await StatusOf(cashier)).State == DeliverySystemLinkState.Online));
        Assert.True((await StatusOf(cashier)).CloudFeaturesAvailable);

        await api.Cloud.StopAsync();
        Assert.True(await TestApi.WaitFor(() => Task.FromResult(links.Any(l => l.State == DeliverySystemLinkState.Offline))));
        Assert.False((await StatusOf(cashier)).CloudFeaturesAvailable);

        var order = await OrderFrom(cashier.PostAsJsonAsync("/api/orders", new { type = "DineIn", tableNumber = "8" }, Cancel));
        await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{order.PublicId}/items", new AddOrderItemRequest(FakeDeliverySystem.Burger), TestApi.Json, Cancel));
        var paid = await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{order.PublicId}/checkout", new CheckoutRequest(PaymentMethod.Cash), TestApi.Json, Cancel));
        Assert.True(OrderRules.IsSettled(paid));
    }

    private static async Task<DeliverySystemLinkStatus> StatusOf(HttpClient client) =>
        (await client.GetFromJsonAsync<NodeStatus>("/api/status", TestApi.Json, Cancel))!.DeliverySystem;

    // The till's day report, over HTTP: a paid order is in it, by payment method.
    [Fact]
    public async Task The_day_report_shows_what_the_till_took()
    {
        await using var api = await TestApi.StartAsync();
        var cashier = await api.CashierAsync(await api.ManagerAsync());

        var order = await OrderFrom(cashier.PostAsJsonAsync("/api/orders", new { type = "DineIn", tableNumber = "2" }, Cancel));
        await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{order.PublicId}/items", new AddOrderItemRequest(FakeDeliverySystem.Burger), TestApi.Json, Cancel));
        await OrderFrom(cashier.PostAsJsonAsync($"/api/orders/{order.PublicId}/checkout", new CheckoutRequest(PaymentMethod.Cash), TestApi.Json, Cancel));

        var report = (await cashier.GetFromJsonAsync<DayReport>("/api/reports/day", TestApi.Json, Cancel))!;

        Assert.Contains(report.Orders, o => o.PublicId == order.PublicId);
        Assert.Equal((1, 136.80m), (report.Summary.PaidOrders, report.Summary.NetSales));
        Assert.Equal(PaymentMethod.Cash, Assert.Single(report.ByPaymentMethod).Method);

        var yesterday = report.BusinessDate.AddDays(-1).ToString("yyyy-MM-dd");
        Assert.Empty((await cashier.GetFromJsonAsync<DayReport>($"/api/reports/day?date={yesterday}", TestApi.Json, Cancel))!.Orders);
    }

    // A token is good for a shift, but not one minute past a change in the delivery system:
    // the next sync brings the change down, and the next request is refused.
    [Fact]
    public async Task A_role_change_or_deactivation_in_the_delivery_system_signs_the_till_out()
    {
        await using var api = await TestApi.StartAsync();
        var cashier = await api.CashierAsync(await api.ManagerAsync());
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/orders/open", Cancel)).StatusCode);

        var data = FakeDeliverySystem.ReferenceData();
        await api.ApplyReferenceDataAsync(data with
        {
            Staff = data.Staff.Select(s => s.Id == TestApi.CashierId ? s with { Role = UserRole.Manager } : s).ToList(),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync("/api/orders/open", Cancel)).StatusCode);

        // Signing in again picks the new role up.
        var promoted = await api.SignInAsync(TestApi.CashierId, TestApi.CashierPin);
        Assert.Equal(UserRole.Manager, (await promoted.GetFromJsonAsync<Me>("/api/auth/me", TestApi.Json, Cancel))!.Role);

        await api.ApplyReferenceDataAsync(data with
        {
            Staff = data.Staff.Select(s => s.Id == TestApi.CashierId ? s with { Role = UserRole.Manager, IsActive = false } : s).ToList(),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await promoted.GetAsync("/api/orders/open", Cancel)).StatusCode);
    }
}
