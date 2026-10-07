using MediatR;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Application.Recipes;
using OtantikPos.Inventory.Application.StockCounts;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Costing;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Node.Infrastructure.Identity;
using OtantikPos.Node.Infrastructure.Messaging;
using OtantikPos.Node.Infrastructure.Printing;
using OtantikPos.Ordering.Application.Orders;
using OtantikPos.Ordering.Application.Reports;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Node.Infrastructure.Tests;

// One class, so the tests run one after another: each starts a whole node with its own
// database, fake cloud and printers.
public class NodeTests
{
    private static Task<bool> MenuSynced(TestNode node) =>
        TestNode.WaitFor(() => node.Db(db => db.MenuItems.AnyAsync()));

    [Fact]
    public async Task The_menu_tax_rate_and_staff_arrive_from_the_delivery_system()
    {
        await using var node = await TestNode.StartAsync();

        Assert.True(await MenuSynced(node));
        var burger = await node.Db(db => db.MenuItems.Include(m => m.MenuItemAddOns).SingleAsync(m => m.Id == FakeDeliverySystem.Burger));
        Assert.Equal([FakeDeliverySystem.Cheese], burger.MenuItemAddOns.Select(a => a.AddOnId));
        Assert.Equal(14m, await node.Db(db => db.Settings.Select(s => s.TaxPercentage).SingleAsync()));
        // A delivery system that does not send the redemption rate yet: Points / 10.
        Assert.Equal(10m, await node.Db(db => db.Settings.Select(s => s.RedemptionValuePer100Points).SingleAsync()));

        using var scope = node.Scope();
        var staff = node.Service<StaffDirectory>(scope);
        Assert.Equal(["Omar", "Sara"], (await staff.GetTillStaffAsync(TestContext.Current.CancellationToken)).Select(s => s.FullName));

        // A PIN is the till's own; the next sync must not wipe it.
        await staff.SetPinAsync("staff-cashier", "2468", TestContext.Current.CancellationToken);
        // The next sync, after an admin changed the redemption rate in the Campaign Manager.
        await scope.ServiceProvider.GetRequiredService<ReferenceDataApplier>().ApplyAsync(
            FakeDeliverySystem.ReferenceData() with { RedemptionValuePer100Points = 12.5m }, DateTime.UtcNow, TestContext.Current.CancellationToken);
        Assert.NotNull((await staff.SignInAsync("staff-cashier", "2468", TestContext.Current.CancellationToken)).Staff);
        Assert.Equal(12.5m, await node.Db(db => db.Settings.Select(s => s.RedemptionValuePer100Points).SingleAsync()));
    }

    // The Recipe Costing Template's own example, Breakfast Strata for 15, on the burger: the card
    // comes to its figures. Its half-and-half has no price; the template counted it as nothing,
    // the card says so.
    [Fact]
    public async Task The_recipe_card_works_out_the_templates_breakfast_strata()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        async Task<Guid> Material(string name, UnitOfMeasure unit, decimal? cost) =>
            (await node.Send(new CreateRawMaterialCommand(name, unit, CostPerPurchaseUnit: cost,
                PurchaseUnit: unit == UnitOfMeasure.Piece ? "piece" : null, PurchaseUnitSize: unit == UnitOfMeasure.Piece ? 1 : null))).Id;
        var rolls = await Material("French rolls", UnitOfMeasure.Piece, 0.29m);
        var eggs = await Material("Eggs", UnitOfMeasure.Piece, 0.05m);
        var sausage = await Material("Sausage", UnitOfMeasure.Piece, 0.24m);
        var provolone = await Material("Provolone", UnitOfMeasure.Piece, 0.16m);
        var halfAndHalf = await Material("Half and half", UnitOfMeasure.Millilitre, null);
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null,
            [new(rolls, 2), new(eggs, 12, 98), new(sausage, 5.30m), new(provolone, 1), new(halfAndHalf, 473)], Portions: 15));

        using var scope = node.Scope();
        var card = await node.Service<CostingService>(scope)
            .GetRecipeCardAsync(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, TestContext.Current.CancellationToken);

        Assert.Equal(15, card.Portions);
        Assert.Equal(0.61m, card.Lines.Single(l => l.Ingredient == "Eggs").RecipeCost);
        Assert.Equal(1.27m, card.Lines.Single(l => l.Ingredient == "Sausage").RecipeCost);
        Assert.Equal(2.62m, card.CostPerRecipe);
        Assert.Equal(0.17m, card.CostPerPortion);
        Assert.Equal(0.58m, card.IdealSellingPrice);
        Assert.Equal(120m - 0.17m, card.MarginPerPortion);
        Assert.Equal(["Half and half"], card.MissingPrices);
        Assert.Equal(CostStatus.MissingPrices, card.Status);
    }

    // The Monthly Theoretical Cost report: each dish sold this month, its net sales, and what its
    // recipe says it cost, from the cost each sale recorded. A refund is not a sale; a dish with
    // no recipe is said to have none rather than shown at 0%; a shared cost is spread per meal.
    [Fact]
    public async Task The_monthly_theoretical_cost_comes_from_what_was_sold()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        async Task<Guid> Piece(string name, decimal cost) =>
            (await node.Send(new CreateRawMaterialCommand(name, UnitOfMeasure.Piece, PurchaseUnit: "piece", PurchaseUnitSize: 1, CostPerPurchaseUnit: cost))).Id;
        var patty = await Piece("Beef patty", 20);
        var bun = await Piece("Bun", 5);
        var cheese = await Piece("Cheese slice", 3);
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(patty, 1), new(bun, 1)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.AddOn, FakeDeliverySystem.Cheese, null, [new(cheese, 1)]));

        async Task<Order> Paid(Func<Order, Task> fill)
        {
            var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: Guid.NewGuid().ToString()[..6]));
            await fill(order);
            await node.Send(new SendToKitchenCommand(order.PublicId));
            return await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        }
        await Paid(o => node.Send(new AddOrderItemCommand(o.PublicId, FakeDeliverySystem.Burger, 2, AddOnIds: [FakeDeliverySystem.Cheese])));
        await Paid(async o =>
        {
            await node.Send(new AddOrderItemCommand(o.PublicId, FakeDeliverySystem.Burger, 1));
            await node.Send(new AddOrderItemCommand(o.PublicId, FakeDeliverySystem.Shawarma, VariantId: FakeDeliverySystem.KiloTray));
        });
        var refunded = await Paid(o => node.Send(new AddOrderItemCommand(o.PublicId, FakeDeliverySystem.Burger, 1)));
        await node.Send(new VoidOrderCommand(refunded.PublicId, "Wrong table"));

        // The sales' stock movements are posted by the outbox, just after each order closes.
        Assert.True(await TestNode.WaitFor(async () => await node.Db(db => db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.Sale)) == 7));

        using var scope = node.Scope();
        var costing = node.Service<CostingService>(scope);
        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);
        var report = await costing.GetTheoreticalCostAsync(today.Year, today.Month, ct);

        var burger = Assert.Single(report.Rows, r => r.MenuItemId == FakeDeliverySystem.Burger);
        Assert.Equal((3, 390m, 81m, 27m, 20.8m, CostStatus.WithinTarget),
            (burger.QuantitySold, burger.NetSales, burger.TheoreticalCost, burger.RecipeCostPerUnit, burger.FoodCostPercent, burger.Status));
        var tray = Assert.Single(report.Rows, r => r.VariantId == FakeDeliverySystem.KiloTray);
        Assert.Equal(($"{FakeDeliverySystem.Shawarma}-{FakeDeliverySystem.KiloTray}", 400m, CostStatus.NoRecipe), (tray.ItemCode, tray.NetSales, tray.Status));
        Assert.Equal((4, 790m, 81m, 10.3m), (report.QuantitySold, report.NetSales, report.TheoreticalCost, report.FoodCostPercent));
        Assert.Equal(0, burger.QuantityCostedNow);

        // The tray was sold before the shawarma had a recipe. Given one now, the tray is costed by
        // it as a sale today would be (a size without its own takes the dish's), at today's prices,
        // and the row says how many were costed so.
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Shawarma, null, [new(patty, 2)]));
        tray = Assert.Single((await costing.GetTheoreticalCostAsync(today.Year, today.Month, ct)).Rows, r => r.VariantId == FakeDeliverySystem.KiloTray);
        Assert.Equal((40m, 10m, CostStatus.WithinTarget, 1), (tray.TheoreticalCost, tray.FoodCostPercent, tray.Status, tray.QuantityCostedNow));

        // Frying oil, 400 a month across every meal: 100 a meal, which puts the burger above target.
        await costing.SaveSharedCostAsync(null, new SaveSharedCostRequest("Frying oil", null, 400, null), ct);
        burger = Assert.Single((await costing.GetTheoreticalCostAsync(today.Year, today.Month, ct)).Rows, r => r.MenuItemId == FakeDeliverySystem.Burger);
        Assert.Equal((300m, 381m, CostStatus.AboveTarget), (burger.SharedCost, burger.TheoreticalCost, burger.Status));
    }

    // Pillar 3 between two counts, after the templates' examples: fries short beyond what sales
    // and records explain (red), mozzarella over despite spoilage (blue), a patty short by one.
    [Fact]
    public async Task The_variance_report_sets_what_left_the_stores_against_what_the_recipes_took()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        async Task<Guid> Material(string name, UnitOfMeasure unit) => (await node.Send(new CreateRawMaterialCommand(name, unit))).Id;
        var fries = await Material("Farm frites", UnitOfMeasure.Gram);
        var cheese = await Material("Mozzarella", UnitOfMeasure.Gram);
        var patty = await Material("Beef patty", UnitOfMeasure.Piece);
        var bun = await Material("Bun", UnitOfMeasure.Piece);
        var lettuce = await Material("Lettuce", UnitOfMeasure.Gram);
        var oil = await Material("Frying oil", UnitOfMeasure.Millilitre);
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(patty, 1), new(bun, 1), new(fries, 150)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.AddOn, FakeDeliverySystem.Cheese, null, [new(cheese, 30)]));
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(),
            [new(fries, 10_000, 600), new(cheese, 5_000, 1_500), new(patty, 50, 1_000), new(bun, 50, 250), new(oil, 20_000, 1_900)]));

        // The opening count, in one request; it matches the records.
        var opening = Guid.NewGuid();
        await node.Send(new RecordStockCountCommand(opening, [new(fries, 10_000), new(cheese, 5_000), new(patty, 50), new(bun, 50), new(oil, 20_000)], "Omar"));

        // The week: more fries delivered, ten cheeseburgers and a tray with no recipe sold, cheese spoiled.
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(fries, 5_000, 300)]));
        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "V1"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 10, AddOnIds: [FakeDeliverySystem.Cheese]));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Shawarma, VariantId: FakeDeliverySystem.KiloTray));
        await node.Send(new SendToKitchenCommand(order.PublicId));
        await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        await node.Send(new RecordSpoilageCommand(Guid.NewGuid(), [new(cheese, 200, "Expired")], "Omar"));
        Assert.True(await TestNode.WaitFor(async () => await node.Db(db => db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.Sale)) == 4));

        // The closing count, filled in as a draft and posted; lettuce counted for the first time.
        var closing = Guid.NewGuid();
        await node.Send(new SaveStockCountCommand(closing,
            [new(fries, 12_000), new(cheese, 4_900), new(patty, 39), new(bun, 40), new(lettuce, 800), new(oil, 18_500)], "Omar"));
        await node.Send(new PostStockCountCommand(closing, "Omar"));

        using var scope = node.Scope();
        // Frying oil is in no recipe: a shared cost, with no standard to fall short of.
        await node.Service<CostingService>(scope).SaveSharedCostAsync(null, new SaveSharedCostRequest("Frying oil", oil, null, null), ct);
        var report = await node.Service<VarianceService>(scope).GetVarianceAsync(null, null, ct);
        Assert.Equal((opening, closing, 5m), (report.From.Id, report.To.Id, report.TolerancePercent));
        Assert.Equal(["Farm frites", "Mozzarella", "Beef patty", "Bun", "Frying oil"], report.Rows.Select(r => r.Material));
        Assert.Equal((1_500m, VarianceEvaluation.SharedCost), (report.Rows[4].ActualUsage, report.Rows[4].Evaluation));

        // Fries: 10 kg + 5 kg − 12 kg left = 3 kg used, against 1.5 kg by the recipes.
        var row = report.Rows[0];
        Assert.Equal((10_000m, 5_000m, 1_500m, 13_500m, 12_000m, 3_000m, 1_500m, 100m, 1_500m, 0.06m, 90m, VarianceEvaluation.Unfavourable),
            (row.Opening, row.Received, row.StandardUsage, row.ExpectedClosing, row.Closing, row.ActualUsage, row.VarianceQuantity,
                row.VariancePercent, row.UnexplainedQuantity, row.UnitCost, row.VarianceValue, row.Evaluation));

        // Mozzarella: 300 g by the recipes, 200 g recorded spoiled, yet only 100 g gone.
        row = report.Rows[1];
        Assert.Equal((300m, 200m, 4_500m, 100m, -200m, -66.7m, -400m, -60m, VarianceEvaluation.Favourable),
            (row.StandardUsage, row.RawWaste, row.ExpectedClosing, row.ActualUsage, row.VarianceQuantity, row.VariancePercent,
                row.UnexplainedQuantity, row.VarianceValue, row.Evaluation));

        Assert.Equal((1m, 10m, 20m, VarianceEvaluation.Unfavourable),
            (report.Rows[2].VarianceQuantity, report.Rows[2].VariancePercent, report.Rows[2].VarianceValue, report.Rows[2].Evaluation));
        Assert.Equal(VarianceEvaluation.WithinLimit, report.Rows[3].Evaluation);

        Assert.Equal((2, 50m, 60m, -10m, "Farm frites", 100m),
            (report.UnfavourableCount, report.NetVarianceValue, report.RecordedWasteValue, report.UnexplainedValue,
                report.LargestRelativeMaterial, report.LargestRelativePercent));
        Assert.Equal(["Lettuce"], report.NotInBothCounts);
        var tray = Assert.Single(report.SoldWithoutStock);
        Assert.Equal(($"{FakeDeliverySystem.Shawarma}-{FakeDeliverySystem.KiloTray}", 1), (tray.ItemCode, tray.Quantity));

        var spoiled = Assert.Single(await node.Service<VarianceService>(scope).GetSpoilageAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1), ct));
        Assert.Equal(("Omar", 60m, "Expired"), (spoiled.RecordedBy, spoiled.Value, Assert.Single(spoiled.Lines).Reason));
    }

    [Fact]
    public async Task A_count_is_filled_in_as_one_draft_at_a_time_and_posted_once()
    {
        await using var node = await TestNode.StartAsync();
        var oil = (await node.Send(new CreateRawMaterialCommand("Frying oil", UnitOfMeasure.Millilitre))).Id;
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(oil, 20_000)]));

        var draft = Guid.NewGuid();
        var saved = await node.Send(new SaveStockCountCommand(draft, [new(oil, 15_000)], "Omar"));
        Assert.Equal((StockCountStatus.Draft, "Omar"), (saved.Status, saved.StartedBy));
        // A draft does not show what the records expect: the counter counts the shelf.
        Assert.Null(Assert.Single(saved.Lines).BookQuantity);

        // A second count while this one is open is refused, naming it.
        var second = await Assert.ThrowsAsync<DomainException>(() => node.Send(new SaveStockCountCommand(Guid.NewGuid(), [], "Sara")));
        Assert.Contains("Omar started a count", second.Message);

        // Saved again from another tablet, then posted twice, as a lost answer would make it.
        await node.Send(new SaveStockCountCommand(draft, [new(oil, 16_000)], "Omar"));
        var posted = await node.Send(new PostStockCountCommand(draft, "Omar"));
        var again = await node.Send(new PostStockCountCommand(draft, "Omar"));
        Assert.Equal((StockCountStatus.Posted, 20_000m, posted.PostedAtUtc), (again.Status, Assert.Single(again.Lines).BookQuantity, again.PostedAtUtc));
        Assert.Equal(16_000m, await node.Db(db => db.RawMaterials.Where(m => m.Id == oil).Select(m => m.QuantityOnHand).SingleAsync()));
        Assert.Equal(1, await node.Db(db => db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.CountAdjustment)));

        await Assert.ThrowsAsync<DomainException>(() => node.Send(new DiscardStockCountCommand(draft)));

        // With nothing open, a new draft can start, and be discarded.
        var next = Guid.NewGuid();
        await node.Send(new SaveStockCountCommand(next, [new(oil, 1)], "Sara"));
        await node.Send(new DiscardStockCountCommand(next));
        Assert.Equal([draft], (await node.Send(new GetStockCountsQuery())).Select(c => c.Id));
    }

    // Pillar 4, the template's KPIs for a month: food and drinks split by category, losses from
    // the ledger counted against food, wages and overheads as the manager enters them.
    [Fact]
    public async Task The_months_kpis_come_from_sales_recipes_losses_and_what_the_manager_enters()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        async Task<Guid> Piece(string name, decimal cost) =>
            (await node.Send(new CreateRawMaterialCommand(name, UnitOfMeasure.Piece, PurchaseUnit: "piece", PurchaseUnitSize: 1, CostPerPurchaseUnit: cost))).Id;
        var patty = await Piece("Beef patty", 20);
        var bun = await Piece("Bun", 5);
        var lemon = await Piece("Lemon", 6);
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(patty, 50), new(bun, 50), new(lemon, 50)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(patty, 1), new(bun, 1)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Lemonade, null, [new(lemon, 1)]));

        // Two burgers and two lemonades; then two burgers sent, one voided after the kitchen made it.
        var first = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "K1"));
        await node.Send(new AddOrderItemCommand(first.PublicId, FakeDeliverySystem.Burger, 2));
        await node.Send(new AddOrderItemCommand(first.PublicId, FakeDeliverySystem.Lemonade, 2));
        await node.Send(new SendToKitchenCommand(first.PublicId));
        await node.Send(new CheckoutCommand(first.PublicId, PaymentMethod.Cash));
        var second = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "K2"));
        second = await node.Send(new AddOrderItemCommand(second.PublicId, FakeDeliverySystem.Burger, 2));
        await node.Send(new SendToKitchenCommand(second.PublicId));
        await node.Send(new VoidOrderItemCommand(second.PublicId, second.OrderItems.Single().PublicId, 1, "Dropped"));
        await node.Send(new CheckoutCommand(second.PublicId, PaymentMethod.Cash));
        await node.Send(new RecordSpoilageCommand(Guid.NewGuid(), [new(bun, 2, "Stale")], "Omar"));
        Assert.True(await TestNode.WaitFor(async () => await node.Db(db => db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.Sale)) == 5));

        using var scope = node.Scope();
        await node.Service<CostingService>(scope).SaveSettingsAsync(new CostingSettingsDto(30, BeverageCategories: ["Drinks"]), ct);
        var kpis = node.Service<KpiService>(scope);
        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);

        // Before the month's wages are in, what needs them says so rather than showing 0.
        var report = await kpis.GetAsync(today.Year, today.Month, ct);
        var labour = Assert.Single(report.Kpis, k => k.Key == "LabourCost");
        Assert.Equal((KpiStatus.Missing, (decimal?)null), (labour.Status, labour.Value));
        Assert.Null(report.Statement.NetProfit);

        await kpis.SaveExpensesAsync(today.Year, today.Month, new MonthlyExpensesDto(120, 10, 20, 50, 5, 5), "Omar", ct);
        report = await kpis.GetAsync(today.Year, today.Month, ct);

        // Food 3 × 120, drinks 2 × 30. Recipes: 3 burgers × 25, 2 lemonades × 6. Losses: the voided
        // burger (25) and two stale buns (10).
        var s = report.Statement;
        Assert.Equal((360m, 60m, 420m, 2, 75m, 12m, 25m, 10m, 0m, 122m, 298m, 98m),
            (s.FoodSales, s.BeverageSales, s.Revenue, s.PaidOrders, s.FoodRecipeCost, s.BeverageRecipeCost,
                s.KitchenWaste, s.Spoilage, s.CountDifferences, s.CostOfSales, s.GrossProfit, s.NetProfit));

        var byKey = report.Kpis.ToDictionary(k => k.Key);
        Assert.Equal((30.6m, KpiStatus.Healthy), (byKey["FoodCost"].Value, byKey["FoodCost"].Status));
        Assert.Equal((20m, KpiStatus.Healthy), (byKey["BeverageCost"].Value, byKey["BeverageCost"].Status));
        Assert.Equal((28.6m, KpiStatus.Healthy), (byKey["LabourCost"].Value, byKey["LabourCost"].Status));
        Assert.Equal((210m, KpiStatus.NoRange), (byKey["AverageCheck"].Value, byKey["AverageCheck"].Status));
        Assert.Equal(42m, byKey["SalesPerLabourHour"].Value);
        Assert.Equal((71m, KpiStatus.Above), (byKey["GrossProfitMargin"].Value, byKey["GrossProfitMargin"].Status));
        Assert.Equal((23.3m, KpiStatus.Above), (byKey["NetProfitMargin"].Value, byKey["NetProfitMargin"].Status));

        // Item profitability: selling price − ingredient cost, the most profitable first.
        Assert.Equal([("Burger", 285m, 95m, 79.2m), ("Lemonade", 48m, 24m, 80m)],
            report.Items.Select(i => (i.MenuItem, i.Profit!.Value, i.ProfitPerUnit!.Value, i.MarginPercent!.Value)));
        Assert.True(report.Items.Single(i => i.MenuItem == "Lemonade").Beverage);

        // A shared cost for every meal, 60 a month: the three burgers carry it, not the lemonades.
        await node.Service<CostingService>(scope).SaveSharedCostAsync(null, new SaveSharedCostRequest("Gas", null, 60, null), ct);
        s = (await kpis.GetAsync(today.Year, today.Month, ct)).Statement;
        Assert.Equal((135m, 12m), (s.FoodRecipeCost, s.BeverageRecipeCost));
    }

    // The break-even worksheet: the month's sales at a year's pace, cost of sales at its ratio, the
    // month's costs × 12 as fixed costs, and the sales at which profit is nil.
    [Fact]
    public async Task The_break_even_worksheet_puts_the_months_at_a_years_pace()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        async Task<Guid> Piece(string name, decimal cost) =>
            (await node.Send(new CreateRawMaterialCommand(name, UnitOfMeasure.Piece, PurchaseUnit: "piece", PurchaseUnitSize: 1, CostPerPurchaseUnit: cost))).Id;
        var patty = await Piece("Beef patty", 20);
        var bun = await Piece("Bun", 5);
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(patty, 50), new(bun, 50)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(patty, 1), new(bun, 1)]));

        // Four burgers at 120, costing 25 each: 480 of sales, 100 of cost.
        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "B1"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 4));
        await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        Assert.True(await TestNode.WaitFor(async () => await node.Db(db => db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.Sale)) == 2));

        using var scope = node.Scope();
        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);
        await node.Service<KpiService>(scope).SaveExpensesAsync(today.Year, today.Month, new MonthlyExpensesDto(1_000, 160, 200, 500, 50, 100), "Omar", ct);
        var breakEven = node.Service<BreakEvenService>(scope);

        var sheet = await breakEven.GetAsync(today.Year, today.Month, today.Year, today.Month, ct);
        Assert.Equal(today.Day, sheet.Days);
        Assert.Equal(Math.Round(480m / today.Day * 7, 2, MidpointRounding.AwayFromZero), sheet.WeeklySales);
        // Wages and other controllable costs are the controllable costs; each line × 12.
        Assert.Equal((14_400m, 6_000m, 600m, 1_200m, 22_200m),
            (sheet.ControllableCosts, sheet.OccupationCost, sheet.Interest, sheet.Depreciation, sheet.TotalFixedCosts));
        // 22,200 ÷ (1 − 100/480): each L.E of sales leaves 0.79 towards the fixed costs.
        Assert.Equal((28_042.11m, 539.27m), (sheet.BreakEvenYearlySales, sheet.BreakEvenWeeklySales));
        // Each figure is rounded on its own, so the profit can differ from the sum by a piastre or two.
        Assert.InRange(sheet.RestaurantProfit - (sheet.GrossSales - sheet.CostOfSales - sheet.TotalFixedCosts), -0.02m, 0.02m);

        // Last month too, which has no costs entered: named, and not counted as a month of nothing.
        var last = today.AddMonths(-1);
        sheet = await breakEven.GetAsync(last.Year, last.Month, today.Year, today.Month, ct);
        Assert.Equal([last.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)], sheet.MonthsWithoutCosts);
        Assert.Equal(22_200m, sheet.TotalFixedCosts);

        await Assert.ThrowsAsync<DomainException>(() => breakEven.GetAsync(today.Year + 1, 1, today.Year + 1, 1, ct));
        await breakEven.SaveScenariosAsync([32_000m, 40_000m, 20_000m, 15_000m], ct);
        Assert.Equal([32_000m, 40_000m, 20_000m, 15_000m], (await breakEven.GetAsync(today.Year, today.Month, today.Year, today.Month, ct)).Scenarios);
    }

    // A delivery, spoilage or count entered by hand can collide with the outbox taking a sale's
    // stock from the same material. Those are run again rather than refused; anything else still
    // reports the conflict.
    [Fact]
    public async Task Stock_entered_by_hand_is_saved_on_a_retry_when_it_collides_with_a_sale()
    {
        await using var node = await TestNode.StartAsync();
        using var scope = node.Scope();
        var ct = TestContext.Current.CancellationToken;
        var collision = () => new ConflictException("Someone else changed this at the same time.");

        var purchase = Assert.Single(node.Service<IEnumerable<IPipelineBehavior<ReceivePurchaseCommand, IReadOnlyList<RawMaterialDto>>>>(scope));
        var calls = 0;
        await purchase.Handle(new ReceivePurchaseCommand(Guid.NewGuid(), []),
            _ => ++calls == 1 ? throw collision() : Task.FromResult<IReadOnlyList<RawMaterialDto>>([]), ct);
        Assert.Equal(2, calls);

        // Three collisions in a row: given up, and reported.
        calls = 0;
        await Assert.ThrowsAsync<ConflictException>(() => purchase.Handle(new ReceivePurchaseCommand(Guid.NewGuid(), []),
            _ => { calls++; throw collision(); }, ct));
        Assert.Equal(3, calls);

        // A draft count saved from two tablets is not retried: the second should see the first.
        var draft = Assert.Single(node.Service<IEnumerable<IPipelineBehavior<SaveStockCountCommand, StockCountDto>>>(scope));
        calls = 0;
        await Assert.ThrowsAsync<ConflictException>(() => draft.Handle(new SaveStockCountCommand(Guid.NewGuid(), []),
            _ => { calls++; throw collision(); }, ct));
        Assert.Equal(1, calls);
    }

    // INSTALL.md, step 4: once a real manager has a PIN, the first manager's PIN comes out of .env
    // and the till restarts. The account it made must stop working then; its PIN was typed into a
    // settings file at setup.
    [Fact]
    public async Task The_first_manager_is_retired_once_its_pin_is_out_of_the_settings()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        using var scope = node.Scope();
        var staff = node.Service<StaffDirectory>(scope);
        var ct = TestContext.Current.CancellationToken;

        // The first start: nobody has a PIN, so the settings make a manager.
        await staff.EnsureBootstrapManagerAsync("Manager", "1234", ct);
        var first = Assert.Single(await staff.GetSignInListAsync(ct));
        Assert.True(first.IsLocalOnly);

        // Its PIN out of the settings, but it is the only manager who can sign in: kept.
        await staff.SetPinAsync("staff-cashier", "2468", ct);
        await staff.EnsureBootstrapManagerAsync("Manager", null, ct);
        Assert.NotNull((await staff.SignInAsync(first.Id, "1234", ct)).Staff);

        // Omar, from the delivery system, has a PIN: with the PIN still in the settings, kept...
        await staff.SetPinAsync("staff-manager", "1357", ct);
        await staff.EnsureBootstrapManagerAsync("Manager", "1234", ct);
        Assert.Contains(await staff.GetSignInListAsync(ct), s => s.Id == first.Id);

        // ...and once it is out of them, retired.
        await staff.EnsureBootstrapManagerAsync("Manager", null, ct);
        Assert.DoesNotContain(await staff.GetSignInListAsync(ct), s => s.Id == first.Id);
        Assert.Null((await staff.SignInAsync(first.Id, "1234", ct)).Staff);
        Assert.Null(await staff.GetActiveRoleAsync(first.Id, ct));
    }

    // The whole till path on real infrastructure: a dine-in order taken, printed in the
    // kitchen over TCP, paid, its stock deducted through the recipes by the outbox, a receipt
    // printed, and the order pushed to the delivery system as the shared model's JSON.
    [Fact]
    public async Task A_dine_in_order_prints_settles_deducts_stock_and_reaches_the_cloud()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));

        var beef = await node.Send(new CreateRawMaterialCommand("Beef patty", UnitOfMeasure.Piece));
        var chicken = await node.Send(new CreateRawMaterialCommand("Chicken", UnitOfMeasure.Gram));
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(beef.Id, 50), new(chicken.Id, 5000)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(beef.Id, 1)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Shawarma, FakeDeliverySystem.KiloTray, [new(chicken.Id, 1000)]));

        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "4"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 2, AddOnIds: [FakeDeliverySystem.Cheese], Notes: "no onions"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Shawarma, VariantId: FakeDeliverySystem.KiloTray));
        await node.Send(new SendToKitchenCommand(order.PublicId));

        Assert.True(await TestNode.WaitFor(() => Task.FromResult(!node.Kitchen.Printed.IsEmpty)));
        var ticket = node.Kitchen.Printed.Single();
        Assert.Contains("Table 4", ticket);
        Assert.Contains("2 x Burger", ticket);
        Assert.Contains("+ Cheese", ticket);
        Assert.Contains("> no onions", ticket);
        Assert.Contains("1 x Shawarma (Kilo tray)", ticket);

        order = await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        Assert.Equal(OrderStatus.Served, order.Status);
        Assert.Equal(763.80m, order.TotalAmount);   // (270 + 400) + 14% tax

        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.RawMaterials.AnyAsync(m => m.Id == beef.Id && m.QuantityOnHand == 48))));
        Assert.Equal(4000m, await node.Db(db => db.RawMaterials.Where(m => m.Id == chicken.Id).Select(m => m.QuantityOnHand).SingleAsync()));

        await node.Send(new PrintFinalReceiptCommand(order.PublicId));
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(!node.Receipts.Printed.IsEmpty)));
        Assert.Contains("TOTAL", node.Receipts.Printed.Single());
        Assert.Contains("763.80", node.Receipts.Printed.Single());

        // The cloud holds the till's latest state, as the shared Order's own JSON.
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(
            node.Cloud.PushedOrders.TryGetValue(order.PublicId, out var json) && json.Contains("\"closedAt\":\"20"))));
        var pushed = JsonSerializer.Deserialize<Order>(node.Cloud.PushedOrders[order.PublicId], DeliverySystemApi.Json)!;
        Assert.Equal(order.PublicId, pushed.PublicId);
        Assert.Equal(OrderType.DineIn, pushed.Type);
        Assert.Equal(2, pushed.OrderItems.Count);
        Assert.DoesNotContain("\"user\"", node.Cloud.PushedOrders[order.PublicId]);
    }

    // A kitchen printer switched off, out of paper or unplugged showed nothing at the till: the
    // cashier saw "sent to the kitchen" while the tickets piled up. Now the tills are told, with
    // how many are waiting, and told again when it prints.
    [Fact]
    public async Task The_tills_are_told_when_a_printer_is_not_printing()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        using var scope = node.Scope();
        var printers = node.Service<PrinterStatus>(scope);
        node.Kitchen.SwitchOff();

        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "6"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 1));
        await node.Send(new SendToKitchenCommand(order.PublicId));

        Assert.True(await TestNode.WaitFor(() => Task.FromResult(printers.Current.Any(p => p.Printer == "Kitchen" && p.Waiting == 1))));
        Assert.Empty(node.Kitchen.Printed);

        node.Kitchen.SwitchOn();
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(printers.Current.Count == 0), seconds: 45));
        Assert.Contains("Table 6", Assert.Single(node.Kitchen.Printed));
    }

    // Sent messages and printed tickets are cleared after a week; kept for good they grew every
    // backup by several hundred MB a year. What is still waiting is never cleared, however old.
    [Fact]
    public async Task Old_sent_messages_and_printed_tickets_are_cleared_and_waiting_ones_kept()
    {
        await using var node = await TestNode.StartAsync();
        var now = DateTime.UtcNow;
        var old = now - Housekeeping.KeepFor - TimeSpan.FromHours(1);
        var recent = now - TimeSpan.FromDays(1);
        OutboxMessage Message(DateTime occurred, DateTime? sent) =>
            new() { Id = Guid.NewGuid(), Type = "Test", Payload = "{}", OccurredAtUtc = occurred, ProcessedAtUtc = sent };
        PrintJob Ticket(DateTime created, DateTime? printed) =>
            new() { Id = Guid.NewGuid(), Printer = "Nowhere", Content = [1], CreatedAtUtc = created, PrintedAtUtc = printed };

        var oldSent = Message(old, old);
        var recentSent = Message(recent, recent);
        var oldWaiting = Message(old, null);
        var oldPrinted = Ticket(old, old);
        var recentPrinted = Ticket(recent, recent);
        var oldUnprinted = Ticket(old, null);
        await node.Db(async db =>
        {
            db.OutboxMessages.AddRange(oldSent, recentSent, oldWaiting);
            db.PrintJobs.AddRange(oldPrinted, recentPrinted, oldUnprinted);
            return await db.SaveChangesAsync();
        });

        await node.Db(db => Housekeeping.ClearAsync(db, now, TestContext.Current.CancellationToken));

        var messages = await node.Db(db => db.OutboxMessages.Where(m => m.Type == "Test").Select(m => m.Id).ToListAsync());
        Assert.Equal(new[] { recentSent.Id, oldWaiting.Id }.Order(), messages.Order());
        var tickets = await node.Db(db => db.PrintJobs.Where(j => j.Printer == "Nowhere").Select(j => j.Id).ToListAsync());
        Assert.Equal(new[] { recentPrinted.Id, oldUnprinted.Id }.Order(), tickets.Order());
    }

    // A tablet with an Arabic keyboard types ١٢ for table 12. The printer's code page has no
    // Arabic, so the kitchen got "Table ?"; the digits are the same numbers as 0-9.
    [Fact]
    public async Task A_table_number_typed_in_arabic_digits_prints_as_digits()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));

        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "١٢"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 1));
        await node.Send(new SendToKitchenCommand(order.PublicId));

        Assert.True(await TestNode.WaitFor(() => Task.FromResult(!node.Kitchen.Printed.IsEmpty)));
        Assert.Contains("Table 12", node.Kitchen.Printed.Single());
    }

    // The cash the customer handed over, and their change, on the receipt; a reprint too.
    [Fact]
    public async Task A_cash_receipt_shows_the_cash_received_and_the_change()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));

        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "8"));
        order = await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 1));
        order = await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash, order.TotalAmount, CashReceived: 200));
        await node.Send(new PrintFinalReceiptCommand(order.PublicId));
        await node.Send(new PrintFinalReceiptCommand(order.PublicId));

        Assert.True(await TestNode.WaitFor(() => Task.FromResult(node.Receipts.Printed.Count == 2)));
        Assert.All(node.Receipts.Printed, receipt =>
        {
            Assert.Contains("Cash received", receipt);
            Assert.Contains("200.00", receipt);
            Assert.Contains("Change", receipt);
            Assert.Contains((200 - order.TotalAmount).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), receipt);
        });
    }

    // The cashier chose Card; Visa is only the name the payment is stored under.
    [Fact]
    public async Task A_card_payment_reads_card_on_the_receipt()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));

        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "3"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 1));
        await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Visa));
        await node.Send(new PrintFinalReceiptCommand(order.PublicId));

        Assert.True(await TestNode.WaitFor(() => Task.FromResult(!node.Receipts.Printed.IsEmpty)));
        Assert.Contains("Paid (Card)", node.Receipts.Printed.Single());
    }

    // A captain's order taken in the delivery app reaches the till over SignalR, lands as the
    // shared Order, and prints in the kitchen. It is not pushed back to the cloud it came from.
    [Fact]
    public async Task A_captains_order_arrives_over_signalr_and_prints()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(node.Cloud.ConnectedTills > 0)));

        var captainOrder = new Order
        {
            Id = 7777,
            Type = OrderType.DineIn,
            TableNumber = "9",
            CreatedByUserId = "staff-captain",
            PaymentStatus = PaymentStatus.Pending,
            OrderItems = [new OrderItem { MenuItemId = FakeDeliverySystem.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = 1, SentToKitchenAt = DateTime.UtcNow }],
        };
        await node.Cloud.BroadcastAsync(captainOrder);

        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.Orders.AnyAsync(o => o.PublicId == captainOrder.PublicId))));
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(node.Kitchen.Printed.Any(t => t.Contains("Table 9")))));
        Assert.Contains((await node.Send(new GetOpenOrdersQuery())), o => o.PublicId == captainOrder.PublicId);

        // The same message again, as a reconnect's catch-up would bring it: no second ticket.
        await node.Cloud.BroadcastAsync(captainOrder);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Single(node.Kitchen.Printed, t => t.Contains("Table 9"));
        Assert.False(node.Cloud.PushedOrders.ContainsKey(captainOrder.PublicId));
    }

    [Fact]
    public async Task Orders_changed_while_the_till_was_away_arrive_by_catch_up()
    {
        var missed = new Order
        {
            Type = OrderType.DineIn, TableNumber = "2", CreatedByUserId = "staff-captain", PaymentStatus = PaymentStatus.Pending,
            UpdatedAt = DateTime.UtcNow,
            OrderItems = [new OrderItem { MenuItemId = FakeDeliverySystem.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = 3 }],
        };

        // In the cloud before the till ever connects, and never broadcast: the only way in is
        // the catch-up the listener runs when it connects.
        await using var node = await TestNode.StartAsync(cloud => cloud.ChangedOrders.Add(missed));

        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.Orders.AnyAsync(o => o.PublicId == missed.PublicId))));
        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.SyncState.AnyAsync(s => s.Key == Persistence.SyncState.OrdersChangedSince))));
    }

    // After an outage the menu, tax rate and staff catch up as soon as the delivery system is back,
    // not at the next scheduled sync, five minutes away here.
    [Fact]
    public async Task The_menu_syncs_as_soon_as_the_delivery_system_is_back()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(node.Cloud.ConnectedTills > 0)));

        // Down, and back with a dish added while the till could not see it.
        var data = FakeDeliverySystem.ReferenceData();
        var falafel = new MenuItem { Id = 20, Name = "Falafel", Price = 45, SubCategoryId = 11 };
        await node.RestartCloudAsync(cloud => cloud.Reference = data with { MenuItems = [.. data.MenuItems, falafel] });

        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.MenuItems.AnyAsync(m => m.Name == "Falafel")), 20));
    }

    // Requirement: takeaway and delivery find the customer by mobile number in the delivery
    // system, and points (Discount = Points / 10) are spent there, once.
    [Fact]
    public async Task Customer_lookup_and_points_go_through_the_delivery_system()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        node.Cloud.Customers["01001234567"] = new CustomerProfile("customer-1", "Ali Hassan", "01001234567", 500);
        node.Cloud.Balances["customer-1"] = 500;

        var order = await node.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "01001234567"));
        Assert.Equal("customer-1", order.UserId);

        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger));
        order = await node.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 100));
        Assert.Equal(10m, order.PointsDiscountAmount);

        order = await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Visa));
        Assert.Equal(400, node.Cloud.Balances["customer-1"]);
        Assert.Equal(100, node.Cloud.Redemptions[order.PublicId]);

        // Spent elsewhere meanwhile: the cloud says 409, the till says why, the bill stays open.
        var second = await node.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "01001234567"));
        await node.Send(new AddOrderItemCommand(second.PublicId, FakeDeliverySystem.Burger));
        await node.Send(new ApplyLoyaltyPointsCommand(second.PublicId, 300));
        node.Cloud.Balances["customer-1"] = 0;
        var refused = await Assert.ThrowsAsync<ConflictException>(() => node.Send(new CheckoutCommand(second.PublicId, PaymentMethod.Cash)));
        Assert.Equal("The customer no longer has enough points.", refused.Message);
        Assert.Null(await node.Db(db => db.Orders.Where(o => o.PublicId == second.PublicId).Select(o => o.ClosedAt).SingleAsync()));
    }

    // Decision: a Void After settles the customer's wallet in the delivery system through the
    // outbox. Made while the cloud is down, it waits there, and lands once the cloud is back.
    [Fact]
    public async Task A_refund_made_offline_settles_the_wallet_when_the_cloud_is_back()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        node.Cloud.Customers["01001234567"] = new CustomerProfile("customer-1", "Ali Hassan", "01001234567", 500);
        node.Cloud.Balances["customer-1"] = 500;

        var order = await node.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "01001234567"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 2));
        await node.Send(new ApplyLoyaltyPointsCommand(order.PublicId, 100));
        order = await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        Assert.Equal(400, node.Cloud.Balances["customer-1"]);

        node.Cloud.Down = true;
        order = await node.Send(new VoidOrderCommand(order.PublicId, "complaint"));
        Assert.Equal(100, order.PointsRefunded);
        var refundType = typeof(OtantikPos.Ordering.Contracts.LoyaltyRefundDue).FullName;
        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.OutboxMessages.AnyAsync(m => m.Type == refundType && m.LastError != null)), 30));
        Assert.Empty(node.Cloud.Refunds);

        node.Cloud.Down = false;
        await node.Db(db => db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, (DateTime?)null)));
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(!node.Cloud.Refunds.IsEmpty), 30));
        var refund = Assert.Single(node.Cloud.Refunds.Values);
        Assert.Equal((order.PublicId, 100, order.TotalAmount), (refund.OrderPublicId, refund.Points, refund.RefundedAmount));
        Assert.Equal(500, node.Cloud.Balances["customer-1"]);
    }

    // Offline: the till keeps selling. The customer lookup degrades to a walk-in, and the push
    // to the cloud waits in the outbox until the cloud is back.
    [Fact]
    public async Task With_the_cloud_down_the_till_keeps_selling_and_catches_up_later()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        node.Cloud.Down = true;

        var order = await node.Send(new OpenOrderCommand(OrderType.Takeaway, CustomerPhone: "01009999999"));
        Assert.Null(order.UserId);
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger));
        order = await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        Assert.True(OrderRules.IsSettled(order));

        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.OutboxMessages.AnyAsync(m => m.ProcessedAtUtc == null && m.LastError != null))));
        Assert.False(node.Cloud.PushedOrders.ContainsKey(order.PublicId));

        node.Cloud.Down = false;
        // What the listener does on reconnect: let the waiting pushes go now.
        await node.Db(db => db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, (DateTime?)null)));
        Assert.True(await TestNode.WaitFor(() => Task.FromResult(node.Cloud.PushedOrders.ContainsKey(order.PublicId)), 30));
    }

    // A test or mistaken order is soft-deleted (Order.IsDeleted), as in the delivery system, and
    // leaves the floor and the day's report; it is still there when fetched by its id.
    [Fact]
    public async Task A_soft_deleted_order_leaves_the_floor_and_the_report()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var kept = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "1"));
        var test = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "99"));

        await node.Db(db => db.Orders.Where(o => o.PublicId == test.PublicId).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true)));

        Assert.Equal([kept.PublicId], (await node.Send(new GetOpenOrdersQuery())).Select(o => o.PublicId));
        var report = await node.Send(new GetDayReportQuery());
        Assert.Equal([kept.PublicId], report.Orders.Select(o => o.PublicId));
        Assert.Equal(1, report.Summary.Orders);
        Assert.Equal("99", (await node.Send(new GetOrderQuery(test.PublicId))).TableNumber);
    }

    // Two tills, one order: the second save is refused, not silently merged.
    [Fact]
    public async Task Two_tills_changing_one_order_conflict()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "5"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 2));

        using var till1 = node.Scope();
        using var till2 = node.Scope();
        var store1 = node.Service<OtantikPos.Ordering.Application.Ports.IOrderStore>(till1);
        var store2 = node.Service<OtantikPos.Ordering.Application.Ports.IOrderStore>(till2);
        var copy1 = (await store1.GetAsync(order.PublicId, TestContext.Current.CancellationToken))!;
        var copy2 = (await store2.GetAsync(order.PublicId, TestContext.Current.CancellationToken))!;

        TillOperations.VoidItem(copy1, copy1.OrderItems.Single(), 1, null, "staff-cashier", 14m, DateTime.UtcNow);
        await node.Service<IUnitOfWork>(till1).SaveChangesAsync(TestContext.Current.CancellationToken);

        TillOperations.Close(copy2, PaymentMethod.Cash, "staff-manager", 14m, DateTime.UtcNow);
        await Assert.ThrowsAsync<ConflictException>(() => node.Service<IUnitOfWork>(till2).SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
