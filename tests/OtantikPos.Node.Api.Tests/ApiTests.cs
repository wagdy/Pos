using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Api.Controllers;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Node.Infrastructure.Excel;
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

    // An invoice's paper goes up as a file from the review screen and comes back as it went:
    // shown in the browser, or downloaded under its name. A manager's only.
    [Fact]
    public async Task An_invoice_photo_is_uploaded_and_read_back_by_a_manager_only()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var invoiceId = Guid.NewGuid();
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

        MultipartFormDataContent Upload(byte[] bytes, string type)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return new MultipartFormDataContent { { file, "file", "receipt.jpg" } };
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsync($"/api/inventory/purchase-invoices/{invoiceId}/attachment", Upload(jpeg, "image/jpeg"), Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/inventory/purchase-invoices", Cancel)).StatusCode);

        var refused = await manager.PutAsync($"/api/inventory/purchase-invoices/{invoiceId}/attachment", Upload([1, 2, 3], "image/jpeg"), Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        (await manager.PutAsync($"/api/inventory/purchase-invoices/{invoiceId}/attachment", Upload(jpeg, "image/jpeg"), Cancel)).EnsureSuccessStatusCode();

        var shown = await manager.GetAsync($"/api/inventory/purchase-invoices/{invoiceId}/attachment", Cancel);
        Assert.Equal(("image/jpeg", "nosniff"), (shown.Content.Headers.ContentType!.MediaType, shown.Headers.GetValues("X-Content-Type-Options").Single()));
        Assert.Equal(jpeg, await shown.Content.ReadAsByteArrayAsync(Cancel));
        var downloaded = await manager.GetAsync($"/api/inventory/purchase-invoices/{invoiceId}/attachment?download=true", Cancel);
        Assert.Equal("receipt.jpg", downloaded.Content.Headers.ContentDisposition!.FileName);
    }

    // The suppliers screen: a manager's. Deleting answers whether the supplier went or was
    // archived, which it is when an invoice has it.
    [Fact]
    public async Task Suppliers_are_kept_by_a_manager_and_archived_once_invoiced()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var nileFarms = Guid.NewGuid();
        var deltaOils = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/inventory/suppliers", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/inventory/suppliers/{nileFarms}", new { name = "Nile Farms" }, Cancel)).StatusCode);

        (await manager.PutAsJsonAsync($"/api/inventory/suppliers/{nileFarms}", new { name = "Nile Farms", phone = "0100 123 4567" }, Cancel)).EnsureSuccessStatusCode();
        (await manager.PutAsJsonAsync($"/api/inventory/suppliers/{deltaOils}", new { name = "Delta Oils" }, Cancel)).EnsureSuccessStatusCode();
        var twice = await manager.PutAsJsonAsync($"/api/inventory/suppliers/{Guid.NewGuid()}", new { name = "NILE FARMS" }, Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice.StatusCode);

        // On an invoice through the screens' own request.
        (await manager.PutAsJsonAsync($"/api/inventory/purchase-invoices/{Guid.NewGuid()}",
            new { supplierId = nileFarms, supplierInvoiceNumber = "F-1", invoiceDate = (string?)null, lines = Array.Empty<object>() }, Cancel)).EnsureSuccessStatusCode();

        async Task<string> Delete(Guid id) =>
            (await (await manager.DeleteAsync($"/api/inventory/suppliers/{id}", Cancel)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel)).GetProperty("outcome").GetString()!;
        Assert.Equal("Deleted", await Delete(deltaOils));
        Assert.Equal("Archived", await Delete(nileFarms));

        async Task<string[]> Names(string query) =>
            (await manager.GetFromJsonAsync<System.Text.Json.JsonElement[]>($"/api/inventory/suppliers{query}", Cancel))!.Select(s => s.GetProperty("name").GetString()!).ToArray();
        Assert.Empty(await Names(""));
        Assert.Equal(["Nile Farms"], await Names("?includeArchived=true"));

        (await manager.PostAsync($"/api/inventory/suppliers/{nileFarms}/restore", null, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(["Nile Farms"], await Names(""));
    }

    // Recipes of their own: written, changed, deleted softly and restored by a manager; listed with
    // their costs to whoever sees the costing.
    [Fact]
    public async Task Recipes_are_created_edited_and_deleted_softly_by_a_manager()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);

        async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel);
        }
        var patty = (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials",
            new { name = "Beef patty", unit = "Piece", reorderLevel = 0, purchaseUnit = "piece", purchaseUnitSize = 1, costPerPurchaseUnit = 20 }, Cancel)))
            .GetProperty("id").GetGuid();

        // The kitchen's own categories: a manager's to keep.
        var trials = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/inventory/recipe-categories/{trials}", new { name = "R&D" }, Cancel)).StatusCode);
        await Json(await manager.PutAsJsonAsync($"/api/inventory/recipe-categories/{trials}", new { name = "R&D", nameAr = "تجارب" }, Cancel));
        var mains = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/inventory/recipe-categories/{mains}", new { name = "Mains" }, Cancel));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await manager.PutAsJsonAsync($"/api/inventory/recipe-categories/{Guid.NewGuid()}", new { name = " r&d " }, Cancel)).StatusCode);

        var body = new
        {
            name = "Smash burger trial",
            targetKind = (string?)null,
            catalogItemId = (int?)null,
            variantId = (int?)null,
            portions = 2,
            ingredients = new[] { new { rawMaterialId = patty, quantity = 3, yieldPercent = 100 } },
            recipeCategoryId = (Guid?)trials,
            plannedPrice = 120,
        };
        // A recipe written here is in a category.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await manager.PostAsJsonAsync("/api/inventory/recipes", body with { recipeCategoryId = (Guid?)null }, Cancel)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/inventory/recipes", body, Cancel)).StatusCode);
        var created = await Json(await manager.PostAsJsonAsync("/api/inventory/recipes", body, Cancel));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("targetKind").ValueKind);

        // 3 patties for 2 portions: 30 a portion, 25% of the planned 120.
        var card = await Json(await manager.GetAsync($"/api/costing/recipes/{id}", Cancel));
        Assert.Equal((true, 30m, 25m), (card.GetProperty("isStandalone").GetBoolean(), card.GetProperty("costPerPortion").GetDecimal(), card.GetProperty("foodCostPercentActual").GetDecimal()));

        (await manager.PutAsJsonAsync($"/api/inventory/recipes/{id}", body with { portions = 3 }, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(20m, (await Json(await manager.GetAsync($"/api/costing/recipes/{id}", Cancel))).GetProperty("costPerPortion").GetDecimal());

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.DeleteAsync($"/api/inventory/recipes/{id}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/inventory/recipes/{id}", Cancel)).StatusCode);
        Assert.Empty((await Json(await manager.GetAsync("/api/costing/recipes", Cancel))).EnumerateArray());
        var kept = Assert.Single((await Json(await manager.GetAsync("/api/costing/recipes?includeDeleted=true", Cancel))).EnumerateArray());
        Assert.True(kept.GetProperty("isDeleted").GetBoolean());

        await Json(await manager.PostAsJsonAsync($"/api/inventory/recipes/{id}/restore", new { name = (string?)null }, Cancel));
        var listed = Assert.Single((await Json(await manager.GetAsync("/api/costing/recipes", Cancel))).EnumerateArray());
        Assert.Equal("R&D", listed.GetProperty("recipeCategory").GetString());

        // R&D holds a recipe: deleted only with somewhere for it to go.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.DeleteAsync($"/api/inventory/recipe-categories/{trials}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/inventory/recipe-categories/{trials}?moveTo={mains}", Cancel)).StatusCode);
        var categories = (await Json(await manager.GetAsync("/api/inventory/recipe-categories", Cancel))).EnumerateArray().ToList();
        Assert.Equal([("Mains", 1)], categories.Select(c => (c.GetProperty("name").GetString(), c.GetProperty("recipes").GetInt32())));

        // The month's theoretical cost from quantities typed in, kept for the month: a manager's,
        // as costs are on it.
        var sheet = new { lines = new[] { new { kind = "Recipe", itemId = id, quantity = 6m } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/costing/manual-sales/2026/10", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync("/api/costing/manual-sales/2026/10", sheet, Cancel)).StatusCode);
        var listedForSales = Assert.Single((await Json(await manager.GetAsync("/api/costing/manual-sales/2026/10", Cancel))).GetProperty("items").EnumerateArray());
        Assert.Equal(("Recipe", "Mains"), (listedForSales.GetProperty("kind").GetString(), listedForSales.GetProperty("group").GetString()));
        // 6 at 20 against 6 at the planned 120.
        var typed = await Json(await manager.PutAsJsonAsync("/api/costing/manual-sales/2026/10", sheet, Cancel));
        Assert.Equal(("Manual", 120m, 720m), (typed.GetProperty("source").GetString(), typed.GetProperty("theoreticalCost").GetDecimal(), typed.GetProperty("netSales").GetDecimal()));
        var report = await Json(await manager.GetAsync("/api/costing/theoretical?year=2026&month=10&source=Manual", Cancel));
        Assert.Equal((6m, "Manual"), (report.GetProperty("quantitySold").GetDecimal(), report.GetProperty("source").GetString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await manager.PutAsJsonAsync("/api/costing/manual-sales/2026/10", new { lines = new[] { new { kind = "Recipe", itemId = id, quantity = 0.5m } } }, Cancel)).StatusCode);
        Assert.Equal("Pos", (await Json(await manager.GetAsync("/api/costing/theoretical?year=2026&month=10", Cancel))).GetProperty("source").GetString());
    }

    // Purchase categories and expense items through the screens' own requests: who may change
    // them, an invoice carrying both kinds of line, and where its expenses land.
    [Fact]
    public async Task Expenses_are_bought_beside_stock_and_added_up_by_category()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var utilities = Guid.NewGuid();
        var dairy = Guid.NewGuid();
        var electricity = Guid.NewGuid();

        async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel);
        }

        // Anyone who sees stock sees how it is grouped; changing it, and the expenses, are a manager's.
        var listed = await Json(await cashier.GetAsync("/api/inventory/purchase-categories", Cancel));
        Assert.Equal("General", listed[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/inventory/purchase-categories/{utilities}", new { name = "Utilities" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/inventory/expense-items", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/costing/purchases/spending?from=2026-10-01&to=2026-10-31", Cancel)).StatusCode);

        var saved = await Json(await manager.PutAsJsonAsync($"/api/inventory/purchase-categories/{utilities}",
            new { name = "Utilities", nameAr = "مرافق", expenseLine = "Controllable", kind = "Expenses" }, Cancel));
        Assert.Equal(("Controllable", "Expenses"), (saved.GetProperty("expenseLine").GetString(), saved.GetProperty("kind").GetString()));
        await Json(await manager.PutAsJsonAsync($"/api/inventory/purchase-categories/{dairy}", new { name = "Dairy" }, Cancel));
        await Json(await manager.PutAsJsonAsync($"/api/inventory/expense-items/{electricity}", new { name = "Electricity", categoryId = utilities }, Cancel));
        var milk = await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials",
            new { name = "Milk", unit = "Millilitre", categoryId = dairy, purchaseUnit = "L", purchaseUnitSize = 1000 }, Cancel));
        Assert.Equal("Dairy", milk.GetProperty("category").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.DeleteAsync($"/api/inventory/purchase-categories/{dairy}?moveTo={utilities}", Cancel)).StatusCode);

        var invoiceId = Guid.NewGuid();
        var invoice = await Json(await manager.PutAsJsonAsync($"/api/inventory/purchase-invoices/{invoiceId}", new
        {
            supplierId = (Guid?)null,
            supplierInvoiceNumber = "B-1",
            invoiceDate = "2026-10-05",
            paymentMethod = "Safe",
            lines = new object[]
            {
                new { rawMaterialId = milk.GetProperty("id").GetGuid(), enteredQuantity = 2, unitLabel = "L", unitSize = 1000, cost = 60 },
                new { itemType = "Expense", expenseItemId = electricity, enteredQuantity = 1, unitSize = 1, cost = 1_200 },
            },
        }, Cancel));
        Assert.Equal((60m, 1_200m), (invoice.GetProperty("inventoryTotal").GetDecimal(), invoice.GetProperty("expenseTotal").GetDecimal()));
        Assert.Equal(["Inventory", "Expense"], invoice.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("itemType").GetString()));
        await Json(await manager.PostAsync($"/api/inventory/purchase-invoices/{invoiceId}/post", null, Cancel));

        var spending = await Json(await manager.GetAsync("/api/costing/purchases/spending?from=2026-10-01&to=2026-10-31", Cancel));
        Assert.Equal((1_260m, 1_200m), (spending.GetProperty("total").GetDecimal(), spending.GetProperty("expenses").GetDecimal()));
        Assert.Equal(["Utilities", "Dairy"], spending.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("category").GetString()));
        var statement = (await Json(await manager.GetAsync("/api/costing/kpis?year=2026&month=10", Cancel))).GetProperty("statement");
        Assert.Equal(1_200m, statement.GetProperty("loggedControllableCosts").GetDecimal());

        // Dairy holds the milk: deleted only by moving it, as it is, to a raw materials category;
        // to Utilities it would be converted, which a plain delete does not do.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.DeleteAsync($"/api/inventory/purchase-categories/{dairy}", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.DeleteAsync($"/api/inventory/purchase-categories/{dairy}?moveTo={utilities}", Cancel)).StatusCode);
        var fresh = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/inventory/purchase-categories/{fresh}", new { name = "Fresh", kind = "RawMaterials" }, Cancel));
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/inventory/purchase-categories/{dairy}?moveTo={fresh}", Cancel)).StatusCode);
        Assert.Equal("Archived", (await Json(await manager.DeleteAsync($"/api/inventory/expense-items/{electricity}", Cancel))).GetProperty("outcome").GetString());
    }

    // The accountant: stock and costing data entry, no orders, and deletes that wait for a
    // manager, who sees them, rejects or approves.
    [Fact]
    public async Task An_accountants_deletes_wait_for_a_manager()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);

        async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel);
        }

        var me = (await accountant.GetFromJsonAsync<Me>("/api/auth/me", TestApi.Json, Cancel))!;
        Assert.Equal(UserRole.Accountant, me.Role);
        Assert.Equal(RolePermissions.For(UserRole.Accountant).Order(StringComparer.Ordinal), me.Permissions);
        Assert.DoesNotContain(Permissions.ApprovalDecide, me.Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync("/api/orders", new { type = "Takeaway" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/costing/menu", Cancel)).StatusCode);

        var salt = (await Json(await accountant.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "Salt", unit = "Gram" }, Cancel)))
            .GetProperty("id").GetGuid();
        object Ask(Guid id) => new { requestId = Guid.NewGuid(), module = "RawMaterial", recordId = id, reason = "Duplicate" };

        var asked = await accountant.PostAsJsonAsync("/api/inventory/deletion-requests", Ask(salt), Cancel);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal("Requested", (await Json(asked)).GetProperty("outcome").GetString());
        // Seen by both, to mark it; decided only by the manager, who also sees the history.
        var seen = await Json(await accountant.GetAsync("/api/inventory/approval-requests?decided=10", Cancel));
        var requestId = Assert.Single(seen.GetProperty("pending").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Empty(seen.GetProperty("decided").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await accountant.PostAsync($"/api/inventory/approval-requests/{requestId}/approve", null, Cancel)).StatusCode);
        // Locked while it waits.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await accountant.PutAsJsonAsync($"/api/inventory/raw-materials/{salt}", new { name = "Sea salt", reorderLevel = 0 }, Cancel)).StatusCode);

        var rejected = await Json(await manager.PostAsJsonAsync($"/api/inventory/approval-requests/{requestId}/reject", new { note = "Still used" }, Cancel));
        Assert.Equal(("Rejected", "Still used"), (rejected.GetProperty("status").GetString(), rejected.GetProperty("decisionNote").GetString()));
        await Json(await accountant.PutAsJsonAsync($"/api/inventory/raw-materials/{salt}", new { name = "Sea salt", reorderLevel = 0 }, Cancel));

        var again = await Json(await accountant.PostAsJsonAsync("/api/inventory/deletion-requests", Ask(salt), Cancel));
        await Json(await manager.PostAsync($"/api/inventory/approval-requests/{again.GetProperty("request").GetProperty("id").GetGuid()}/approve", null, Cancel));
        Assert.DoesNotContain((await Json(await manager.GetAsync("/api/inventory/raw-materials", Cancel))).EnumerateArray(), m => m.GetProperty("id").GetGuid() == salt);
        var history = await Json(await manager.GetAsync("/api/inventory/approval-requests?decided=10", Cancel));
        Assert.Equal(["Approved", "Rejected"], history.GetProperty("decided").EnumerateArray().Select(d => d.GetProperty("status").GetString()));

        // A manager's delete is done (200); and the old recipe delete answers as before for them.
        var pepper = (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "Pepper", unit = "Gram" }, Cancel)))
            .GetProperty("id").GetGuid();
        var done = await manager.PostAsJsonAsync("/api/inventory/deletion-requests", Ask(pepper), Cancel);
        Assert.Equal((HttpStatusCode.OK, "Applied"), (done.StatusCode, (await Json(done)).GetProperty("outcome").GetString()));
    }

    // Tax and service: the tax rate is the bills' own, shown to whoever keeps the books; the
    // service charge is a manager's to set. A day's takings with the tax in them: the tax kept
    // apart, all of the cash in the safe, and the day reconciled with its sales and in the daily cash,
    // which a cashier does not see.
    [Fact]
    public async Task Tax_and_service_are_a_managers_to_set_and_takings_keep_their_tax_apart()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);

        async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel);
        }

        var settings = await Json(await accountant.GetAsync("/api/financials/settings", Cancel));
        Assert.Equal((14m, 0m), (settings.GetProperty("taxPercent").GetDecimal(), settings.GetProperty("servicePercent").GetDecimal()));
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync("/api/financials/settings", new { servicePercent = 12 }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PutAsJsonAsync("/api/financials/settings", new { servicePercent = 100 }, Cancel)).StatusCode);
        var saved = await Json(await manager.PutAsJsonAsync("/api/financials/settings", new { servicePercent = 12 }, Cancel));
        Assert.Equal(12m, saved.GetProperty("servicePercent").GetDecimal());
        Assert.False(string.IsNullOrEmpty(saved.GetProperty("updatedBy").GetString()));

        // 1,140 counted with the tax in it, whatever rate the screen sends: 140 of tax, 1,140 in the safe.
        var today = (await Json(await manager.GetAsync("/api/financials/today", Cancel))).GetProperty("today").GetString();
        var entry = await Json(await manager.PostAsJsonAsync("/api/financials/vouchers", new
        {
            requestId = Guid.NewGuid(), transactionId = Guid.NewGuid(), kind = "RevenueTransfer", amount = 1_140, revenueDate = today, includesTax = true,
            taxPercent = 1,
        }, Cancel));
        Assert.Equal((true, 14m, 140m), (entry.GetProperty("includesTax").GetBoolean(), entry.GetProperty("taxPercent").GetDecimal(), entry.GetProperty("taxAmount").GetDecimal()));
        var month = DateOnly.Parse(today!, System.Globalization.CultureInfo.InvariantCulture);
        var safe = await Json(await manager.GetAsync($"/api/financials/safe?year={month.Year}&month={month.Month}", Cancel));
        Assert.Equal(1_140m, safe.GetProperty("balanceNow").GetDecimal());
        Assert.Equal(140m, safe.GetProperty("entries")[0].GetProperty("tax").GetDecimal());

        // Nothing sold: the 1,000 before tax is all cash for items not rung up.
        var report = await Json(await accountant.GetAsync($"/api/financials/reconciliation?from={today}&to={today}", Cancel));
        var total = report.GetProperty("total");
        Assert.Equal((1_000m, 0m, 1_000m), (total.GetProperty("netCash").GetDecimal(), total.GetProperty("expectedCash").GetDecimal(), total.GetProperty("difference").GetDecimal()));

        // Nothing paid out of the day's takings: all of it reached the safe.
        string Daily = $"/api/financials/daily-cash?from={today}&to={today}";
        var day = (await Json(await accountant.GetAsync(Daily, Cancel))).GetProperty("days").EnumerateArray().Single();
        Assert.Equal((1_140m, 0m, 1_140m, "Takings entered"), (day.GetProperty("gross").GetDecimal(), day.GetProperty("deductions").GetDecimal(),
            day.GetProperty("netToSafe").GetDecimal(), day.GetProperty("entered").EnumerateArray().Single().GetProperty("description").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.CashierAsync(manager)).GetAsync(Daily, Cancel)).StatusCode);

        // The day's takings are in: the Takings in screen says so, and a second entry is refused.
        var preview = await Json(await accountant.GetAsync($"/api/financials/takings?day={today}", Cancel));
        Assert.Equal((1_140m, 0m), (preview.GetProperty("entered").GetDecimal(), preview.GetProperty("paidOut").GetDecimal()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync("/api/financials/vouchers", new
        {
            requestId = Guid.NewGuid(), transactionId = Guid.NewGuid(), kind = "RevenueTransfer", amount = 500, revenueDate = today,
        }, Cancel)).StatusCode);
    }

    // Raw materials brought in from Excel: the template downloaded, filled in and imported by
    // whoever manages stock, never a cashier. Each good row a purchased stock item; a name already
    // taken, or a unit the till does not know, skipped and said why.
    [Fact]
    public async Task Raw_materials_are_imported_from_the_excel_template()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var accountant = await api.AccountantAsync(manager);

        async Task<JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        }
        MultipartFormDataContent Upload(byte[] bytes, string name = "materials.xlsx")
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(RawMaterialWorkbook.ContentType);
            return new MultipartFormDataContent { { file, "file", name } };
        }
        static byte[] Saved(ClosedXML.Excel.XLWorkbook workbook)
        {
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        // Dairy and Tomatoes are there already.
        await Json(await manager.PutAsJsonAsync($"/api/inventory/purchase-categories/{Guid.NewGuid()}", new { name = "Dairy" }, Cancel));
        await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "Tomatoes", unit = "Gram" }, Cancel));

        // The template: its headings, nothing to import by mistake, and the dropdowns' lists.
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/inventory/raw-materials/template", Cancel)).StatusCode);
        var download = await accountant.GetAsync("/api/inventory/raw-materials/template", Cancel);
        Assert.True(download.IsSuccessStatusCode);
        Assert.Equal(RawMaterialWorkbook.ContentType, download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(RawMaterialWorkbook.FileName, download.Content.Headers.ContentDisposition!.FileNameStar);
        using var template = new ClosedXML.Excel.XLWorkbook(await download.Content.ReadAsStreamAsync(Cancel));
        var sheet = template.Worksheet(RawMaterialWorkbook.DataSheetName);
        Assert.Equal(
            [RawMaterialWorkbook.NameHeader, RawMaterialWorkbook.CategoryHeader, RawMaterialWorkbook.UnitHeader, RawMaterialWorkbook.CostHeader],
            sheet.Row(1).CellsUsed().Select(c => c.GetString()));
        Assert.True(sheet.Cell(2, 1).IsEmpty());
        var lists = template.Worksheet("Lists");
        Assert.Equal(ClosedXML.Excel.XLWorksheetVisibility.Hidden, lists.Visibility);
        Assert.Contains("Dairy", lists.Column(1).CellsUsed().Select(c => c.GetString()));
        Assert.Equal(["Kg", "Liter", "Piece"], lists.Column(2).CellsUsed().Skip(1).Select(c => c.GetString()));
        Assert.Equal(3, sheet.DataValidations.Count());

        // Filled in: a new material in a new category, one in Dairy, one already there, one in sacks.
        object[][] rows =
        [
            ["Chicken breast", "Poultry", "Kg", 185],
            ["Milk", "dairy", "Liter", 42.5],
            ["tomatoes", "", "Kg", ""],
            ["Flour", "Dry goods", "Sack", 500],
        ];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                sheet.Cell(r + 2, c + 1).Value = ClosedXML.Excel.XLCellValue.FromObject(rows[r][c]);

        var filled = Saved(template);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsync("/api/inventory/raw-materials/import", Upload(filled), Cancel)).StatusCode);
        var result = await Json(await accountant.PostAsync("/api/inventory/raw-materials/import", Upload(filled), Cancel));
        Assert.Equal(["Chicken breast", "Milk"], result.GetProperty("created").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal(
            [(4, "Tomatoes is already on the list."), (5, "\"Sack\" is not a unit the till knows: Kg, Liter or Piece.")],
            result.GetProperty("skipped").EnumerateArray().Select(s => (s.GetProperty("row").GetInt32(), s.GetProperty("reason").GetString())));
        Assert.Equal(["Poultry"], result.GetProperty("categoriesAdded").EnumerateArray().Select(c => c.GetString()));

        // On the costing list as stock items, priced per kg and per litre.
        var materials = (await Json(await manager.GetAsync("/api/costing/materials", Cancel))).EnumerateArray().ToList();
        JsonElement Named(string name) => materials.Single(m => m.GetProperty("name").GetString() == name);
        (string?, string?, string?, decimal, string?) Seen(JsonElement m) => (
            m.GetProperty("category").GetString(), m.GetProperty("unit").GetString(), m.GetProperty("purchaseUnit").GetString(),
            m.GetProperty("costPerPurchaseUnit").GetDecimal(), m.GetProperty("kind").GetString());
        Assert.Equal(("Poultry", "Gram", "kg", 185m, "Purchased"), Seen(Named("Chicken breast")));
        Assert.Equal(("Dairy", "Millilitre", "L", 42.5m, "Purchased"), Seen(Named("Milk")));
        Assert.Single(materials, m => m.GetProperty("name").GetString() == "Tomatoes");

        // Headed in Arabic only, its columns in another order: read as written.
        using var arabic = new ClosedXML.Excel.XLWorkbook();
        var ws = arabic.AddWorksheet("خامات");
        string[] headings = ["وحدة القياس", "اسم الخامة", "التكلفة المبدئية", "القسم"];
        for (var c = 0; c < headings.Length; c++)
            ws.Cell(1, c + 1).Value = headings[c];
        ws.Cell(2, 1).Value = "قطعة";
        ws.Cell(2, 2).Value = "بيض";
        ws.Cell(2, 3).Value = 4.25;
        ws.Cell(2, 4).Value = "Dairy";
        var eggs = (await Json(await manager.PostAsync("/api/inventory/raw-materials/import", Upload(Saved(arabic)), Cancel))).GetProperty("created")[0];
        Assert.Equal(
            ("بيض", "Dairy", "Piece", 4.25m),
            (eggs.GetProperty("name").GetString(), eggs.GetProperty("category").GetString(), eggs.GetProperty("unit").GetString(),
                eggs.GetProperty("costPerPurchaseUnit").GetDecimal()));

        // Not a workbook at all: said so.
        var notExcel = await manager.PostAsync("/api/inventory/raw-materials/import", Upload("not a workbook"u8.ToArray(), "notes.xlsx"), Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notExcel.StatusCode);
        Assert.Contains("could not be read as an Excel workbook", await notExcel.Content.ReadAsStringAsync(Cancel));
    }

    // A day's purchases entered together: each supplier's its own invoice, none saved unless all
    // can be, each problem said by its invoice; an accountant's paid only once a manager approves.
    [Fact]
    public async Task A_days_purchases_are_sent_together_as_an_invoice_for_each_supplier()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var accountant = await api.AccountantAsync(manager);

        async Task<JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        }

        var tomatoes = (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "Tomatoes", unit = "Gram" }, Cancel)))
            .GetProperty("id").GetGuid();
        var nile = (await Json(await manager.PutAsJsonAsync($"/api/inventory/suppliers/{Guid.NewGuid()}", new { name = "Nile Farms" }, Cancel)))
            .GetProperty("id").GetGuid();
        object Invoice(Guid? supplier, decimal cost) => new
        {
            invoiceId = Guid.NewGuid(), supplierId = supplier, supplierInvoiceNumber = (string?)null,
            lines = new[] { new { rawMaterialId = tomatoes, enteredQuantity = 5, unitLabel = "kg", unitSize = 1000, cost, itemType = "Inventory" } },
        };
        object Batch(string method, params object[] invoices) => new { invoiceDate = "2026-10-13", paymentMethod = method, invoices };

        Assert.Equal(HttpStatusCode.Forbidden,
            (await cashier.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", Batch("Safe", Invoice(nile, 100)), Cancel)).StatusCode);

        // On credit, the second has no supplier: refused, said by its invoice, and nothing saved.
        var refused = await manager.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", Batch("Credit", Invoice(nile, 100), Invoice(null, 50)), Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("errors")[0].GetString()!;
        Assert.StartsWith("Invoice 2: ", problem);
        Assert.Empty((await Json(await manager.GetAsync("/api/inventory/purchase-invoices", Cancel))).EnumerateArray());

        // The accountant's from the safe: both posted, each payment waiting for a manager.
        var made = (await Json(await accountant.PostAsJsonAsync("/api/inventory/purchase-invoices/batch",
            Batch("Safe", Invoice(nile, 100), Invoice(null, 60)), Cancel))).GetProperty("invoices").EnumerateArray().ToList();
        Assert.Equal([("Posted", "Pending"), ("Posted", "Pending")], made.Select(i => (i.GetProperty("status").GetString(), i.GetProperty("paymentStatus").GetString())));
        var pending = (await Json(await manager.GetAsync("/api/inventory/approval-requests", Cancel))).GetProperty("pending").EnumerateArray().ToList();
        Assert.Equal(2, pending.Count(r => r.GetProperty("module").GetString() == "Treasury"));

        // Bought on credit, then 50 of it paid in a batch of its own: no invoice, off Nile Farms' account.
        var bought = (await Json(await manager.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", Batch("Credit", Invoice(nile, 80)), Cancel)))
            .GetProperty("invoices")[0].GetProperty("id").GetGuid();
        var paid = (await Json(await manager.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", new
        {
            invoiceDate = "2026-10-13", paymentMethod = "Safe", invoices = Array.Empty<object>(),
            payments = new[] { new { transactionId = Guid.NewGuid(), supplierId = nile, amount = 50, notes = "Receipt R-1" } },
        }, Cancel))).GetProperty("payments").EnumerateArray().Single();
        Assert.Equal(("Nile Farms", 50m, "Receipt R-1", "Applied"), (paid.GetProperty("supplier").GetString(), paid.GetProperty("amount").GetDecimal(),
            paid.GetProperty("notes").GetString(), paid.GetProperty("outcome").GetString()));
        var account = await Json(await manager.GetAsync($"/api/financials/suppliers/{nile}", Cancel));
        Assert.Equal(30m, account.GetProperty("balance").GetDecimal());

        // The rest paid on its own, as the new invoice screen does, out of a day's takings: off the
        // account, and paid out of those takings, not the safe.
        var day = (await Json(await manager.GetAsync("/api/financials/today", Cancel))).GetProperty("today").GetString();
        var payment = Guid.NewGuid();
        await Json(await manager.PostAsJsonAsync("/api/financials/vouchers", new
        {
            requestId = payment, transactionId = payment, kind = "SupplierPayment", amount = 30, supplierId = nile, revenueDate = day, notes = "R-2",
        }, Cancel));
        Assert.Equal(0m, (await Json(await manager.GetAsync($"/api/financials/suppliers/{nile}", Cancel))).GetProperty("balance").GetDecimal());
        var takings = await Json(await manager.GetAsync($"/api/financials/takings?day={day}", Cancel));
        Assert.Equal((30m, 1), (takings.GetProperty("paidOut").GetDecimal(), takings.GetProperty("supplierPayments").GetInt32()));

        // The credit invoice corrected, 5 kg costing 90 not 80: a cashier may not; the accountant's
        // waits for a manager (202), the invoice locked meanwhile.
        object Fix() => new
        {
            requestId = Guid.NewGuid(), newInvoiceId = Guid.NewGuid(), supplierId = nile, supplierInvoiceNumber = (string?)null, invoiceDate = "2026-10-13",
            paymentMethod = "Credit",
            lines = new[] { new { rawMaterialId = tomatoes, enteredQuantity = 5, unitLabel = "kg", unitSize = 1000, cost = 90, itemType = "Inventory" } },
        };
        var correct = $"/api/inventory/purchase-invoices/{bought}/correct";
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync(correct, Fix(), Cancel)).StatusCode);
        // Reviewed first: what changes, as the manager will see it, and nothing saved.
        var preview = await Json(await accountant.PostAsJsonAsync($"{correct}/preview", Fix(), Cancel));
        Assert.Equal("Invoice of 13 Oct 2026 · Nile Farms corrected · L.E 80.00 → L.E 90.00", preview.GetProperty("summary").GetString());
        Assert.Equal("5 kg · L.E 90.00", preview.GetProperty("changes")[0].GetProperty("after").GetString());
        var asked = await accountant.PostAsJsonAsync(correct, Fix(), Cancel);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal("Requested", (await asked.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync(correct, Fix(), Cancel)).StatusCode);
    }

    // A category deleted with its raw materials made expense items: reviewed first, nothing saved;
    // an accountant's waits for a manager (202); a cashier may not. Moved as they are, it is the
    // plain delete it always was.
    [Fact]
    public async Task A_category_is_deleted_with_its_contents_converted_on_a_managers_say()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var accountant = await api.AccountantAsync(manager);

        async Task<JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        }
        async Task<Guid> Category(string name, string kind = "RawMaterials")
        {
            var id = Guid.NewGuid();
            await Json(await manager.PutAsJsonAsync($"/api/inventory/purchase-categories/{id}", new { name, kind }, Cancel));
            return id;
        }
        var cleaning = await Category("Cleaning stock");
        var maintenance = await Category("Maintenance", "Expenses");
        await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "Bleach", unit = "Piece", categoryId = cleaning }, Cancel));
        object Body() => new { requestId = Guid.NewGuid(), materialsTo = maintenance, reason = "Cleaning is an expense" };
        var path = $"/api/inventory/purchase-categories/{cleaning}/delete";

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync(path, Body(), Cancel)).StatusCode);
        var preview = await Json(await accountant.PostAsJsonAsync($"{path}/preview", Body(), Cancel));
        Assert.Equal(("Category Cleaning stock deleted: 1 raw material becomes an expense item in Maintenance", true),
            (preview.GetProperty("summary").GetString(), preview.GetProperty("converts").GetBoolean()));

        var asked = await accountant.PostAsJsonAsync(path, Body(), Cancel);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var request = (await asked.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("request");
        Assert.Equal(("PurchaseCategory", "Delete"), (request.GetProperty("module").GetString(), request.GetProperty("action").GetString()));
        // Locked meanwhile, even to a manager's plain delete.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await manager.DeleteAsync($"/api/inventory/purchase-categories/{cleaning}?moveTo={maintenance}", Cancel)).StatusCode);
        await Json(await manager.PostAsync($"/api/inventory/approval-requests/{request.GetProperty("id").GetGuid()}/approve", null, Cancel));
        var expenses = await Json(await manager.GetAsync("/api/inventory/expense-items", Cancel));
        Assert.Contains(expenses.EnumerateArray(), e => e.GetProperty("name").GetString() == "Bleach");

        // Moved as they are: deleted at once, as before.
        var spare = await Category("Spare");
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.DeleteAsync($"/api/inventory/purchase-categories/{spare}", Cancel)).StatusCode);
    }

    // A material's purchase history card, for whoever sees the costs: each invoice line that bought
    // it. A cashier does not.
    [Fact]
    public async Task A_materials_purchase_history_is_read_by_whoever_sees_the_costs()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var accountant = await api.AccountantAsync(manager);

        async Task<JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        }
        var tomatoes = (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "Tomatoes", unit = "Gram" }, Cancel)))
            .GetProperty("id").GetGuid();
        var nile = (await Json(await manager.PutAsJsonAsync($"/api/inventory/suppliers/{Guid.NewGuid()}", new { name = "Nile Farms" }, Cancel)))
            .GetProperty("id").GetGuid();
        var invoice = Guid.NewGuid();
        await Json(await manager.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", new
        {
            invoiceDate = "2026-10-13", paymentMethod = "Credit",
            invoices = new[]
            {
                new
                {
                    invoiceId = invoice, supplierId = nile, supplierInvoiceNumber = "F-1",
                    lines = new[] { new { rawMaterialId = tomatoes, enteredQuantity = 6, unitLabel = "kg", unitSize = 1000, cost = 180, itemType = "Inventory" } },
                },
            },
        }, Cancel));

        var path = $"/api/costing/materials/{tomatoes}/purchases";
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(path, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/costing/materials/{Guid.NewGuid()}/purchases", Cancel)).StatusCode);
        var card = await Json(await accountant.GetAsync(path, Cancel));
        var line = card.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal((invoice, "2026-10-13", "Nile Farms", "F-1", 6m, "kg", 6_000m, 30m, 180m, false),
            (line.GetProperty("invoiceId").GetGuid(), line.GetProperty("date").GetString(), line.GetProperty("supplier").GetString(),
                line.GetProperty("invoiceNumber").GetString(), line.GetProperty("quantity").GetDecimal(), line.GetProperty("unit").GetString(),
                line.GetProperty("baseQuantity").GetDecimal(), line.GetProperty("unitPrice").GetDecimal(), line.GetProperty("total").GetDecimal(),
                line.GetProperty("voided").GetBoolean()));
        Assert.Equal(("Gram", 30m), (card.GetProperty("unit").GetString(), card.GetProperty("averagePerPurchaseUnit").GetDecimal()));
    }

    // The unit a material is counted in changed: at once while nothing records it, for whoever
    // keeps stock; once something does, with the conversion, at once for a manager (200) and asked
    // of one for an accountant (202), the material locked meanwhile. A cashier may not.
    [Fact]
    public async Task A_materials_unit_is_changed_with_its_records_restated()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var accountant = await api.AccountantAsync(manager);

        async Task<JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        }
        async Task<Guid> Material(string name) =>
            (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name, unit = "Gram" }, Cancel))).GetProperty("id").GetGuid();
        object Pieces(decimal? oneNewIsOld) => new { requestId = Guid.NewGuid(), unit = "Piece", oneNewIsOld, purchaseUnit = "crate", purchaseUnitSize = 100 };

        // Not bought yet: the accountant's is done at once.
        var lemons = await Material("Lemons");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/inventory/raw-materials/{lemons}/unit", Pieces(null), Cancel)).StatusCode);
        var unused = await accountant.PostAsJsonAsync($"/api/inventory/raw-materials/{lemons}/unit", Pieces(null), Cancel);
        Assert.Equal(HttpStatusCode.OK, unused.StatusCode);
        var applied = await unused.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        Assert.Equal(("Applied", "Piece"), (applied.GetProperty("outcome").GetString(), applied.GetProperty("material").GetProperty("unit").GetString()));

        // Bought: what records it, and the conversion needed.
        var tomatoes = await Material("Tomatoes");
        await Json(await manager.PostAsJsonAsync("/api/inventory/purchase-invoices/batch", new
        {
            invoiceDate = "2026-10-13", paymentMethod = "Credit",
            invoices = new[]
            {
                new
                {
                    invoiceId = Guid.NewGuid(), supplierId = (await Json(await manager.PutAsJsonAsync($"/api/inventory/suppliers/{Guid.NewGuid()}", new { name = "Nile Farms" }, Cancel))).GetProperty("id").GetGuid(),
                    lines = new[] { new { rawMaterialId = tomatoes, enteredQuantity = 6, unitLabel = "kg", unitSize = 1000, cost = 180, itemType = "Inventory" } },
                },
            },
        }, Cancel));
        var usage = await Json(await accountant.GetAsync($"/api/inventory/raw-materials/{tomatoes}/usage", Cancel));
        Assert.Equal((1, 1, true), (usage.GetProperty("stockMovements").GetInt32(), usage.GetProperty("invoiceLines").GetInt32(), usage.GetProperty("inUse").GetBoolean()));
        var unit = $"/api/inventory/raw-materials/{tomatoes}/unit";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync(unit, Pieces(null), Cancel)).StatusCode);

        // The accountant's waits (202), and locks it; approved, it is 100 pieces.
        var asked = await accountant.PostAsJsonAsync(unit, Pieces(60), Cancel);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var request = (await asked.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("request");
        Assert.Equal(("RawMaterial", "Update"), (request.GetProperty("module").GetString(), request.GetProperty("action").GetString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync(unit, Pieces(60), Cancel)).StatusCode);
        await Json(await manager.PostAsync($"/api/inventory/approval-requests/{request.GetProperty("id").GetGuid()}/approve", null, Cancel));
        var now = (await Json(await manager.GetAsync("/api/inventory/raw-materials", Cancel))).EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == tomatoes);
        Assert.Equal(("Piece", 100m), (now.GetProperty("unit").GetString(), now.GetProperty("quantityOnHand").GetDecimal()));

        // The manager's own, back to grams: at once.
        var back = await manager.PostAsJsonAsync(unit, new { requestId = Guid.NewGuid(), unit = "Gram", oneNewIsOld = 1m / 60, purchaseUnit = "kg", purchaseUnitSize = 1000 }, Cancel);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(6_000m, (await back.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("material").GetProperty("quantityOnHand").GetDecimal());
    }

    // A sheet of batch recipes (المصنعات), as kitchens write them: a prep item's name on its own row,
    // a heading row, then its ingredients. A manager's import makes them; an accountant's asks.
    [Fact]
    public async Task Prep_items_are_imported_from_a_sheet_of_batch_recipes()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);
        var accountant = await api.AccountantAsync(manager);

        async Task<JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        }
        static MultipartFormDataContent Upload(byte[] bytes) =>
            new() { { new ByteArrayContent(bytes), "file", "مصنعات.xlsx" } };
        static byte[] Sheet(params object[][] rows)
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var sheet = workbook.AddWorksheet("Sheet1");
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    sheet.Cell(r + 1, c + 1).Value = ClosedXML.Excel.XLCellValue.FromObject(rows[r][c]);
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "زبدة_فيرن_", unit = "Gram" }, Cancel));
        object[] heading = ["الصنف", "الوحدة", "الكمية"];
        var sheet = Sheet(
            heading,
            ["صوص_دميجلاس"],
            heading,
            ["زبدة_فيرن_", "كيلو", 0.2],
            ["كريمه", "لتر", 0.3],
            ["ملح_", "", 0.025],
            ["طبق_فاكهه"],
            heading,
            ["تفاح", "كيلو", 0.1]);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsync("/api/inventory/prep-items/import", Upload(sheet), Cancel)).StatusCode);
        var made = await Json(await manager.PostAsync("/api/inventory/prep-items/import", Upload(sheet), Cancel));
        Assert.Equal(["صوص_دميجلاس", "طبق_فاكهه"], made.GetProperty("imported").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        var sauce = made.GetProperty("imported")[0];
        Assert.Equal((500m, 2, false), (sauce.GetProperty("batchQuantity").GetDecimal(), sauce.GetProperty("ingredients").GetInt32(), sauce.GetProperty("requested").GetBoolean()));
        Assert.Contains(sauce.GetProperty("notes").EnumerateArray(), n => n.GetString() == "ملح_ 0.025: no unit, left out.");
        Assert.Equal(["كريمه", "تفاح"], made.GetProperty("materialsAdded").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal("مصنعات", made.GetProperty("categoryAdded").GetString());

        // On the prep items list, as prep items, with their recipes.
        var items = (await Json(await manager.GetAsync("/api/inventory/prep-items", Cancel))).EnumerateArray().ToList();
        var listed = items.Single(i => i.GetProperty("name").GetString() == "صوص_دميجلاس");
        Assert.Equal(("مصنعات", "Gram", 500m, 2), (listed.GetProperty("category").GetString(), listed.GetProperty("unit").GetString(),
            listed.GetProperty("batchQuantity").GetDecimal(), listed.GetProperty("ingredients").GetArrayLength()));

        // The accountant's: asked of a manager, nothing made until then.
        var asked = await Json(await accountant.PostAsync("/api/inventory/prep-items/import", Upload(Sheet(heading, ["بطاطس_بوريه"], heading, ["بطاطس", "كيلو", 2.5])), Cancel));
        Assert.True(asked.GetProperty("imported")[0].GetProperty("requested").GetBoolean());
        var pending = (await Json(await manager.GetAsync("/api/inventory/approval-requests", Cancel))).GetProperty("pending").EnumerateArray().ToList();
        Assert.Contains(pending, r => r.GetProperty("module").GetString() == "PrepItem" && r.GetProperty("action").GetString() == "Create");
        Assert.DoesNotContain((await Json(await manager.GetAsync("/api/inventory/prep-items", Cancel))).EnumerateArray(),
            i => i.GetProperty("name").GetString() == "بطاطس_بوريه");

        // Not a workbook at all: said so.
        var notExcel = await manager.PostAsync("/api/inventory/prep-items/import", Upload("not a workbook"u8.ToArray()), Cancel);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notExcel.StatusCode);
    }

    // An accountant's prep item and batch: each answered 202, a request, with nothing made until
    // the manager approves; the manager sees what approving would move, at today's averages. A
    // manager's own correction is done at once (200), and kept with the approvals.
    [Fact]
    public async Task An_accountants_prep_items_and_batches_wait_for_a_manager()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);

        async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel);
        }
        async Task<System.Text.Json.JsonElement> Approved(HttpResponseMessage asked)
        {
            Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
            var request = (await Json(asked)).GetProperty("request");
            return await Json(await manager.PostAsync($"/api/inventory/approval-requests/{request.GetProperty("id").GetGuid()}/approve", null, Cancel));
        }

        var flour = (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new
        {
            name = "Flour", unit = "Gram", purchaseUnit = "kg", purchaseUnitSize = 1000, costPerPurchaseUnit = 20,
        }, Cancel))).GetProperty("id").GetGuid();
        await Json(await manager.PostAsJsonAsync("/api/inventory/stock-counts", new
        {
            stockCountId = Guid.NewGuid(), lines = new[] { new { rawMaterialId = flour, countedQuantity = 10_000 } },
        }, Cancel));

        var doughBody = new { name = "Pizza dough", unit = "Gram", batchQuantity = 10_000, ingredients = new[] { new { rawMaterialId = flour, quantity = 6_000 } } };
        var approved = await Approved(await accountant.PostAsJsonAsync("/api/inventory/prep-items", doughBody, Cancel));
        Assert.Equal(("PrepItem", "Create", "Approved"),
            (approved.GetProperty("module").GetString(), approved.GetProperty("action").GetString(), approved.GetProperty("status").GetString()));
        var dough = approved.GetProperty("recordId").GetGuid();
        Assert.Equal("Pizza dough", (await Json(await accountant.GetAsync($"/api/inventory/prep-items/{dough}", Cancel))).GetProperty("name").GetString());

        // Her batch: a request, with what it would move now, and nothing made.
        var orderId = Guid.NewGuid();
        var batch = await accountant.PutAsJsonAsync($"/api/inventory/production-orders/{orderId}",
            new { prepItemId = dough, plannedQuantity = 10_000, producedQuantity = 10_000 }, Cancel);
        Assert.Equal(HttpStatusCode.Accepted, batch.StatusCode);
        var requestId = (await Json(batch)).GetProperty("request").GetProperty("id").GetGuid();
        Assert.Empty((await Json(await manager.GetAsync("/api/inventory/production-orders", Cancel))).EnumerateArray());
        var preview = await Json(await manager.GetAsync($"/api/inventory/approval-requests/{requestId}/preview", Cancel));
        Assert.Equal(120m, preview.GetProperty("batch").GetProperty("totalCost").GetDecimal());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await accountant.PostAsync($"/api/inventory/approval-requests/{requestId}/approve", null, Cancel)).StatusCode);
        await Json(await manager.PostAsync($"/api/inventory/approval-requests/{requestId}/approve", null, Cancel));
        var made = await Json(await manager.GetAsync($"/api/inventory/production-orders/{orderId}", Cancel));
        Assert.Equal(("Made", "Boss"), (made.GetProperty("status").GetString(), made.GetProperty("approvedBy").GetString()));

        // The manager's own correction: done at once, and kept with the decisions.
        var corrected = Guid.NewGuid();
        var correction = await manager.PostAsJsonAsync($"/api/inventory/production-orders/{orderId}/revision", new
        {
            requestId = Guid.NewGuid(), newOrderId = corrected, plannedQuantity = 10_000, producedQuantity = 9_500,
        }, Cancel);
        Assert.Equal((HttpStatusCode.OK, "Applied"), (correction.StatusCode, (await Json(correction)).GetProperty("outcome").GetString()));
        Assert.Equal("Voided", (await Json(await manager.GetAsync($"/api/inventory/production-orders/{orderId}", Cancel))).GetProperty("status").GetString());
        Assert.Equal(orderId, (await Json(await manager.GetAsync($"/api/inventory/production-orders/{corrected}", Cancel))).GetProperty("revisionOf").GetGuid());
        var history = await Json(await manager.GetAsync("/api/inventory/approval-requests?decided=10", Cancel));
        Assert.Equal(["Update", "Create", "Create"], history.GetProperty("decided").EnumerateArray().Select(d => d.GetProperty("action").GetString()));
    }

    // The treasury over HTTP: a cashier has none of it; an accountant's safe entry is a request
    // (202) and her cash invoice's payment waits; a manager's are done (200).
    [Fact]
    public async Task An_accountants_safe_entries_wait_for_a_manager()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var cashier = await api.CashierAsync(manager);

        async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel);
        }
        var today = DateTime.Now;
        string Safe = $"/api/financials/safe?year={today.Year}&month={today.Month}";
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(Safe, Cancel)).StatusCode);

        object Opening(Guid id) => new { requestId = Guid.NewGuid(), transactionId = id, kind = "OpeningBalance", amount = 2_000, source = "OwnerFinancing" };
        var asked = await accountant.PostAsJsonAsync("/api/financials/vouchers", Opening(Guid.NewGuid()), Cancel);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var request = (await Json(asked)).GetProperty("request");
        Assert.Equal(("Treasury", "Create"), (request.GetProperty("module").GetString(), request.GetProperty("action").GetString()));
        Assert.Equal(0m, (await Json(await manager.GetAsync(Safe, Cancel))).GetProperty("balanceNow").GetDecimal());
        await Json(await manager.PostAsync($"/api/inventory/approval-requests/{request.GetProperty("id").GetGuid()}/approve", null, Cancel));
        var month = await Json(await accountant.GetAsync(Safe, Cancel));
        Assert.Equal((2_000m, "OpeningEntry"), (month.GetProperty("balanceNow").GetDecimal(), month.GetProperty("openingKind").GetString()));
        Assert.Equal(("Mona", "Boss"), (month.GetProperty("entries")[0].GetProperty("recordedBy").GetString(), month.GetProperty("entries")[0].GetProperty("approvedBy").GetString()));

        // The manager's own funding: done at once.
        var funded = await manager.PostAsJsonAsync("/api/financials/vouchers",
            new { requestId = Guid.NewGuid(), transactionId = Guid.NewGuid(), kind = "Funding", amount = 500, source = "Other", notes = "Loan" }, Cancel);
        Assert.Equal(HttpStatusCode.OK, funded.StatusCode);
        Assert.Equal(2_500m, (await Json(await manager.GetAsync(Safe, Cancel))).GetProperty("balanceNow").GetDecimal());

        // The accountant's cash invoice: posted, its payment waiting.
        var flour = (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials", new { name = "Flour", unit = "Gram" }, Cancel))).GetProperty("id").GetGuid();
        var invoiceId = Guid.NewGuid();
        await Json(await accountant.PutAsJsonAsync($"/api/inventory/purchase-invoices/{invoiceId}", new
        {
            paymentMethod = "Safe",
            lines = new[] { new { rawMaterialId = flour, enteredQuantity = 1, unitLabel = "kg", unitSize = 1000, cost = 30 } },
        }, Cancel));
        var posted = await Json(await accountant.PostAsync($"/api/inventory/purchase-invoices/{invoiceId}/post", null, Cancel));
        Assert.Equal("Pending", posted.GetProperty("paymentStatus").GetString());
        var listed = (await Json(await accountant.GetAsync("/api/inventory/purchase-invoices?status=Posted", Cancel))).EnumerateArray().Single();
        Assert.Equal(("Pending", "Safe"), (listed.GetProperty("paymentStatus").GetString(), listed.GetProperty("paymentMethod").GetString()));
        Assert.Equal(2_500m, (await Json(await manager.GetAsync(Safe, Cancel))).GetProperty("balanceNow").GetDecimal());
    }

    // Prep items and production orders: a manager's, as costs are on them. A batch is made under
    // the id the till sends, once.
    [Fact]
    public async Task Prep_items_are_made_by_a_manager_through_production_orders()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var cashier = await api.CashierAsync(manager);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/inventory/prep-items", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/inventory/production-orders", Cancel)).StatusCode);

        async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel);
        }
        var flour = (await Json(await manager.PostAsJsonAsync("/api/inventory/raw-materials",
            new { name = "Flour", unit = "Gram", reorderLevel = 0, purchaseUnit = "kg", purchaseUnitSize = 1000, costPerPurchaseUnit = 20 }, Cancel)))
            .GetProperty("id").GetGuid();
        var dough = await Json(await manager.PostAsJsonAsync("/api/inventory/prep-items", new
        {
            name = "Pizza dough",
            unit = "Gram",
            batchQuantity = 10_000,
            displayUnit = "kg",
            displayUnitSize = 1000,
            ingredients = new[] { new { rawMaterialId = flour, quantity = 6_000 } },
        }, Cancel));
        var doughId = dough.GetProperty("id").GetGuid();
        Assert.Equal(12m, dough.GetProperty("standardCostPerDisplayUnit").GetDecimal());

        var preview = await Json(await manager.GetAsync($"/api/inventory/production-orders/preview?prepItemId={doughId}&planned=5000&produced=4800", Cancel));
        Assert.Equal((60m, 12.5m), (preview.GetProperty("totalCost").GetDecimal(), preview.GetProperty("unitCostPerDisplayUnit").GetDecimal()));
        Assert.Equal(["Flour"], preview.GetProperty("shortages").EnumerateArray().Select(s => s.GetString()));

        var orderId = Guid.NewGuid();
        var body = new { prepItemId = doughId, plannedQuantity = 5_000, producedQuantity = 4_800, notes = "Lunch prep" };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/inventory/production-orders/{orderId}", body, Cancel)).StatusCode);
        var made = await Json(await manager.PutAsJsonAsync($"/api/inventory/production-orders/{orderId}", body, Cancel));
        await Json(await manager.PutAsJsonAsync($"/api/inventory/production-orders/{orderId}", body, Cancel));
        Assert.Equal((60m, 4_800m), (made.GetProperty("totalCost").GetDecimal(), made.GetProperty("producedQuantity").GetDecimal()));

        var profile = await Json(await manager.GetAsync($"/api/inventory/prep-items/{doughId}", Cancel));
        Assert.Equal((4_800m, 12.5m), (profile.GetProperty("quantityOnHand").GetDecimal(), profile.GetProperty("averageCostPerDisplayUnit").GetDecimal()));
        Assert.Single((await Json(await manager.GetAsync("/api/inventory/production-orders", Cancel))).EnumerateArray());
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
