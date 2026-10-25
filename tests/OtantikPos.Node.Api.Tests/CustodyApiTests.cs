using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OtantikPos.Node.Api.Tests;

// Custody & Assets through the API: categories, assets and their vouchers, a consumable's
// breakage in Costing → Waste, the assets bought on invoices; and the employee portal, its
// sign-in, what it shows, what it never reaches, and its own port.
public class CustodyApiTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
    }

    private static async Task<string> Problem(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<DateOnly> TodayAsync(HttpClient client) =>
        DateOnly.Parse((await Json(await client.GetAsync("/api/financials/today", Cancel))).GetProperty("today").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<Guid> HireAsync(HttpClient manager, string name, DateOnly hiredOn, string? mobile = null)
    {
        var id = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{id}", new
        {
            requestId = Guid.NewGuid(), name, jobTitle = "Chef", monthlySalary = 5_400, shiftStartsAt = "10:00:00", daysOff = Array.Empty<string>(),
            hiredOn = Iso(hiredOn), mobileNumber = mobile,
        }, Cancel));
        return id;
    }

    private static async Task ApproveAsync(HttpClient manager, JsonElement asked) =>
        await Json(await manager.PostAsJsonAsync($"/api/inventory/approval-requests/{asked.GetProperty("request").GetProperty("id").GetGuid()}/approve", new { }, Cancel));

    private static object Asset(string name, Guid? category, decimal? cost, int quantity) =>
        new { requestId = Guid.NewGuid(), name, categoryId = category, unitCost = cost, quantity };

    private static object In(Guid employee, DateOnly? date = null) =>
        new { requestId = Guid.NewGuid(), transactionId = Guid.NewGuid(), employeeId = employee, date = date is { } d ? Iso(d) : null };

    private static object Out(string reason, int quantity, Guid? transactionId = null) =>
        new { requestId = Guid.NewGuid(), transactionId = transactionId ?? Guid.NewGuid(), reason, quantity, notes = "Dropped a tray" };

    private static async Task<JsonElement> AssetAsync(HttpClient client, Guid id) =>
        (await Json(await client.GetAsync($"/api/custody/assets/{id}", Cancel))).GetProperty("asset");

    [Fact]
    public async Task Assets_are_handed_out_and_back_by_vouchers_and_a_consumables_breakage_goes_to_waste()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var cashier = await api.CashierAsync(manager);
        var today = await TodayAsync(manager);
        var hassan = await HireAsync(manager, "Hassan", today.AddMonths(-1));

        // Categories: a manager's; seen by whoever sees HR.
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/custody/categories", Cancel)).StatusCode);
        var (tableware, devices, uniforms) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await accountant.PutAsJsonAsync($"/api/custody/categories/{tableware}", new { name = "Tableware", isConsumable = true }, Cancel)).StatusCode);
        await Json(await manager.PutAsJsonAsync($"/api/custody/categories/{tableware}", new { name = "Tableware", isConsumable = true }, Cancel));
        await Json(await manager.PutAsJsonAsync($"/api/custody/categories/{devices}", new { name = "Devices", isConsumable = false }, Cancel));
        await Json(await manager.PutAsJsonAsync($"/api/custody/categories/{uniforms}", new { name = "Uniforms", isConsumable = false }, Cancel));
        Assert.Contains("There is a Tableware category already",
            await Problem(await manager.PutAsJsonAsync($"/api/custody/categories/{Guid.NewGuid()}", new { name = " tableware ", isConsumable = false }, Cancel)));

        // 40 plates entered in HR, at 35 each; a manager's at once.
        var plates = Guid.NewGuid();
        var made = await Json(await manager.PutAsJsonAsync($"/api/custody/assets/{plates}", Asset("Dinner plates", tableware, 35, 40), Cancel));
        var asset = made.GetProperty("asset");
        Assert.Equal(("Applied", "Dinner plates", 40, 1_400m, "InStore", "HrManual", true),
            (made.GetProperty("outcome").GetString(), asset.GetProperty("name").GetString(), asset.GetProperty("quantity").GetInt32(),
                asset.GetProperty("totalCost").GetDecimal(), asset.GetProperty("status").GetString(), asset.GetProperty("origin").GetString(),
                asset.GetProperty("isConsumable").GetBoolean()));
        // An accountant's waits for a manager, and is not there until approved.
        var fryer = Guid.NewGuid();
        var askedFryer = await Json(await accountant.PutAsJsonAsync($"/api/custody/assets/{fryer}", Asset("Fryer", devices, 9_000, 1), Cancel));
        Assert.Equal(("Requested", "New asset · Fryer × 1 · L.E 9,000.00 a piece"),
            (askedFryer.GetProperty("outcome").GetString(), askedFryer.GetProperty("request").GetProperty("summary").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/custody/assets/{fryer}", Cancel)).StatusCode);
        await ApproveAsync(manager, askedFryer);
        Assert.Equal("Fryer", (await AssetAsync(manager, fryer)).GetProperty("name").GetString());

        // Into Hassan's custody, all of it; on his profile since today.
        var handed = await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{plates}/check-in", In(hassan), Cancel));
        Assert.Equal(("CheckIn", 40, "Hassan"), (handed.GetProperty("voucher").GetProperty("type").GetString(), handed.GetProperty("voucher").GetProperty("quantity").GetInt32(),
            handed.GetProperty("voucher").GetProperty("employeeName").GetString()));
        Assert.Equal(("InCustody", "Hassan"), (handed.GetProperty("asset").GetProperty("status").GetString(), handed.GetProperty("asset").GetProperty("employeeName").GetString()));
        var custody = (await Json(await accountant.GetAsync($"/api/hr/employees/{hassan}/custody", Cancel))).EnumerateArray().Single();
        Assert.Equal(("Dinner plates", Iso(today)), (custody.GetProperty("asset").GetProperty("name").GetString(), custody.GetProperty("since").GetString()));
        Assert.Contains("in their custody already", await Problem(await manager.PostAsJsonAsync($"/api/custody/assets/{plates}/check-in", In(hassan), Cancel)));
        Assert.Contains("dated today or before",
            await Problem(await manager.PostAsJsonAsync($"/api/custody/assets/{fryer}/check-in", In(hassan, today.AddDays(1)), Cancel)));

        // Three broken: out of his custody, and into Costing → Waste at 105. Sent twice, made once.
        var brokenId = Guid.NewGuid();
        var broken = await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{plates}/check-out", Out("Breakage", 3, brokenId), Cancel));
        var voucher = broken.GetProperty("voucher");
        Assert.Equal(("Breakage", 3, 105m, true), (voucher.GetProperty("reason").GetString(), voucher.GetProperty("quantity").GetInt32(),
            voucher.GetProperty("cost").GetDecimal(), voucher.GetProperty("routedToWaste").GetBoolean()));
        Assert.Equal((37, "InCustody"), (broken.GetProperty("asset").GetProperty("quantity").GetInt32(), broken.GetProperty("asset").GetProperty("status").GetString()));
        await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{plates}/check-out", Out("Breakage", 3, brokenId), Cancel));
        Assert.Equal(37, (await AssetAsync(manager, plates)).GetProperty("quantity").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/costing/breakage", Cancel)).StatusCode);
        var waste = (await Json(await manager.GetAsync("/api/costing/breakage?days=7", Cancel))).EnumerateArray().Single();
        Assert.Equal(("Dinner plates", 3, 105m, "Hassan", brokenId), (waste.GetProperty("description").GetString(), waste.GetProperty("quantity").GetInt32(),
            waste.GetProperty("cost").GetDecimal(), waste.GetProperty("employeeName").GetString(), waste.GetProperty("assetTransactionId").GetGuid()));
        Assert.Equal(waste.GetProperty("id").GetGuid(), voucher.GetProperty("wasteEntryId").GetGuid());

        // A fixed asset broken is not the day's waste.
        await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{fryer}/check-in", In(hassan), Cancel));
        var fryerBroken = await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{fryer}/check-out", Out("Breakage", 1), Cancel));
        Assert.Equal((false, "BrokenWaste", 0), (fryerBroken.GetProperty("voucher").GetProperty("routedToWaste").GetBoolean(),
            fryerBroken.GetProperty("asset").GetProperty("status").GetString(), fryerBroken.GetProperty("asset").GetProperty("quantity").GetInt32()));
        Assert.Single((await Json(await manager.GetAsync("/api/costing/breakage?days=7", Cancel))).EnumerateArray());
        // In the month's statement, said apart: bought, it was counted as an expense already.
        var statement = (await Json(await manager.GetAsync($"/api/costing/kpis?year={today.Year}&month={today.Month}", Cancel))).GetProperty("statement");
        Assert.Equal(105m, statement.GetProperty("breakage").GetDecimal());

        // An accountant's check-out waits, and holds the asset until decided.
        var lost = await Json(await accountant.PostAsJsonAsync($"/api/custody/assets/{plates}/check-out", Out("Lost", 1), Cancel));
        Assert.Equal("Out of Hassan's custody, lost · Dinner plates × 1", lost.GetProperty("request").GetProperty("summary").GetString());
        Assert.Contains("can be changed once a manager has decided",
            await Problem(await manager.PostAsJsonAsync($"/api/custody/assets/{plates}/check-out", Out("Returned", 37), Cancel)));
        await ApproveAsync(manager, lost);
        Assert.Equal(36, (await AssetAsync(manager, plates)).GetProperty("quantity").GetInt32());

        // Handed back, all of it: in the store, and off his profile.
        Assert.Contains("All 36 of them go out together",
            await Problem(await manager.PostAsJsonAsync($"/api/custody/assets/{plates}/check-out", Out("Returned", 10), Cancel)));
        var back = await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{plates}/check-out", Out("Returned", 36), Cancel));
        Assert.Equal(("InStore", JsonValueKind.Null), (back.GetProperty("asset").GetProperty("status").GetString(), back.GetProperty("asset").GetProperty("employeeId").ValueKind));
        Assert.Empty((await Json(await accountant.GetAsync($"/api/hr/employees/{hassan}/custody", Cancel))).EnumerateArray());

        // How many there are changes by vouchers alone; the rest is changed as any record.
        Assert.Contains("changes by vouchers: there are 36",
            await Problem(await manager.PutAsJsonAsync($"/api/custody/assets/{plates}", Asset("Dinner plates", tableware, 35, 50), Cancel)));
        var renamed = await Json(await manager.PutAsJsonAsync($"/api/custody/assets/{plates}", Asset("Dinner plates, white", tableware, 36, 36), Cancel));
        Assert.Equal(("Dinner plates, white", 36m), (renamed.GetProperty("asset").GetProperty("name").GetString(), renamed.GetProperty("asset").GetProperty("unitCost").GetDecimal()));

        // The vouchers, the latest first; the asset's own.
        var vouchers = (await Json(await accountant.GetAsync("/api/custody/vouchers", Cancel))).EnumerateArray().ToList();
        Assert.Equal(6, vouchers.Count);
        Assert.Equal(("CheckOut", "Returned", "Dinner plates, white"),
            (vouchers[0].GetProperty("type").GetString(), vouchers[0].GetProperty("reason").GetString(), vouchers[0].GetProperty("assetName").GetString()));
        var detail = await Json(await manager.GetAsync($"/api/custody/assets/{plates}", Cancel));
        Assert.Equal(["Returned", "Lost", "Breakage", null], detail.GetProperty("vouchers").EnumerateArray().Select(v => v.GetProperty("reason").GetString()));

        // A category with assets in it stays; an empty one goes.
        Assert.Contains("Assets are in Tableware", await Problem(await manager.DeleteAsync($"/api/custody/categories/{tableware}", Cancel)));
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/custody/categories/{uniforms}", Cancel)).StatusCode);
        var categories = (await Json(await manager.GetAsync("/api/custody/categories", Cancel))).EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => (c.GetProperty("assets").GetInt32(), c.GetProperty("isConsumable").GetBoolean()));
        Assert.Equal(new Dictionary<string, (int, bool)> { ["Devices"] = (0, false), ["Tableware"] = (1, true) }, categories);
    }

    [Fact]
    public async Task Assets_bought_on_an_invoice_join_the_list_with_financials_and_go_with_it_if_it_is_voided_before_one_moves()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var today = await TodayAsync(manager);
        var tableware = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/custody/categories/{tableware}", new { name = "Tableware", isConsumable = true }, Cancel));
        var glasses = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/inventory/expense-items/{glasses}", new { name = "Water glasses" }, Cancel));
        Assert.Contains("not there any more",
            await Problem(await manager.PutAsJsonAsync($"/api/inventory/expense-items/{glasses}/asset-category", new { assetCategoryId = Guid.NewGuid() }, Cancel)));
        var marked = await Json(await manager.PutAsJsonAsync($"/api/inventory/expense-items/{glasses}/asset-category", new { assetCategoryId = tableware }, Cancel));
        Assert.Equal(tableware, marked.GetProperty("assetCategoryId").GetGuid());
        // The item's own form, which knows nothing of it, leaves it marked.
        await Json(await manager.PutAsJsonAsync($"/api/inventory/expense-items/{glasses}", new { name = "Water glasses" }, Cancel));

        async Task<Guid> BoughtAsync(decimal quantity, decimal cost)
        {
            var id = Guid.NewGuid();
            await Json(await manager.PutAsJsonAsync($"/api/inventory/purchase-invoices/{id}", new
            {
                supplierId = (Guid?)null, supplierInvoiceNumber = $"G-{quantity}", invoiceDate = Iso(today), paymentMethod = "Safe",
                lines = new object[] { new { itemType = "Expense", expenseItemId = glasses, enteredQuantity = quantity, unitSize = 1, cost } },
            }, Cancel));
            await Json(await manager.PostAsync($"/api/inventory/purchase-invoices/{id}/post", null, Cancel));
            return id;
        }
        async Task<List<JsonElement>> Linked() => (await Json(await manager.GetAsync("/api/custody/assets?withFinancial=true", Cancel))).EnumerateArray().ToList();
        Task<HttpResponseMessage> Void(Guid invoice) => manager.PostAsJsonAsync("/api/inventory/deletion-requests",
            new { requestId = Guid.NewGuid(), module = "PurchaseInvoice", recordId = invoice, reason = "Entered twice" }, Cancel);

        // 24 glasses for 480: an asset in the store at 20 each, shown with Link with Financials on.
        var first = await BoughtAsync(24, 480);
        Assert.Empty((await Json(await manager.GetAsync("/api/custody/assets", Cancel))).EnumerateArray());
        var bought = Assert.Single(await Linked());
        Assert.Equal(("Water glasses", 24, 20m, "FinancialPurchase", "InStore", "Tableware", first),
            (bought.GetProperty("name").GetString(), bought.GetProperty("quantity").GetInt32(), bought.GetProperty("unitCost").GetDecimal(),
                bought.GetProperty("origin").GetString(), bought.GetProperty("status").GetString(), bought.GetProperty("categoryName").GetString(),
                bought.GetProperty("sourceInvoiceId").GetGuid()));
        // Its cost is its invoice's.
        Assert.Contains("costs what its invoice says",
            await Problem(await manager.PutAsJsonAsync($"/api/custody/assets/{bought.GetProperty("id").GetGuid()}", Asset("Water glasses", tableware, 30, 24), Cancel)));

        // Voided before any moved: they go with it.
        Assert.Equal("Applied", (await Json(await Void(first))).GetProperty("outcome").GetString());
        Assert.Empty(await Linked());

        // Handed out, they keep the invoice from being voided.
        var second = await BoughtAsync(12, 300);
        var hassan = await HireAsync(manager, "Hassan", today.AddMonths(-1));
        await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{Assert.Single(await Linked()).GetProperty("id").GetGuid()}/check-in", In(hassan), Cancel));
        Assert.Contains("has been handed out in Custody & Assets", await Problem(await Void(second)));

        // A quantity that is not a whole number is one, at the line's cost.
        await BoughtAsync(2.5m, 50);
        var odd = (await Linked()).Single(a => a.GetProperty("quantity").GetInt32() == 1);
        Assert.Equal((50m, "2.5 unit on the invoice"), (odd.GetProperty("unitCost").GetDecimal(), odd.GetProperty("notes").GetString()));
    }

    [Fact]
    public async Task An_employee_signs_in_with_their_mobile_and_pin_and_sees_only_their_own_portal()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var accountant = await api.AccountantAsync(manager);
        var today = await TodayAsync(manager);
        var hassan = await HireAsync(manager, "Hassan", new DateOnly(today.Year, today.Month, 1).AddMonths(-2), "01012345678");
        var mona = await HireAsync(manager, "Mona", today.AddMonths(-2));

        // A manager's to set: digits, and a mobile to sign in with. Never shown again.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync($"/api/hr/employees/{hassan}/pin", new { pin = "2468" }, Cancel)).StatusCode);
        Assert.Contains("A PIN is 4 to 6 digits", await Problem(await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/pin", new { pin = "12" }, Cancel)));
        Assert.Contains("mobile number first", await Problem(await manager.PutAsJsonAsync($"/api/hr/employees/{mona}/pin", new { pin = "2468" }, Cancel)));
        var set = await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/pin", new { pin = "٢٤٦٨" }, Cancel));
        Assert.True(set.GetProperty("hasPin").GetBoolean());
        Assert.DoesNotContain("pinHash", await (await manager.GetAsync("/api/hr/employees", Cancel)).Content.ReadAsStringAsync(Cancel), StringComparison.OrdinalIgnoreCase);

        // Wrong: one answer, whether or not the number is anyone's.
        var anonymous = api.Anonymous();
        Task<HttpResponseMessage> Login(string mobile, string pin) =>
            anonymous.PostAsJsonAsync("/api/auth/employee-login", new { mobileNumber = mobile, pinCode = pin }, Cancel);
        Assert.Contains("Wrong mobile number or PIN.", await Problem(await Login("01012345678", "1111"), HttpStatusCode.Unauthorized));
        Assert.Contains("Wrong mobile number or PIN.", await Problem(await Login("01099999999", "2468"), HttpStatusCode.Unauthorized));
        var signedIn = await Json(await Login("010 1234 5678", "2468"));
        Assert.Equal(("Employee", "Hassan", hassan),
            (signedIn.GetProperty("role").GetString(), signedIn.GetProperty("name").GetString(), signedIn.GetProperty("employeeId").GetGuid()));
        var phone = api.Anonymous();
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.GetProperty("token").GetString());

        // Theirs: their details, their payslips, their custody.
        var me = await Json(await phone.GetAsync("/api/portal/me", Cancel));
        Assert.Equal(("Hassan", "01012345678", "General"), (me.GetProperty("name").GetString(), me.GetProperty("mobileNumber").GetString(), me.GetProperty("department").GetString()));
        var months = (await Json(await phone.GetAsync("/api/portal/payslip-months", Cancel))).EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Equal(Iso(new DateOnly(today.Year, today.Month, 1)), months[0]);
        Assert.Equal(3, months.Count);
        var slip = await Json(await phone.GetAsync("/api/portal/payslip", Cancel));
        Assert.Equal((hassan, months[0]), (slip.GetProperty("employee").GetProperty("id").GetGuid(), slip.GetProperty("month").GetString()));
        Assert.Equal(months[2], (await Json(await phone.GetAsync($"/api/portal/payslip?month={months[2]}", Cancel))).GetProperty("month").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/api/portal/payslip?month=2020-01-01", Cancel)).StatusCode);
        Assert.Empty((await Json(await phone.GetAsync("/api/portal/custody", Cancel))).EnumerateArray());
        // Their custody: what each thing is and how many, those of one name and category handed
        // over apart on one line, and nothing of what any of it cost.
        var tableware = Guid.NewGuid();
        await Json(await manager.PutAsJsonAsync($"/api/custody/categories/{tableware}", new { name = "Tableware", isConsumable = true }, Cancel));
        async Task HandOverAsync(string name, Guid? category, decimal cost, int quantity, Guid to)
        {
            var asset = Guid.NewGuid();
            await Json(await manager.PutAsJsonAsync($"/api/custody/assets/{asset}", Asset(name, category, cost, quantity), Cancel));
            await Json(await manager.PostAsJsonAsync($"/api/custody/assets/{asset}/check-in", In(to), Cancel));
        }
        await HandOverAsync("Dinner plates", tableware, 35, 10, hassan);
        await HandOverAsync("dinner plates", tableware, 40, 5, hassan);
        await HandOverAsync("Dinner plates", null, 35, 2, hassan);
        await HandOverAsync("Knife set", null, 1_250, 1, hassan);
        await HandOverAsync("Dinner plates", tableware, 35, 7, mona);
        var custody = await (await phone.GetAsync("/api/portal/custody", Cancel)).Content.ReadAsStringAsync(Cancel);
        var lines = JsonDocument.Parse(custody).RootElement.EnumerateArray().ToList();
        (string?, string?, int)[] expected = [("Dinner plates", null, 2), ("Dinner plates", "Tableware", 15), ("Knife set", null, 1)];
        Assert.Equal(expected, lines.Select(c => (c.GetProperty("name").GetString(), c.GetProperty("category").GetString(), c.GetProperty("quantity").GetInt32())));
        Assert.All(lines, c => Assert.Equal(["name", "category", "quantity"], c.EnumerateObject().Select(p => p.Name)));
        Assert.DoesNotContain("cost", custody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1250", custody);
        // A manager sees them with their costs, on the profile.
        Assert.Contains("totalCost", await (await manager.GetAsync($"/api/hr/employees/{hassan}/custody", Cancel)).Content.ReadAsStringAsync(Cancel));
        // Their ID card, once a manager has uploaded it.
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/api/portal/id-photo", Cancel)).StatusCode);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
        var card = new ByteArrayContent(jpeg);
        card.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        await Json(await manager.PutAsync($"/api/hr/employees/{hassan}/id-photo", new MultipartFormDataContent { { card, "file", "card.jpg" } }, Cancel));
        var photo = await phone.GetAsync("/api/portal/id-photo", Cancel);
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(jpeg, await photo.Content.ReadAsByteArrayAsync(Cancel));

        // Nothing of the till: its token holds no till role.
        foreach (var path in new[] { "/api/hr/employees", "/api/auth/me", "/api/custody/assets", "/api/financials/today", $"/api/hr/employees/{mona}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync(path, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PutAsJsonAsync($"/api/hr/employees/{hassan}/pin", new { pin = "1111" }, Cancel)).StatusCode);
        // And staff are not employees.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/portal/me", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/portal/me", Cancel)).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_portal_and_a_new_pin_or_leaving_signs_them_out()
    {
        await using var api = await TestApi.StartAsync();
        var manager = await api.ManagerAsync();
        var today = await TodayAsync(manager);
        var hassan = await HireAsync(manager, "Hassan", today.AddMonths(-1), "01012345678");
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/pin", new { pin = "2468" }, Cancel));
        var anonymous = api.Anonymous();
        Task<HttpResponseMessage> Login(string pin) =>
            anonymous.PostAsJsonAsync("/api/auth/employee-login", new { mobileNumber = "01012345678", pinCode = pin }, Cancel);
        HttpClient Phone(JsonElement signedIn)
        {
            var client = api.Anonymous();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.GetProperty("token").GetString());
            return client;
        }

        var before = Phone(await Json(await Login("2468")));
        for (var i = 0; i < 4; i++)
            Assert.Contains("Wrong mobile number or PIN.", await Problem(await Login("0000"), HttpStatusCode.Unauthorized));
        Assert.Contains("Too many wrong PINs. Try again after", await Problem(await Login("0000"), HttpStatusCode.Unauthorized));
        // Locked: even the right PIN waits.
        Assert.Contains("Too many wrong PINs", await Problem(await Login("2468"), HttpStatusCode.Unauthorized));

        // A manager's new PIN lets them in at once, and signs the old sign-in out.
        await Json(await manager.PutAsJsonAsync($"/api/hr/employees/{hassan}/pin", new { pin = "135790" }, Cancel));
        Assert.Equal(HttpStatusCode.Unauthorized, (await before.GetAsync("/api/portal/me", Cancel)).StatusCode);
        var after = Phone(await Json(await Login("135790")));
        Assert.Equal(HttpStatusCode.OK, (await after.GetAsync("/api/portal/me", Cancel)).StatusCode);

        // Leaving signs them out, and keeps them out.
        await Json(await manager.PostAsJsonAsync($"/api/hr/employees/{hassan}/leave", new { requestId = Guid.NewGuid(), lastDay = Iso(today) }, Cancel));
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/api/portal/me", Cancel)).StatusCode);
        Assert.Contains("Wrong mobile number or PIN.", await Problem(await Login("135790"), HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task The_portal_port_answers_only_the_portal()
    {
        // The test server takes every request on port 0: as if each came in on the portal's port.
        await using var api = await TestApi.StartAsync(new Dictionary<string, string> { ["Portal:Port"] = "0" });
        var phone = api.Anonymous();

        foreach (var path in new[] { "/api/auth/staff", "/api/financials/today", "/api/costing/breakage", "/health", "/iclock/cdata?SN=CQZ7232060112", "/hubs/till/negotiate" })
            Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync(path, Cancel)).StatusCode);
        // HR and Custody answer there, for the tabs a manager gave an employee: no one signed in, nothing.
        foreach (var path in new[] { "/api/hr/employees", "/api/custody/assets" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync(path, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsJsonAsync("/api/auth/sign-in", new { staffId = "x", pin = "1234" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsync("/my-profile", null, Cancel)).StatusCode);
        // Its address alone, or a screen of the till's, opens the portal's sign-in; the portal's own screens and the app's files open.
        var root = api.Anonymous(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var path in new[] { "/", "/costing/waste", "/staff", "/portalx" })
        {
            var opened = await root.GetAsync(path, Cancel);
            Assert.Equal((HttpStatusCode.Redirect, "/portal"), (opened.StatusCode, opened.Headers.Location?.OriginalString));
        }
        foreach (var path in new[] { "/portal", "/my-profile", "/hr/now", "/custody/assets", "/Portal?reason=expired", "/favicon.ico", "/main-ABC.js" })
            Assert.NotEqual(HttpStatusCode.Redirect, (await root.GetAsync(path, Cancel)).StatusCode);
        // Its sign-in and its API answer there.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await phone.PostAsJsonAsync("/api/auth/employee-login", new { mobileNumber = "01012345678", pinCode = "1234" }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/portal/me", Cancel)).StatusCode);
    }
}
