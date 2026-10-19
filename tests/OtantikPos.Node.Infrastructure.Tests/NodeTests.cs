using MediatR;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.Expenses;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Application.Production;
using OtantikPos.Inventory.Application.Purchases;
using OtantikPos.Inventory.Application.Recipes;
using OtantikPos.Inventory.Application.StockCounts;
using OtantikPos.Inventory.Application.Suppliers;
using OtantikPos.Inventory.Application.Treasury;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Treasury;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Costing;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Node.Infrastructure.Identity;
using OtantikPos.Node.Infrastructure.Messaging;
using OtantikPos.Node.Infrastructure.Printing;
using OtantikPos.Node.Infrastructure.Persistence;
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
        Assert.Equal(["Mona", "Omar", "Sara"], (await staff.GetTillStaffAsync(TestContext.Current.CancellationToken)).Select(s => s.FullName));

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
        // The outbox posts the sales' stock and the voided burger's waste each in its own time.
        Assert.True(await TestNode.WaitFor(async () =>
            await node.Db(db => db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.Sale)) == 5
            && await node.Db(db => db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.Waste)) == 2));

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

    // Online orders are run from the delivery system's own screens but cooked from the same stores:
    // the till reads the paid ones, takes their stock once, and counts them in its cost reports,
    // while its open orders and the day's takings stay its own.
    [Fact]
    public async Task Online_orders_paid_in_the_delivery_system_take_stock_and_count_in_the_reports()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        var patty = (await node.Send(new CreateRawMaterialCommand("Beef patty", UnitOfMeasure.Piece, PurchaseUnit: "piece", PurchaseUnitSize: 1, CostPerPurchaseUnit: 20))).Id;
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(patty, 50)]));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(patty, 1)]));
        Task<decimal> Patties() => node.Db(db => db.RawMaterials.Where(m => m.Id == patty).Select(m => m.QuantityOnHand).SingleAsync());

        // The sync starts from when it first runs; these are placed after.
        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.SyncState.AnyAsync(s => s.Key == SyncState.OnlineOrdersChangedSince))));
        Order Online(PaymentMethod method, int burgers, OrderStatus status = OrderStatus.Pending) => new()
        {
            Type = OrderType.Delivery,
            Status = status,
            PaymentMethod = method,
            PaymentStatus = PaymentStatus.Confirmed,
            OrderItems = [new OrderItem { MenuItemId = FakeDeliverySystem.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = burgers }],
        };
        // Priced as the delivery system prices them, at its 14%.
        var instapay = Online(PaymentMethod.Instapay, 3);
        instapay.DiscountAmount = 30;
        instapay.DeliveryFee = 20;
        OrderPricing.Reprice(instapay, 14m);
        var cash = Online(PaymentMethod.Cash, 1);
        lock (node.Cloud.OnlineOrders)
            node.Cloud.OnlineOrders.AddRange([instapay, cash, Online(PaymentMethod.Instapay, 5, OrderStatus.Cancelled)]);

        using var scope = node.Scope();
        var sync = node.Service<OnlineSalesSync>(scope);
        await sync.SyncAsync(ct);
        Assert.True(await TestNode.WaitFor(async () => await Patties() == 47));

        // Cash on delivery counts once the driver has handed it over.
        // Part paid with 10 of points.
        var delivered = Online(PaymentMethod.Cash, 1, OrderStatus.Delivered);
        delivered.PublicId = cash.PublicId;
        delivered.OrderItems.Single().PublicId = cash.OrderItems.Single().PublicId;
        delivered.PointsDiscountAmount = 10;
        OrderPricing.Reprice(delivered, 14m);
        lock (node.Cloud.OnlineOrders)
        {
            node.Cloud.OnlineOrders.Remove(cash);
            node.Cloud.OnlineOrders.Add(delivered);
        }
        await sync.SyncAsync(ct);
        await sync.SyncAsync(ct);
        Assert.True(await TestNode.WaitFor(async () => await Patties() == 46));
        Assert.Equal(2, await node.Db(db => db.OnlineSales.CountAsync()));

        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);
        var burger = Assert.Single((await node.Service<CostingService>(scope).GetTheoreticalCostAsync(today.Year, today.Month, ct)).Rows);
        Assert.Equal((4, 480m, 80m, 0), (burger.QuantitySold, burger.NetSales, burger.TheoreticalCost, burger.QuantityCostedNow));
        // Revenue: 360 less the 30 promo, the delivered 120, and the 20 delivery fee.
        var month = (await node.Service<KpiService>(scope).GetAsync(today.Year, today.Month, ct)).Statement;
        Assert.Equal((470m, 2), (month.Revenue, month.PaidOrders));

        // The day's cash is what the driver brought back: the burger, its VAT, less the points:
        // 120 + 16.80 - 10. The Instapay order was paid online, never in cash.
        var treasury = node.Service<TreasuryService>(scope);
        Assert.Equal(126.80m, delivered.TotalAmount);
        Assert.Equal(126.80m, (await treasury.GetDayRevenueAsync(today, today, ct)).Days.Single().CashSales);
        // Reconciled before tax: 480 of burgers, less the 30 promo and the 10 of points, plus the 20
        // delivery fee; the Instapay order's 350 paid online; 110 to come in as cash.
        var day = (await treasury.GetReconciliationAsync(today, today, ct)).Days.Single();
        Assert.Equal(
            (480m, 30m, 10m, 20m, 350m, 110m),
            (day.ItemSales, day.Discounts, day.Points, day.DeliveryFees, day.CardAndOnline, day.ExpectedCash));

        // Not the till's orders: nothing to take payment for, and not in its takings.
        Assert.Empty(await node.Send(new GetOpenOrdersQuery()));
        Assert.Empty((await node.Send(new GetDayReportQuery())).Orders);
        await Task.Delay(500, ct);
        Assert.Equal(46m, await Patties());
    }

    // A delivery system from before the online orders feed answers 404. The till carries on, and
    // once the feed appears takes the stock of online orders from then, not the weeks before.
    [Fact]
    public async Task Without_the_online_orders_feed_the_till_waits_and_starts_from_when_it_appears()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        node.Cloud.OnlineOrdersOffered = false;
        using var scope = node.Scope();
        var sync = node.Service<OnlineSalesSync>(scope);

        // The till has been reading since yesterday; an online order was paid an hour ago, while
        // the delivery system had no feed.
        Assert.True(await TestNode.WaitFor(() => node.Db(db => db.SyncState.AnyAsync(s => s.Key == SyncState.OnlineOrdersChangedSince))));
        await node.Db(async db =>
        {
            var cursor = await db.SyncState.SingleAsync(s => s.Key == SyncState.OnlineOrdersChangedSince, ct);
            cursor.Value = DateTime.UtcNow.AddDays(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            return await db.SaveChangesAsync(ct);
        });
        var placedWhileMissing = new Order
        {
            Type = OrderType.Delivery,
            PaymentMethod = PaymentMethod.Instapay,
            PaymentStatus = PaymentStatus.Confirmed,
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            OrderItems = [new OrderItem { MenuItemId = FakeDeliverySystem.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = 1 }],
        };
        lock (node.Cloud.OnlineOrders)
            node.Cloud.OnlineOrders.Add(placedWhileMissing);
        await sync.SyncAsync(ct);

        node.Cloud.OnlineOrdersOffered = true;
        await sync.SyncAsync(ct);
        Assert.Equal(0, await node.Db(db => db.OnlineSales.CountAsync()));
    }

    // The same through the database, which keeps a unit cost to six places: what the costing
    // screens show for 5 kg at L.E 100 and 2,500 g at L.E 62.50 is right to the cent.
    [Fact]
    public async Task Purchases_in_kilograms_and_grams_cost_out_to_the_cent()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;

        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(tomatoes, 5 * 1000, 100)]));
        using var scope = node.Scope();
        var costing = node.Service<CostingService>(scope);
        var material = Assert.Single(await costing.GetMaterialsAsync(ct));
        Assert.Equal((5000m, 0.02m, 20m, 100m), (material.QuantityOnHand, material.CostPerUnit, material.CostPerPurchaseUnit, material.StockValue));

        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(tomatoes, 2500, 62.50m)]));
        material = Assert.Single(await costing.GetMaterialsAsync(ct));
        Assert.Equal((7500m, 21.67m, 162.50m, 25m), (material.QuantityOnHand, material.CostPerPurchaseUnit, material.StockValue, material.LastPurchaseCostPerPurchaseUnit));
    }

    // A supplier's invoice in two steps: saved as a draft with its details and its paper, which
    // moves no stock; posted after the review, which does, once. Then it is in the history.
    [Fact]
    public async Task An_invoice_is_reviewed_as_a_draft_and_takes_stock_only_when_posted()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        Task<decimal> OnHand() => node.Db(db => db.RawMaterials.Where(m => m.Id == tomatoes).Select(m => m.QuantityOnHand).SingleAsync());
        var invoiceId = Guid.NewGuid();
        var nileFarms = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms"))).Id;

        var draft = await node.Send(new SavePurchaseInvoiceCommand(invoiceId, nileFarms, "F-1001", new DateOnly(2026, 10, 7),
            [new PurchaseInvoiceLineEntry(tomatoes, 5, "kg", 1000, 100)], "Omar", PaymentMethod: PurchasePaymentMethod.Safe));
        Assert.Equal((PurchaseInvoiceStatus.Draft, 100m, 5000m), (draft.Status, draft.Total, draft.Lines.Single().Quantity));
        Assert.Equal(0m, await OnHand());

        // The paper: a PNG by its first bytes. One that only says it is a PNG is refused.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new AttachPurchaseInvoiceFileCommand(invoiceId, "x.png", "image/png", [1, 2, 3], "Omar")));
        var attached = await node.Send(new AttachPurchaseInvoiceFileCommand(invoiceId, "invoice.png", "image/png", png, "Omar"));
        Assert.Equal(("invoice.png", png.Length), (attached.Attachment!.FileName, attached.Attachment.Bytes));

        Assert.Single(await node.Send(new GetPurchaseInvoicesQuery(PurchaseInvoiceStatus.Draft)));
        Assert.Empty(await node.Send(new GetPurchaseInvoicesQuery(PurchaseInvoiceStatus.Posted)));

        // Confirmed after the review; confirmed again, as a lost answer would.
        var posted = await node.Send(new PostPurchaseInvoiceCommand(invoiceId, "Omar"));
        await node.Send(new PostPurchaseInvoiceCommand(invoiceId, "Omar"));
        Assert.Equal((PurchaseInvoiceStatus.Posted, "Omar"), (posted.Status, posted.PostedBy));
        Assert.Equal(5000m, await OnHand());
        using (var scope = node.Scope())
        {
            var material = Assert.Single(await node.Service<CostingService>(scope).GetMaterialsAsync(ct));
            Assert.Equal((20m, 100m), (material.CostPerPurchaseUnit, material.StockValue));
        }

        var history = Assert.Single(await node.Send(new GetPurchaseInvoicesQuery(PurchaseInvoiceStatus.Posted)));
        Assert.Equal(("Nile Farms", "F-1001", 100m, "Omar", "invoice.png", 1), (history.SupplierName, history.SupplierInvoiceNumber, history.Total, history.CreatedBy, history.AttachmentFileName, history.Lines));
        Assert.Equal(png, (await node.Send(new GetPurchaseInvoiceAttachmentQuery(invoiceId)))!.Content);
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new DiscardPurchaseInvoiceCommand(invoiceId)));
        var supplier = Assert.Single(await node.Send(new GetSuppliersQuery()));
        Assert.Equal(("Nile Farms", 1, 100m, posted.PostedAtUtc), (supplier.Name, supplier.Invoices, supplier.PostedTotal, supplier.LastPostedAtUtc));

        // A delivery sent in one request is filed as a posted invoice too, in grams.
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(tomatoes, 2500, 62.50m)], "Sara"));
        Assert.Equal(2, (await node.Send(new GetPurchaseInvoicesQuery(PurchaseInvoiceStatus.Posted))).Count);
        Assert.Equal(7500m, await OnHand());
    }

    // Suppliers are added, changed, and deleted. One that is on an invoice is archived instead: off
    // the list for new invoices, still on the ones it is on, and restorable.
    [Fact]
    public async Task A_supplier_on_an_invoice_is_archived_rather_than_deleted()
    {
        await using var node = await TestNode.StartAsync();
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        Task<IReadOnlyList<SupplierDto>> Suppliers(bool archived = false) => node.Send(new GetSuppliersQuery(archived));
        PurchaseInvoiceLineEntry[] OneKilo = [new(tomatoes, 1, "kg", 1000, 20)];

        var nileFarms = Guid.NewGuid();
        await node.Send(new SaveSupplierCommand(nileFarms, "Nile Farms", "Hassan", "0100 123 4567"));
        var edited = await node.Send(new SaveSupplierCommand(nileFarms, "Nile Farms", "Hassan", "0100 765 4321", "orders@nile.example"));
        Assert.Equal(("0100 765 4321", "orders@nile.example"), (edited.Phone, edited.Email));
        var deltaOils = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Delta Oils"))).Id;

        // One name, one supplier: spelt differently, it is still Nile Farms.
        var twice = await Assert.ThrowsAsync<DomainException>(() => node.Send(new SaveSupplierCommand(Guid.NewGuid(), " nile  FARMS ")));
        Assert.Equal("Nile Farms is already a supplier.", twice.Message);
        Assert.Equal(["Delta Oils", "Nile Farms"], (await Suppliers()).Select(s => s.Name));

        // Never invoiced: deleted outright.
        Assert.Equal(SupplierRemoval.Deleted, (await node.Send(new DeleteSupplierCommand(deltaOils))).Outcome);
        Assert.Equal(SupplierRemoval.Deleted, (await node.Send(new DeleteSupplierCommand(deltaOils))).Outcome);
        Assert.Equal(["Nile Farms"], (await Suppliers(archived: true)).Select(s => s.Name));

        // On a posted invoice and a draft: archived.
        var posted = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(posted, nileFarms, "F-1", null, OneKilo, "Omar", PaymentMethod: PurchasePaymentMethod.Safe));
        await node.Send(new PostPurchaseInvoiceCommand(posted, "Omar"));
        var draft = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(draft, nileFarms, "F-2", null, OneKilo, "Omar", PaymentMethod: PurchasePaymentMethod.Safe));
        Assert.Equal(SupplierRemoval.Archived, (await node.Send(new DeleteSupplierCommand(nileFarms))).Outcome);

        Assert.Empty(await Suppliers());
        var archived = Assert.Single(await Suppliers(archived: true));
        Assert.Equal((true, 2, 20m), (archived.IsArchived, archived.Invoices, archived.PostedTotal));
        Assert.Equal("Nile Farms", (await node.Send(new GetPurchaseInvoiceQuery(posted)))!.SupplierName);
        Assert.Equal("Nile Farms", Assert.Single(await node.Send(new GetPurchaseInvoicesQuery(PurchaseInvoiceStatus.Posted))).SupplierName);

        // The draft that had it keeps it; a new invoice cannot take it, nor can it be added again.
        await node.Send(new SavePurchaseInvoiceCommand(draft, nileFarms, "F-2", null, OneKilo, "Omar", PaymentMethod: PurchasePaymentMethod.Safe));
        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            node.Send(new SavePurchaseInvoiceCommand(Guid.NewGuid(), nileFarms, "F-3", null, OneKilo, "Omar", PaymentMethod: PurchasePaymentMethod.Safe)));
        Assert.Contains("archived", refused.Message);
        Assert.Contains("archived", (await Assert.ThrowsAsync<DomainException>(() => node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms")))).Message);

        // Restored, it is back for new invoices.
        await node.Send(new RestoreSupplierCommand(nileFarms));
        Assert.Equal(["Nile Farms"], (await Suppliers()).Select(s => s.Name));
        await node.Send(new SavePurchaseInvoiceCommand(Guid.NewGuid(), nileFarms, "F-3", null, OneKilo, "Omar", PaymentMethod: PurchasePaymentMethod.Safe));
    }

    // The supplier names typed on invoices before suppliers were kept become suppliers on the
    // update: one per name, however it was spelt, each invoice pointing at its own.
    [Fact]
    public async Task Supplier_names_typed_on_invoices_become_suppliers()
    {
        await using var node = await TestNode.StartAsync();
        await node.Db(async db =>
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261007133059_BackfillPurchaseInvoices");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO inventory."PurchaseInvoices" ("Id", "Status", "CreatedAtUtc", "CreatedBy", "SupplierName") VALUES
                    ('00000000-0000-0000-0000-000000000001', 'Posted', now() - interval '2 days', 'Omar', 'nile farms'),
                    ('00000000-0000-0000-0000-000000000002', 'Posted', now() - interval '1 day', 'Omar', ' Nile  Farms '),
                    ('00000000-0000-0000-0000-000000000003', 'Draft', now(), 'Sara', 'Delta Oils'),
                    ('00000000-0000-0000-0000-000000000004', 'Posted', now(), 'Sara', NULL);
                """);
            await migrator.MigrateAsync();
            return 0;
        });

        var suppliers = await node.Send(new GetSuppliersQuery());
        // The latest spelling names it.
        Assert.Equal([("Delta Oils", 1), ("Nile Farms", 2)], suppliers.Select(s => (s.Name, s.Invoices)));
        var invoices = await node.Db(db => db.PurchaseInvoices.OrderBy(i => i.Id).Select(i => i.SupplierId).ToListAsync());
        var nile = suppliers.Single(s => s.Name == "Nile Farms").Id;
        Assert.Equal([nile, nile, suppliers.Single(s => s.Name == "Delta Oils").Id, null], invoices);
    }

    // A recipe of its own: written off the menu (R&D) with a planned price, then linked to the
    // burger, which sells by it. Deleted, it is kept and marked: off the list, no stock taken by
    // it, and the month's report keeps what the sales made under it cost. Restored, it is linked
    // again, or off the menu if the burger has another recipe by then.
    [Fact]
    public async Task Recipes_are_written_off_the_menu_linked_and_deleted_softly()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        async Task<Guid> Piece(string name, decimal cost) =>
            (await node.Send(new CreateRawMaterialCommand(name, UnitOfMeasure.Piece, PurchaseUnit: "piece", PurchaseUnitSize: 1, CostPerPurchaseUnit: cost))).Id;
        var patty = await Piece("Beef patty", 20);
        var bun = await Piece("Bun", 5);
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(patty, 50, 1_000), new(bun, 50, 250)]));
        Task<decimal> OnHand(Guid id) => node.Db(db => db.RawMaterials.Where(m => m.Id == id).Select(m => m.QuantityOnHand).SingleAsync());
        async Task Sell(int burgers)
        {
            var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: Guid.NewGuid().ToString()[..6]));
            await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, burgers));
            await node.Send(new SendToKitchenCommand(order.PublicId));
            await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        }
        using var scope = node.Scope();
        var costing = node.Service<CostingService>(scope);

        // Off the menu, with a planned price of 100: 25 a portion is 25%.
        var mains = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Mains"))).Id;
        var trial = await node.Send(new SaveRecipeCommand(null, "Double burger trial", null, null, null, 1, [new(patty, 1), new(bun, 1)], mains, 100));
        var card = await costing.GetRecipeCardAsync(trial.Id, ct);
        Assert.Equal(("Double burger trial", true, (string?)null, 100m, 25m, 25m), (card.Recipe, card.IsStandalone, card.LinkedTo, card.MenuPrice!.Value, card.CostPerPortion, card.FoodCostPercentActual!.Value));
        // Off the menu, the burger does not sell by it.
        Assert.Null(await node.Send(new GetRecipeQuery(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null)));

        // Linked to the burger: from the next sale, the burger takes stock by it.
        await node.Send(new SaveRecipeCommand(trial.Id, "Double burger", RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, 1, [new(patty, 2), new(bun, 1)], mains));
        card = await costing.GetRecipeCardAsync(trial.Id, ct);
        Assert.Equal(("Double burger", false, "Burger"), (card.Recipe, card.IsStandalone, card.LinkedTo));
        await Sell(2);
        Assert.True(await TestNode.WaitFor(async () => await OnHand(patty) == 46m));

        // One recipe a menu item sells by: a second is refused, naming the first.
        var second = await Assert.ThrowsAsync<DomainException>(() => node.Send(new SaveRecipeCommand(
            null, "Another", RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, 1, [new(patty, 1)], mains)));
        Assert.Equal("That menu item already sells by the recipe \"Double burger\". Unlink or delete that one first.", second.Message);

        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);
        var before = Assert.Single((await costing.GetTheoreticalCostAsync(today.Year, today.Month, ct)).Rows, r => r.MenuItemId == FakeDeliverySystem.Burger);

        // Deleted: kept and marked, off the list, and the burger takes nothing any more.
        await node.Send(new DeleteRecipeCommand(trial.Id, "Omar"));
        Assert.Empty(await costing.GetRecipesAsync(false, ct));
        var deleted = Assert.Single(await costing.GetRecipesAsync(true, ct));
        Assert.Equal((true, "Omar"), (deleted.IsDeleted, deleted.DeletedBy));
        Assert.True(await node.Db(db => db.Recipes.AnyAsync(r => r.Id == trial.Id && r.DeletedAtUtc != null)));
        await Sell(1);
        // Both sales handled by the stock module; the second took nothing.
        var settled = typeof(OtantikPos.Ordering.Contracts.OrderSettled).FullName!;
        Assert.True(await TestNode.WaitFor(async () => await node.Db(db => db.OutboxMessages.CountAsync(m => m.Type == settled && m.ProcessedAtUtc != null)) == 2));
        Assert.Equal(46m, await OnHand(patty));
        Assert.Null(await node.Send(new GetRecipeQuery(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null)));

        // The month's report still costs the two burgers sold under it, from what their sales took.
        var after = Assert.Single((await costing.GetTheoreticalCostAsync(today.Year, today.Month, ct)).Rows, r => r.MenuItemId == FakeDeliverySystem.Burger);
        Assert.Equal(before.TheoreticalCost, after.TheoreticalCost);
        Assert.Equal(90m, after.TheoreticalCost);

        // Restored while the burger has no other recipe: linked again.
        var restored = await node.Send(new RestoreRecipeCommand(trial.Id));
        Assert.Equal((false, (int?)FakeDeliverySystem.Burger), (restored.IsDeleted, restored.CatalogItemId));

        // Deleted again, and the burger given a new one in the meantime: it comes back off the menu.
        await node.Send(new DeleteRecipeCommand(trial.Id, "Omar"));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(patty, 1), new(bun, 1)]));
        restored = await node.Send(new RestoreRecipeCommand(trial.Id, "Double burger (old)"));
        Assert.Equal(((int?)null, "Double burger"), (restored.CatalogItemId, restored.Name));
        Assert.Equal(2, (await costing.GetRecipesAsync(false, ct)).Count);

        // Through the old path, an empty recipe deletes the one the burger sells by, softly.
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, []));
        Assert.Equal(2, await node.Db(db => db.Recipes.CountAsync()));
        Assert.Equal(["Double burger"], (await costing.GetRecipesAsync(false, ct)).Select(r => r.Recipe));
    }

    // The theoretical cost by hand: an accountant types the month's quantities instead of the till's
    // sales, for every active recipe and prep item. Kept for the month, said to be typed and by whom,
    // costed by the recipes at today's prices, shared costs spread over the meals typed in. The
    // till's own report is untouched.
    [Fact]
    public async Task The_theoretical_cost_can_come_from_quantities_typed_in()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        async Task<Guid> Piece(string name, decimal cost) =>
            (await node.Send(new CreateRawMaterialCommand(name, UnitOfMeasure.Piece, PurchaseUnit: "piece", PurchaseUnitSize: 1, CostPerPurchaseUnit: cost))).Id;
        var patty = await Piece("Beef patty", 20);
        var bun = await Piece("Bun", 5);
        var flour = (await node.Send(new CreateRawMaterialCommand("Flour", UnitOfMeasure.Gram, PurchaseUnit: "kg", PurchaseUnitSize: 1000, CostPerPurchaseUnit: 20))).Id;
        // The burger on the menu at 120, 25 by its recipe; a staff meal off the menu, unpriced;
        // dough at 12 a kg by its recipe.
        var burger = (await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(patty, 1), new(bun, 1)])))!.Id;
        var staffMeals = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Staff meals"))).Id;
        var staffMeal = (await node.Send(new SaveRecipeCommand(null, "Staff burger", null, null, null, 1, [new(patty, 1)], staffMeals))).Id;
        var dough = (await node.Send(new SavePrepItemCommand(null, "Pizza dough", UnitOfMeasure.Gram, 10_000, [new(flour, 6_000)], DisplayUnit: "kg", DisplayUnitSize: 1000))).Id;

        using var scope = node.Scope();
        var costing = node.Service<CostingService>(scope);
        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);
        // 140 of frying oil a month: over the 14 meals typed in, 10 a meal.
        await costing.SaveSharedCostAsync(null, new SaveSharedCostRequest("Frying oil", null, 140, null), ct);

        var sheet = await costing.GetManualSalesAsync(today.Year, today.Month, ct);
        Assert.Equal(
            [(ManualSalesItemKind.Recipe, "Burger", "Uncategorized", "portion", (decimal?)25m, (decimal?)120m),
             (ManualSalesItemKind.Recipe, "Staff burger", "Staff meals", "portion", 20m, null),
             (ManualSalesItemKind.PrepItem, "Pizza dough", "Prep items", "kg", 12m, null)],
            sheet.Items.Select(i => (i.Kind, i.Name, i.Group, i.Unit, i.CostPerUnit, i.Price)).OrderBy(i => i.Kind).ThenBy(i => i.Name));
        Assert.All(sheet.Items, i => Assert.Null(i.Quantity));

        // Portions are whole, and nothing is negative.
        await Assert.ThrowsAsync<DomainException>(() => costing.SaveManualSalesAsync(today.Year, today.Month,
            [new(ManualSalesItemKind.Recipe, burger, 1.5m)], "Sara", ct));
        await Assert.ThrowsAsync<DomainException>(() => costing.SaveManualSalesAsync(today.Year, today.Month,
            [new(ManualSalesItemKind.PrepItem, dough, -1)], "Sara", ct));

        var report = await costing.SaveManualSalesAsync(today.Year, today.Month,
            [new(ManualSalesItemKind.Recipe, burger, 10), new(ManualSalesItemKind.Recipe, staffMeal, 4), new(ManualSalesItemKind.PrepItem, dough, 2.5m)],
            "Sara", ct);
        Assert.Equal((TheoreticalSource.Manual, "Sara"), (report.Source, report.EnteredBy));
        var rows = report.Rows.ToDictionary(r => r.MenuItem);
        // Burger: 10 at 25 and 10 of oil each is 350, against 1,200 of sales.
        Assert.Equal((10m, 1_200m, 35m, 350m, 100m, 29.2m, CostStatus.WithinTarget),
            (rows["Burger"].QuantitySold, rows["Burger"].NetSales, rows["Burger"].RecipeCostPerUnit, rows["Burger"].TheoreticalCost,
                rows["Burger"].SharedCost, rows["Burger"].FoodCostPercent!.Value, rows["Burger"].Status));
        Assert.Equal((4m, 0m, 120m, CostStatus.NoPrice), (rows["Staff burger"].QuantitySold, rows["Staff burger"].NetSales, rows["Staff burger"].TheoreticalCost, rows["Staff burger"].Status));
        Assert.Equal((2.5m, "kg", 30m, 0m, (Guid?)dough), (rows["Pizza dough"].QuantitySold, rows["Pizza dough"].Unit, rows["Pizza dough"].TheoreticalCost, rows["Pizza dough"].SharedCost, rows["Pizza dough"].PrepItemId));
        Assert.Equal((14m, 1_200m, 500m, 41.7m), (report.QuantitySold, report.NetSales, report.TheoreticalCost, report.FoodCostPercent!.Value));

        // Kept for the month: the sheet comes back filled in, and the report from it.
        Assert.Equal(10m, (await costing.GetManualSalesAsync(today.Year, today.Month, ct)).Items.Single(i => i.ItemId == burger).Quantity);
        var again = await costing.GetTheoreticalCostAsync(today.Year, today.Month, ct, TheoreticalSource.Manual);
        Assert.Equal((500m, "Sara"), (again.TheoreticalCost, again.EnteredBy));

        // The till's own report for the month: nothing sold, and said to be the till's.
        var pos = await costing.GetTheoreticalCostAsync(today.Year, today.Month, ct);
        Assert.Equal((TheoreticalSource.Pos, 0, (string?)null), (pos.Source, pos.Rows.Count, pos.EnteredBy));
    }

    // The kitchen's own recipe categories. One holding recipes is deleted only by moving them to
    // another, the deleted ones with them; a recipe written on the recipe screen needs one.
    [Fact]
    public async Task Recipe_categories_are_kept_apart_from_the_menu_and_move_their_recipes_when_deleted()
    {
        await using var node = await TestNode.StartAsync();
        var patty = (await node.Send(new CreateRawMaterialCommand("Beef patty", UnitOfMeasure.Piece))).Id;
        var sauces = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Sauces", "صوصات"))).Id;
        var staff = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Staff meals"))).Id;
        var twice = await Assert.ThrowsAsync<DomainException>(() => node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), " staff  MEALS")));
        Assert.Equal("There is already a category called Staff meals.", twice.Message);
        Assert.Equal("Sauce bar", (await node.Send(new SaveRecipeCategoryCommand(sauces, "Sauce bar", "صوصات"))).Name);

        Task<RecipeDto> Write(string name, Guid? category) =>
            node.Send(new SaveRecipeCommand(null, name, null, null, null, 1, [new(patty, 1)], category));
        Assert.Equal("Choose the recipe's category.", (await Assert.ThrowsAsync<DomainException>(() => Write("Unsorted", null))).Message);
        var familyMeal = await Write("Family meal", staff);
        var oldMeal = await Write("Old family meal", staff);
        await node.Send(new DeleteRecipeCommand(oldMeal.Id, "Omar"));

        var categories = await node.Send(new GetRecipeCategoriesQuery());
        Assert.Equal([("Sauce bar", "صوصات", 0), ("Staff meals", (string?)null, 1)], categories.Select(c => (c.Name, c.NameAr, c.Recipes)));

        // Staff meals holds a recipe: it goes only with somewhere for its recipes to go.
        var held = await Assert.ThrowsAsync<DomainException>(() => node.Send(new DeleteRecipeCategoryCommand(staff)));
        Assert.Equal("Staff meals has 1 recipe. Choose the category it moves to.", held.Message);
        await node.Send(new DeleteRecipeCategoryCommand(staff, sauces));
        Assert.Equal([("Sauce bar", 1)], (await node.Send(new GetRecipeCategoriesQuery())).Select(c => (c.Name, c.Recipes)));
        var moved = await node.Db(db => db.Recipes.Where(r => r.Id == familyMeal.Id || r.Id == oldMeal.Id).Select(r => r.RecipeCategoryId).ToListAsync());
        Assert.All(moved, id => Assert.Equal(sauces, id));

        // One with no recipes goes at once; the card says which category a recipe is in.
        var empty = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Catering orders"))).Id;
        await node.Send(new DeleteRecipeCategoryCommand(empty));
        using var scope = node.Scope();
        var card = await node.Service<CostingService>(scope).GetRecipeCardAsync(familyMeal.Id, TestContext.Current.CancellationToken);
        Assert.Equal(((Guid?)sauces, "Sauce bar", "صوصات"), (card.RecipeCategoryId, card.RecipeCategory, card.RecipeCategoryAr));
    }

    // Prep items are sorted under the kitchen's recipe categories too, as the dishes are: counted
    // in them, changed through a manager's approval like the rest of a prep item, and moved with
    // a category that is deleted.
    [Fact]
    public async Task Prep_items_are_sorted_under_recipe_categories_and_move_with_them()
    {
        await using var node = await TestNode.StartAsync();
        var flour = (await node.Send(new CreateRawMaterialCommand("Flour", UnitOfMeasure.Gram))).Id;
        var sauces = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Sauces", "صوصات"))).Id;
        var doughs = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Doughs", "عجين"))).Id;
        SavePrepItemCommand Dough(Guid? category) =>
            new(null, "Pizza dough", UnitOfMeasure.Gram, 10_000, [new(flour, 6_000)], RecipeCategoryId: category);

        var gone = await Assert.ThrowsAsync<DomainException>(() => node.Send(Dough(Guid.NewGuid())));
        Assert.Equal("That recipe category is no longer on the list. Choose it again, or add it.", gone.Message);
        var dough = await node.Send(Dough(sauces));
        Assert.Equal((Guid?)sauces, dough.RecipeCategoryId);
        Assert.Equal((Guid?)sauces, (await node.Send(new GetPrepItemsQuery())).Single().RecipeCategoryId);
        Assert.Equal([("Doughs", 0, 0), ("Sauces", 0, 1)], (await node.Send(new GetRecipeCategoriesQuery())).Select(c => (c.Name, c.Recipes, c.PrepItems)));

        // The accountant moves it to Doughs: asked, and said so line by line; approved, it moves.
        var change = Dough(doughs).Change;
        var asked = await node.Send(new RequestPrepItemChangeCommand(Guid.NewGuid(), dough.Id, change, null, "staff-mona", "Mona"));
        Assert.Equal([new ApprovalChange("Recipe category", "Sauces", "Doughs")], asked.Request.Changes);
        await node.Send(new ApproveRequestCommand(asked.Request.Id, "Omar"));
        Assert.Equal((Guid?)doughs, (await node.Send(new GetPrepItemQuery(dough.Id)))!.RecipeCategoryId);

        // Doughs holds it: deleted only with somewhere for it to go, and it goes there.
        var held = await Assert.ThrowsAsync<DomainException>(() => node.Send(new DeleteRecipeCategoryCommand(doughs)));
        Assert.Equal("Doughs has 1 recipe. Choose the category it moves to.", held.Message);
        await node.Send(new DeleteRecipeCategoryCommand(doughs, sauces));
        Assert.Equal((Guid?)sauces, (await node.Send(new GetPrepItemQuery(dough.Id)))!.RecipeCategoryId);
        Assert.Equal([("Sauces", 1)], (await node.Send(new GetRecipeCategoriesQuery())).Select(c => (c.Name, c.PrepItems)));
    }

    // The categories typed on off-menu recipes before categories were kept become categories on
    // the update, one per name however spelt; the rest are left to be sorted.
    [Fact]
    public async Task Typed_recipe_categories_become_categories()
    {
        await using var node = await TestNode.StartAsync();
        await node.Db(async db =>
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261007172513_StandaloneRecipesAndSoftDelete");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO inventory."Recipes" ("Id", "Name", "Category", "Portions", "CreatedAtUtc") VALUES
                    ('00000000-0000-0000-0000-000000000001', 'Garlic dip', 'sauces', 1, now()),
                    ('00000000-0000-0000-0000-000000000002', 'Chili oil', ' Sauces ', 1, now()),
                    ('00000000-0000-0000-0000-000000000003', 'Family meal', 'Staff meals', 1, now()),
                    ('00000000-0000-0000-0000-000000000004', 'Trial', NULL, 1, now());
                """);
            await migrator.MigrateAsync();
            return 0;
        });

        var categories = await node.Send(new GetRecipeCategoriesQuery());
        Assert.Equal([2, 1], categories.Select(c => c.Recipes));
        Assert.Equal("STAFF MEALS", (await node.Db(db => db.RecipeCategories.SingleAsync(c => c.Name == "Staff meals"))).NormalizedName);
        Assert.Null(await node.Db(db => db.Recipes.Where(r => r.Name == "Trial").Select(r => r.RecipeCategoryId).SingleAsync()));
    }

    // Pizza dough made in the kitchen: its recipe costs it per kg, a production order takes the
    // flour, yeast and oil off stock and puts the dough in at their cost over what came out, and a
    // burger sold takes dough, not flour. Between two counts, the flour production took is a use
    // by its recipe, not a loss; what is missing shows on the dough.
    [Fact]
    public async Task Prep_items_are_made_by_production_orders_and_used_by_the_dishes_sold()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        async Task<Guid> Material(string name, UnitOfMeasure unit) => (await node.Send(new CreateRawMaterialCommand(name, unit))).Id;
        Task<decimal> OnHand(Guid id) => node.Db(db => db.RawMaterials.Where(m => m.Id == id).Select(m => m.QuantityOnHand).SingleAsync());
        var flour = await Material("Flour", UnitOfMeasure.Gram);
        var yeast = await Material("Yeast", UnitOfMeasure.Gram);
        var oil = await Material("Olive oil", UnitOfMeasure.Millilitre);
        // 20 a kg of flour, 500 a kg of yeast, 60 a litre of oil.
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(flour, 20_000, 400), new(yeast, 500, 250), new(oil, 5_000, 300)]));

        // A 10 kg batch: 120 of flour, 50 of yeast, 12 of oil, so 18.20 a kg by its recipe.
        var dough = await node.Send(new SavePrepItemCommand(
            null, "Pizza dough", UnitOfMeasure.Gram, 10_000, [new(flour, 6_000), new(yeast, 100), new(oil, 200)],
            Code: "PREP-1", DisplayUnit: "kg", DisplayUnitSize: 1000));
        Assert.Equal((182m, 18.20m, 0m), (dough.StandardBatchCost, dough.StandardCostPerDisplayUnit, dough.QuantityOnHand));
        Assert.Equal([("Flour", 120m, 65.9m), ("Yeast", 50m, 27.5m), ("Olive oil", 12m, 6.6m)],
            dough.Ingredients.Select(i => (i.Name, i.BatchCost!.Value, i.SharePercent!.Value)));

        var opening = Guid.NewGuid();
        await node.Send(new RecordStockCountCommand(opening, [new(flour, 20_000), new(yeast, 500), new(oil, 5_000), new(dough.Id, 0)], "Omar"));

        // The review: 10 kg planned, 9.6 kg came out, so 182 over 9.6 kg. Four batches' flour is
        // more than the records show: flagged, not refused.
        var preview = await node.Send(new PreviewProductionQuery(dough.Id, 10_000, 9_600));
        Assert.Equal((182m, 18.96m), (preview.TotalCost, preview.UnitCostPerDisplayUnit));
        Assert.Empty(preview.Shortages);
        var big = await node.Send(new PreviewProductionQuery(dough.Id, 40_000));
        Assert.Equal(["Flour"], big.Shortages);
        Assert.Equal(4_000m, big.Lines.Single(l => l.Material == "Flour").Short);

        // Made, and made again as a lost answer would: once.
        var orderId = Guid.NewGuid();
        var made = await node.Send(new MakeProductionOrderCommand(orderId, dough.Id, 10_000, 9_600, "Morning batch", "Omar"));
        await node.Send(new MakeProductionOrderCommand(orderId, dough.Id, 10_000, 9_600, null, "Omar"));
        Assert.Equal((182m, 18.96m, 96m, "Omar", "Morning batch"), (made.TotalCost, made.UnitCostPerDisplayUnit, made.YieldPercent, made.ProducedBy, made.Notes));
        Assert.Equal((14_000m, 400m, 4_800m, 9_600m), (await OnHand(flour), await OnHand(yeast), await OnHand(oil), await OnHand(dough.Id)));
        var profile = (await node.Send(new GetPrepItemQuery(dough.Id)))!;
        Assert.Equal((18.96m, 182m, 18.96m), (profile.AverageCostPerDisplayUnit, profile.StockValue, profile.LastBatchCostPerDisplayUnit));
        Assert.Equal(orderId, Assert.Single(profile.Recent).Id);
        Assert.Equal(orderId, Assert.Single(await node.Send(new GetProductionOrdersQuery())).Id);

        // A burger takes 250 g of dough. Two sold take 500 g of it, at its batch cost, and no flour.
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(dough.Id, 250)]));
        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "P1"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 2));
        await node.Send(new SendToKitchenCommand(order.PublicId));
        await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        Assert.True(await TestNode.WaitFor(async () => await OnHand(dough.Id) == 9_100m));
        Assert.Equal(14_000m, await OnHand(flour));
        var sale = await node.Db(db => db.StockMovements.SingleAsync(m => m.Reason == StockMovementReason.Sale));
        // Its unit cost kept to six places, as every cost in the ledger is.
        Assert.Equal((dough.Id, -500m, 0.018958m), (sale.RawMaterialId, sale.Quantity, sale.UnitCost!.Value));

        // The closing count finds 100 g of dough fewer than the records.
        var closing = Guid.NewGuid();
        await node.Send(new RecordStockCountCommand(closing, [new(flour, 14_000), new(yeast, 400), new(oil, 4_800), new(dough.Id, 9_000)], "Omar"));
        using (var scope = node.Scope())
        {
            var rows = (await node.Service<VarianceService>(scope).GetVarianceAsync(opening, closing, ct)).Rows.ToDictionary(r => r.Material);
            Assert.Equal((6_000m, 6_000m, 0m, VarianceEvaluation.WithinLimit),
                (rows["Flour"].StandardUsage, rows["Flour"].ActualUsage, rows["Flour"].VarianceQuantity, rows["Flour"].Evaluation));
            Assert.Equal((9_600m, 500m, 600m, 100m, VarianceEvaluation.Unfavourable),
                (rows["Pizza dough"].Received, rows["Pizza dough"].StandardUsage, rows["Pizza dough"].ActualUsage, rows["Pizza dough"].VarianceQuantity, rows["Pizza dough"].Evaluation));
        }

        // Made, so not bought; and not made from what is made from it.
        var bought = await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePurchaseInvoiceCommand(
            Guid.NewGuid(), null, null, null, [new PurchaseInvoiceLineEntry(dough.Id, 1, "kg", 1000, 18)], "Omar", PaymentMethod: PurchasePaymentMethod.Safe)));
        Assert.StartsWith("Pizza dough is made in the kitchen", bought.Message);
        var sauce = await node.Send(new SavePrepItemCommand(null, "Dough crumb", UnitOfMeasure.Gram, 1_000, [new(dough.Id, 1_000)]));
        var loop = await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePrepItemCommand(
            dough.Id, "Pizza dough", UnitOfMeasure.Gram, 10_000, [new(flour, 6_000), new(sauce.Id, 50)])));
        Assert.Equal("Dough crumb is made with Pizza dough, so Pizza dough cannot be made with Dough crumb.", loop.Message);
        Assert.Equal(["Dough crumb", "Pizza dough"], (await node.Send(new GetPrepItemsQuery())).Select(p => p.Name));
    }

    // A till that took deliveries before invoices were kept: on the update, each is filed as the
    // posted invoice it was, in the material's own unit and at what it was booked at.
    [Fact]
    public async Task Deliveries_from_before_invoices_are_filed_in_the_history()
    {
        await using var node = await TestNode.StartAsync();
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var oil = (await node.Send(new CreateRawMaterialCommand("Frying oil", UnitOfMeasure.Millilitre))).Id;
        var delivery = Guid.NewGuid();
        // As the till recorded a delivery then: the stock movements, and no invoice.
        await node.Db(async db =>
        {
            var materials = await db.RawMaterials.ToDictionaryAsync(m => m.Id);
            db.StockMovements.AddRange(materials[tomatoes].ReceivePurchase(2500, delivery, 62.50m), materials[oil].ReceivePurchase(10000, delivery));
            return await db.SaveChangesAsync();
        });

        // The update, run again over it.
        await node.Db(async db =>
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261007131728_AddPurchaseInvoices");
            await migrator.MigrateAsync();
            return 0;
        });

        var filed = Assert.Single(await node.Send(new GetPurchaseInvoicesQuery(PurchaseInvoiceStatus.Posted)));
        Assert.Equal((delivery, (string?)null, 62.50m, 2, (string?)null), (filed.Id, filed.SupplierName, filed.Total, filed.Lines, filed.AttachmentFileName));
        var lines = (await node.Send(new GetPurchaseInvoiceQuery(delivery)))!.Lines.OrderBy(l => l.Material).ToList();
        Assert.Equal(
            [("Frying oil", 10000m, "ml", 10000m, (decimal?)null), ("Tomatoes", 2500m, "g", 2500m, 62.50m)],
            lines.Select(l => (l.Material, l.EnteredQuantity, l.UnitLabel, l.Quantity, l.Cost)));
    }

    // The categories typed on materials become purchase categories: one per name, capitals and
    // extra spaces ignored, spelled as most of its materials had it. Materials with none, or with
    // "general", go into General.
    [Fact]
    public async Task Typed_material_categories_become_purchase_categories()
    {
        await using var node = await TestNode.StartAsync();
        await node.Db(async db =>
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261008132446_ManualSalesSheets");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO inventory."RawMaterials" ("Id", "Name", "Unit", "QuantityOnHand", "ReorderLevel", "Category") VALUES
                    ('00000000-0000-0000-0000-000000000001', 'Milk', 'Millilitre', 0, 0, 'dairy'),
                    ('00000000-0000-0000-0000-000000000002', 'Cheese', 'Gram', 0, 0, ' Dairy '),
                    ('00000000-0000-0000-0000-000000000003', 'Cheddar', 'Gram', 0, 0, 'Dairy'),
                    ('00000000-0000-0000-0000-000000000004', 'Salt', 'Gram', 0, 0, NULL),
                    ('00000000-0000-0000-0000-000000000005', 'Napkins', 'Piece', 0, 0, 'general');
                """);
            await migrator.MigrateAsync();
            return 0;
        });

        var categories = await node.Send(new GetPurchaseCategoriesQuery());
        Assert.Equal([("General", true, 2), ("Dairy", false, 3)], categories.Select(c => (c.Name, c.IsGeneral, c.Materials)));
        Assert.Equal(PurchaseCategory.GeneralId, categories[0].Id);
        Assert.Equal("Dairy", (await node.Send(new GetRawMaterialsQuery())).Single(m => m.Name == "Milk").Category);
    }

    // Expense items go on purchase invoices beside raw materials. Posting takes the stock lines in
    // and leaves stock alone for the expenses, which are only what they cost, under their category.
    // They never reach the stock list or a recipe. The spending report adds both up by category;
    // the KPIs and the break-even worksheet take the expenses off on each category's line, on top
    // of what is typed in.
    [Fact]
    public async Task Expenses_bought_on_invoices_reach_the_profit_and_loss_but_never_stock()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);

        async Task<Guid> Category(string name, ExpenseLine line) => (await node.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), name, null, line))).Id;
        var dairy = await Category("Dairy", ExpenseLine.Controllable);
        var utilities = await Category("Utilities", ExpenseLine.Controllable);
        var rent = await Category("Rent", ExpenseLine.Occupation);
        var insurance = await Category("Insurance", ExpenseLine.OtherFixed);
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), " utilities ")));

        var milk = await node.Send(new CreateRawMaterialCommand("Milk", UnitOfMeasure.Millilitre, CategoryId: dairy, PurchaseUnit: "L", PurchaseUnitSize: 1000));
        Assert.Equal(("Dairy", dairy), (milk.Category, milk.CategoryId));
        // No category given: General.
        Assert.Equal("General", (await node.Send(new CreateRawMaterialCommand("Salt", UnitOfMeasure.Gram))).Category);

        async Task<Guid> Expense(string name, Guid category, string? unit = null) =>
            (await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), name, category, null, unit))).Id;
        var electricity = await Expense("Electricity", utilities);
        var spray = await Expense("Cleaning spray", utilities, "bottle");
        var shop = await Expense("Shop rent", rent);
        var cover = await Expense("Fire insurance", insurance);

        // Never stock: not on the stock list, and no recipe can use one.
        Assert.DoesNotContain(await node.Send(new GetRawMaterialsQuery()), m => m.Name == "Electricity");
        Assert.DoesNotContain(await node.Service<CostingService>(scope).GetMaterialsAsync(ct), m => m.Name == "Electricity");
        await Assert.ThrowsAsync<NotFoundException>(() => node.Send(new SetRecipeCommand(
            RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(electricity, 1)])));

        // One invoice, both kinds: 10 L of milk for 300, the month's electricity, four bottles of spray.
        var bill = Guid.NewGuid();
        PurchaseInvoiceLineEntry Spend(Guid item, decimal amount, decimal quantity = 1, string? unit = null) =>
            new(null, quantity, unit, 1, amount, PurchaseItemType.Expense, item);
        var draft = await node.Send(new SavePurchaseInvoiceCommand(bill, null, null, today,
            [new(milk.Id, 10, "L", 1000, 300), Spend(electricity, 1_500), Spend(spray, 200, 4, "bottle")], PaymentMethod: PurchasePaymentMethod.Safe));
        Assert.Equal((300m, 1_700m, 2_000m), (draft.InventoryTotal, draft.ExpenseTotal, draft.Total));
        Assert.Equal([("Milk", PurchaseItemType.Inventory, "Dairy"), ("Electricity", PurchaseItemType.Expense, "Utilities"), ("Cleaning spray", PurchaseItemType.Expense, "Utilities")],
            draft.Lines.Select(l => (l.Material, l.ItemType, l.Category)));
        // An expense is its amount, and is one thing or the other.
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePurchaseInvoiceCommand(bill, null, null, today,
            [new(null, 1, null, 1, null, PurchaseItemType.Expense, electricity)], PaymentMethod: PurchasePaymentMethod.Safe)));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePurchaseInvoiceCommand(bill, null, null, today,
            [new(milk.Id, 1, null, 1, 50, PurchaseItemType.Expense, electricity)], PaymentMethod: PurchasePaymentMethod.Safe)));

        await node.Send(new PostPurchaseInvoiceCommand(bill, "Omar"));
        // The milk is in, at 0.03 a ml; the expenses moved nothing.
        var stocked = await node.Db(db => db.RawMaterials.SingleAsync(m => m.Id == milk.Id));
        Assert.Equal((10_000m, (decimal?)0.03m), (stocked.QuantityOnHand, stocked.AverageCost));
        Assert.Equal([milk.Id], await node.Db(db => db.StockMovements.Where(m => m.SourceId == bill).Select(m => m.RawMaterialId).ToListAsync()));

        // Rent and insurance on an invoice of their own; a draft and last month's bill do not count this month.
        var overheads = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(overheads, null, null, today, [Spend(shop, 8_000), Spend(cover, 600)], PaymentMethod: PurchasePaymentMethod.Safe));
        await node.Send(new PostPurchaseInvoiceCommand(overheads, "Omar"));
        await node.Send(new SavePurchaseInvoiceCommand(Guid.NewGuid(), null, null, today, [Spend(electricity, 999)], PaymentMethod: PurchasePaymentMethod.Safe));
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        var earlier = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(earlier, null, null, lastMonth, [Spend(electricity, 1_400)], PaymentMethod: PurchasePaymentMethod.Safe));
        await node.Send(new PostPurchaseInvoiceCommand(earlier, "Omar"));

        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var spending = node.Service<PurchaseSpendingService>(scope);
        var report = await spending.GetAsync(firstOfMonth, today, ct);
        Assert.Equal((10_600m, 300m, 10_300m, 2, 0), (report.Total, report.Inventory, report.Expenses, report.Invoices, report.UnpricedLines));
        Assert.Equal(
            [("Rent", 0m, 8_000m, (decimal?)75.5m), ("Utilities", 0m, 1_700m, 16.0m), ("Insurance", 0m, 600m, 5.7m), ("Dairy", 300m, 0m, 2.8m)],
            report.Categories.Select(c => (c.Category, c.Inventory, c.Expenses, c.SharePercent)));
        Assert.Equal([("Electricity", 1m, 1_500m), ("Cleaning spray", 4m, 200m)],
            report.Categories.Single(c => c.Category == "Utilities").Items.Select(i => (i.Name, i.Quantity, i.Total)));
        Assert.Equal((10m, "L"), report.Categories.Single(c => c.Category == "Dairy").Items.Select(i => (i.Quantity, i.Unit)).Single());
        Assert.Equal(12_000m, (await spending.GetAsync(lastMonth, today, ct)).Total);

        // The month's P&L: 1,700 controllable, 8,000 occupation, 600 other fixed, from the invoices.
        var kpis = node.Service<KpiService>(scope);
        var s = (await kpis.GetAsync(today.Year, today.Month, ct)).Statement;
        Assert.Equal((1_700m, 8_000m, 600m), (s.LoggedControllableCosts, s.LoggedOccupationCost, s.LoggedOtherFixedCosts));
        Assert.Equal(["Rent", "Utilities", "Insurance"], s.LoggedExpenses!.Select(e => e.Category));
        // No net profit until the wages are in; then everything comes off, typed and logged.
        Assert.Null(s.NetProfit);
        await kpis.SaveExpensesAsync(today.Year, today.Month, new MonthlyExpensesDto(5_000, 100, 300, null, null, null), "Omar", ct);
        s = (await kpis.GetAsync(today.Year, today.Month, ct)).Statement;
        Assert.Equal((15_600m, s.GrossProfit - 15_600m), (s.TotalExpenses, s.NetProfit!.Value));

        // A year of it: wages, typed and logged controllable costs; logged rent; logged insurance.
        var sheet = await node.Service<BreakEvenService>(scope).GetAsync(today.Year, today.Month, today.Year, today.Month, ct);
        Assert.Equal((84_000m, 20_400m, 96_000m, 96_000m, 7_200m, 187_200m),
            (sheet.ControllableCosts, sheet.ControllableFromInvoices, sheet.OccupationCost, sheet.OccupationFromInvoices, sheet.OtherFixedCosts, sheet.TotalFixedCosts));

        // On invoices, an expense is archived rather than deleted, and not put on another until restored.
        Assert.Equal(ExpenseItemRemoval.Archived, (await node.Send(new DeleteExpenseItemCommand(electricity))).Outcome);
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePurchaseInvoiceCommand(Guid.NewGuid(), null, null, today, [Spend(electricity, 10)], PaymentMethod: PurchasePaymentMethod.Safe)));
        Assert.DoesNotContain(await node.Send(new GetExpenseItemsQuery()), e => e.Id == electricity);
        // On three invoices, the draft too; 1,500 and 1,400 posted.
        var archived = (await node.Send(new GetExpenseItemsQuery(IncludeArchived: true))).Single(e => e.Id == electricity);
        Assert.Equal((true, 3, 2_900m), (archived.IsArchived, archived.Invoices, archived.PostedTotal));
        await node.Send(new RestoreExpenseItemCommand(electricity));

        // A category goes only once what is in it has somewhere to go; General never goes. The
        // insurance moves to Rent, and its history with it.
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new DeletePurchaseCategoryCommand(insurance)));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new DeletePurchaseCategoryCommand(PurchaseCategory.GeneralId, rent)));
        await node.Send(new DeletePurchaseCategoryCommand(insurance, rent));
        s = (await node.Service<KpiService>(node.Scope()).GetAsync(today.Year, today.Month, ct)).Statement;
        Assert.Equal((8_600m, 0m), (s.LoggedOccupationCost, s.LoggedOtherFixedCosts));
    }

    // Maker-checker deletion across the five modules. An accountant's delete is a request: the
    // record still counts and is locked. A manager approves (a recipe deleted, a material
    // archived, a posted invoice, waste entry or count voided by reversing entries dated now),
    // rejects (it stays as it was), or deletes outright. Every decision is kept.
    [Fact]
    public async Task Deletions_wait_for_a_manager_and_voids_reverse_the_stock()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var today = node.Service<RestaurantClock>(scope).BusinessDateOf(DateTime.UtcNow);
        Task<decimal> OnHand(Guid id) => node.Db(db => db.RawMaterials.Where(m => m.Id == id).Select(m => m.QuantityOnHand).SingleAsync());
        Task<ApprovalResultDto> Ask(ApprovalModule module, Guid id, bool manager = false, string? reason = null) =>
            node.Send(new RequestDeletionCommand(Guid.NewGuid(), module, id, reason, manager ? "staff-omar" : "staff-mona",
                manager ? "Omar" : "Mona", CanApprove: manager));

        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var salt = (await node.Send(new CreateRawMaterialCommand("Salt", UnitOfMeasure.Gram))).Id;
        async Task<Guid> Invoice(decimal kg, decimal cost)
        {
            var id = Guid.NewGuid();
            await node.Send(new SavePurchaseInvoiceCommand(id, null, $"F-{kg}", today, [new(tomatoes, kg, "kg", 1000, cost)], PaymentMethod: PurchasePaymentMethod.Safe));
            await node.Send(new PostPurchaseInvoiceCommand(id, "Mona"));
            return id;
        }
        var first = await Invoice(5, 100);
        var second = await Invoice(5, 150);
        var waste = Guid.NewGuid();
        await node.Send(new RecordSpoilageCommand(waste, [new(tomatoes, 1_000, "Expired")], "Mona"));
        var count = Guid.NewGuid();
        await node.Send(new RecordStockCountCommand(count, [new(tomatoes, 8_900)], "Mona"));
        var categoryId = (await node.Send(new SaveRecipeCategoryCommand(Guid.NewGuid(), "Sauces"))).Id;
        var sauce = (await node.Send(new SaveRecipeCommand(null, "House sauce", null, null, null, 1, [new(tomatoes, 200)], categoryId))).Id;
        var draft = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(draft, null, null, today, [new(tomatoes, 1, "kg", 1000, 20)], PaymentMethod: PurchasePaymentMethod.Safe));
        Assert.Equal(8_900m, await OnHand(tomatoes));

        // The accountant asks: nothing is deleted, and the stock is as it was.
        foreach (var (module, id) in new[] { (ApprovalModule.Recipe, sauce), (ApprovalModule.PurchaseInvoice, second),
                     (ApprovalModule.Waste, waste), (ApprovalModule.StockCount, count), (ApprovalModule.RawMaterial, salt) })
            Assert.Equal(ApprovalOutcome.Requested, (await Ask(module, id, reason: "Entered by mistake")).Outcome);
        Assert.Equal(8_900m, await OnHand(tomatoes));
        Assert.False(await node.Db(db => db.Recipes.Where(r => r.Id == sauce).Select(r => r.DeletedAtUtc != null).SingleAsync()));

        // Asked about, a record is locked until decided; and is not asked about twice.
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new SaveRecipeCommand(sauce, "Sauce", null, null, null, 1, [new(tomatoes, 250)], categoryId)));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new UpdateRawMaterialCommand(salt, "Sea salt", 0)));
        await Assert.ThrowsAsync<DomainException>(() => Ask(ApprovalModule.Recipe, sauce));
        // What cannot be deleted is refused when asked: a material in a recipe and on the shelf, a draft.
        await Assert.ThrowsAsync<DomainException>(() => Ask(ApprovalModule.RawMaterial, tomatoes));
        await Assert.ThrowsAsync<DomainException>(() => Ask(ApprovalModule.PurchaseInvoice, draft));

        var pending = (await node.Send(new GetApprovalRequestsQuery())).Pending;
        Assert.Equal(
            [(ApprovalModule.Recipe, "Recipe House sauce"),
             (ApprovalModule.PurchaseInvoice, $"Invoice F-5 · {today:d MMM yyyy} · 1 line · L.E 150.00"),
             (ApprovalModule.Waste, $"Waste of {today:d MMM yyyy} by Mona: Tomatoes 1 kg (Expired) · L.E 25.00"),
             (ApprovalModule.StockCount, $"Stock count posted {today:d MMM yyyy} by Mona · 1 material"),
             (ApprovalModule.RawMaterial, "Raw material Salt")],
            pending.Select(p => (p.Module, p.Summary)));
        Assert.All(pending, p => Assert.Equal(("Mona", "Entered by mistake"), (p.RequestedBy, p.Reason)));
        var request = pending.ToDictionary(p => p.Module, p => p.Id);

        // The manager decides.
        await node.Send(new ApproveRequestCommand(request[ApprovalModule.Recipe], "Omar"));
        Assert.True(await node.Db(db => db.Recipes.Where(r => r.Id == sauce).Select(r => r.DeletedAtUtc != null).SingleAsync()));

        // The second delivery goes back out at its own price; the invoice is kept, voided.
        await node.Send(new ApproveRequestCommand(request[ApprovalModule.PurchaseInvoice], "Omar"));
        Assert.Equal(3_900m, await OnHand(tomatoes));
        var voided = await node.Db(db => db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == second));
        Assert.Equal((PurchaseInvoiceStatus.Voided, "Omar"), (voided.Status, voided.VoidedBy));
        Assert.Equal(100m, (await node.Service<PurchaseSpendingService>(scope).GetAsync(today, today, ct)).Total);
        // The last price is the delivery that stands (20 a kg), not the voided one, nor its reversal.
        Assert.Equal(20m, (await node.Service<CostingService>(scope).GetMaterialsAsync(ct)).Single(m => m.Id == tomatoes).LastPurchaseCostPerPurchaseUnit);

        await node.Send(new RejectRequestCommand(request[ApprovalModule.Waste], "It was spoiled", "Omar"));
        Assert.Single(await node.Service<VarianceService>(scope).GetSpoilageAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), ct));

        // The count's 100 g shortfall is put back, and it is no longer a count to measure from.
        await node.Send(new ApproveRequestCommand(request[ApprovalModule.StockCount], "Omar"));
        Assert.Equal(4_000m, await OnHand(tomatoes));
        await Assert.ThrowsAsync<DomainException>(() => node.Service<VarianceService>(scope).GetVarianceAsync(null, null, ct));

        await node.Send(new ApproveRequestCommand(request[ApprovalModule.RawMaterial], "Omar"));
        Assert.DoesNotContain(await node.Send(new GetRawMaterialsQuery()), m => m.Id == salt);
        // Approving again after a lost answer changes nothing.
        await node.Send(new ApproveRequestCommand(request[ApprovalModule.StockCount], "Omar"));
        Assert.Equal(4_000m, await OnHand(tomatoes));

        // A manager's own delete is done at once, and recorded as theirs.
        var direct = await Ask(ApprovalModule.Waste, waste, manager: true);
        Assert.Equal((ApprovalOutcome.Applied, ApprovalStatus.Approved, "Omar"), (direct.Outcome, direct.Request.Status, direct.Request.DecidedBy));
        Assert.Equal(5_000m, await OnHand(tomatoes));
        Assert.Empty(await node.Service<VarianceService>(scope).GetSpoilageAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), ct));
        await Assert.ThrowsAsync<DomainException>(() => Ask(ApprovalModule.Waste, waste, manager: true));

        var history = await node.Send(new GetApprovalRequestsQuery(Decided: 10));
        Assert.Empty(history.Pending);
        Assert.Equal(
            [ApprovalStatus.Approved, ApprovalStatus.Approved, ApprovalStatus.Approved, ApprovalStatus.Rejected,
             ApprovalStatus.Approved, ApprovalStatus.Approved],
            history.Decided.Select(d => d.Status));
        // Each void stands beside what it voided in the ledger: 1 purchase, 1 waste and 1 count reversed.
        Assert.Equal(3, await node.Db(db => db.StockMovements.CountAsync(m => m.ReversalOf != null)));
        _ = first;
    }

    // Prep items and batches by an accountant wait for a manager. A new prep item does not exist
    // until approved; a batch moves nothing until approved, and then at the averages of that
    // moment; a correction reverses the batch and makes it again; a deletion voids it.
    [Fact]
    public async Task Prep_items_and_batches_by_an_accountant_wait_for_a_manager()
    {
        await using var node = await TestNode.StartAsync();
        Task<decimal> OnHand(Guid id) => node.Db(db => db.RawMaterials.Where(m => m.Id == id).Select(m => m.QuantityOnHand).SingleAsync());
        Task<ApprovalRequestDto> Approve(ApprovalResultDto asked) => node.Send(new ApproveRequestCommand(asked.Request.Id, "Omar"));
        var flour = (await node.Send(new CreateRawMaterialCommand("Flour", UnitOfMeasure.Gram))).Id;
        var yeast = (await node.Send(new CreateRawMaterialCommand("Yeast", UnitOfMeasure.Gram))).Id;
        // 0.02 a gram of flour, 0.50 a gram of yeast.
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(flour, 20_000, 400), new(yeast, 500, 250)]));

        // A new prep item, asked for: nothing exists until it is approved.
        var doughChange = new PrepItemChange("Pizza dough", UnitOfMeasure.Gram, 10_000, [new(flour, 6_000), new(yeast, 100)], Code: "PREP-1");
        var newItem = await node.Send(new RequestPrepItemChangeCommand(Guid.NewGuid(), null, doughChange, "For the pizzas", "staff-mona", "Mona"));
        Assert.Equal((ApprovalOutcome.Requested, ApprovalAction.Create, "New prep item PREP-1 · Pizza dough · 10 kg batch · 2 ingredients"),
            (newItem.Outcome, newItem.Request.Action, newItem.Request.Summary));
        Assert.Contains(new ApprovalChange("Flour", null, "6 kg"), newItem.Request.Changes);
        Assert.Contains(new ApprovalChange("Yeast", null, "100 g"), newItem.Request.Changes);
        Assert.Empty(await node.Send(new GetPrepItemsQuery()));
        Assert.DoesNotContain(await node.Send(new GetRawMaterialsQuery()), m => m.Name == "Pizza dough");
        // What could not be saved is refused when asked, not when approved.
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new RequestPrepItemChangeCommand(
            Guid.NewGuid(), null, doughChange with { BatchQuantity = 0 }, null, "staff-mona", "Mona")));

        await Approve(newItem);
        var dough = newItem.Request.RecordId;
        Assert.Equal(("Pizza dough", 2), await node.Send(new GetPrepItemQuery(dough)) is { } made0 ? (made0.Name, made0.Ingredients.Count) : default);

        // A batch, asked for: no flour out, no dough in.
        var orderId = Guid.NewGuid();
        Task<ApprovalResultDto> AskBatch() => node.Send(new RequestProductionOrderCommand(
            Guid.NewGuid(), orderId, dough, 10_000, 9_600, "Morning batch", null, "staff-mona", "Mona"));
        var batch = await AskBatch();
        Assert.Equal(batch.Request.Id, (await AskBatch()).Request.Id);
        Assert.Equal((20_000m, 500m, 0m), (await OnHand(flour), await OnHand(yeast), await OnHand(dough)));
        Assert.Empty(await node.Send(new GetProductionOrdersQuery()));
        Assert.Contains(new ApprovalChange("Quantity to produce", null, "10 kg"), batch.Request.Changes);

        // Flour costs more by the time the manager approves: the batch is costed then, at 0.025 a
        // gram, as the preview shows.
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(flour, 20_000, 600)]));
        var preview = (await node.Send(new GetApprovalPreviewQuery(batch.Request.Id)))!;
        Assert.Equal((200m, 20.83m, (ProductionOrderDto?)null), (preview.Batch.TotalCost, preview.Batch.UnitCostPerDisplayUnit, preview.Replaces));
        await Approve(batch);
        var made = (await node.Send(new GetProductionOrderQuery(orderId)))!;
        Assert.Equal((200m, "Mona", "Omar", ProductionOrderStatus.Made), (made.TotalCost, made.ProducedBy, made.ApprovedBy, made.Status));
        Assert.Equal((34_000m, 400m, 9_600m), (await OnHand(flour), await OnHand(yeast), await OnHand(dough)));

        // A correction, asked for: 12 kg planned, 11.5 kg made. Nothing moves; the batch is locked.
        var corrected = Guid.NewGuid();
        var correction = await node.Send(new ReviseProductionOrderCommand(
            Guid.NewGuid(), orderId, corrected, 12_000, 11_500, "Morning batch", "Weighed again", "staff-mona", "Mona", CanApprove: false));
        Assert.Equal(ApprovalOutcome.Requested, correction.Outcome);
        Assert.Equal(
            [new("Quantity to produce", "10 kg", "12 kg"), new("Came out", "9.6 kg", "11.5 kg"),
             new("Flour taken", "6 kg", "7.2 kg"), new("Yeast taken", "100 g", "120 g")],
            correction.Request.Changes);
        Assert.Equal((34_000m, 9_600m), (await OnHand(flour), await OnHand(dough)));
        // What the manager sees: the batch made again, and the one it replaces, going back first.
        var correctionPreview = (await node.Send(new GetApprovalPreviewQuery(correction.Request.Id)))!;
        Assert.Equal((7_200m, 6_000m, 200m), (correctionPreview.Batch.Lines.Single(l => l.Material == "Flour").Quantity,
            correctionPreview.Replaces!.Lines.Single(l => l.Material == "Flour").Quantity, correctionPreview.Replaces.TotalCost));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new RequestDeletionCommand(
            Guid.NewGuid(), ApprovalModule.ProductionOrder, orderId, null, "staff-mona", "Mona", CanApprove: false)));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new ReviseProductionOrderCommand(
            Guid.NewGuid(), orderId, Guid.NewGuid(), 11_000, 11_000, null, null, "staff-omar", "Omar", CanApprove: true)));

        // Approved: the batch reversed (its flour and yeast back, its dough out) and made again.
        await Approve(correction);
        var replaced = (await node.Send(new GetProductionOrderQuery(orderId)))!;
        var remade = (await node.Send(new GetProductionOrderQuery(corrected)))!;
        Assert.Equal((ProductionOrderStatus.Voided, corrected, "Omar"), (replaced.Status, replaced.ReplacedBy, replaced.VoidedBy));
        Assert.Equal((orderId, 240m, "Mona", "Omar"), (remade.RevisionOf, remade.TotalCost, remade.ProducedBy, remade.ApprovedBy));
        Assert.Equal((32_800m, 380m, 11_500m), (await OnHand(flour), await OnHand(yeast), await OnHand(dough)));
        var profile = (await node.Send(new GetPrepItemQuery(dough)))!;
        Assert.Equal((20.87m, 20.87m), (profile.AverageCostPerDisplayUnit, profile.LastBatchCostPerDisplayUnit));

        // A change to its recipe, asked for and rejected: the recipe stays, and can be changed again.
        var recipeChange = await node.Send(new RequestPrepItemChangeCommand(
            Guid.NewGuid(), dough, doughChange with { Ingredients = [new(flour, 6_000), new(yeast, 150)] }, null, "staff-mona", "Mona"));
        Assert.Equal([new ApprovalChange("Yeast", "100 g", "150 g")], recipeChange.Request.Changes);
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePrepItemCommand(
            dough, "Pizza dough", UnitOfMeasure.Gram, 10_000, [new(flour, 6_000)])));
        await node.Send(new RejectRequestCommand(recipeChange.Request.Id, "Keep it as it is", "Omar"));
        Assert.Equal(100m, (await node.Send(new GetPrepItemQuery(dough)))!.Ingredients.Single(i => i.Name == "Yeast").Quantity);

        // The corrected batch, deleted: voided, and everything back where it was before it.
        var deletion = await node.Send(new RequestDeletionCommand(
            Guid.NewGuid(), ApprovalModule.ProductionOrder, corrected, "Never made", "staff-mona", "Mona", CanApprove: false));
        Assert.StartsWith("Batch of Pizza dough made ", deletion.Request.Summary);
        await Approve(deletion);
        Assert.Equal((40_000m, 500m, 0m), (await OnHand(flour), await OnHand(yeast), await OnHand(dough)));
        Assert.All(await node.Send(new GetProductionOrdersQuery()), o => Assert.Equal(ProductionOrderStatus.Voided, o.Status));
        // Two batches of three entries each, every one reversed.
        Assert.Equal(6, await node.Db(db => db.StockMovements.CountAsync(m => m.ReversalOf != null)));
        Assert.Equal(5, (await node.Send(new GetApprovalRequestsQuery(Decided: 10))).Decided.Count);
    }

    // The treasury. The safe opens, is funded, and takes a day's cash; an invoice is paid from the
    // takings, another bought on credit and paid by a voucher an accountant asks for; expense
    // vouchers reach the profit and loss; and every void puts the cash back on the day it is made.
    [Fact]
    public async Task The_safe_and_the_suppliers_accounts_follow_every_payment()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var treasury = node.Service<TreasuryService>(scope);
        var today = treasury.Today;
        Task<SafeVoucherResultDto> Voucher(SafeVoucher voucher, bool manager = true) => node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), voucher, null, manager ? "staff-omar" : "staff-mona", manager ? "Omar" : "Mona", manager));
        SafeVoucher New(SafeTransactionKind kind, decimal amount, FundingSource? source = null, DateOnly? takings = null, Guid? supplier = null, Guid? expense = null) =>
            new(Guid.NewGuid(), kind, amount, today, source, takings, supplier, expense);
        async Task<decimal> Safe() => (await treasury.GetSafeAsync(today.Year, today.Month, ct)).BalanceNow;
        async Task<DayRevenue> Takings() => (await treasury.GetDayRevenueAsync(today, today, ct)).Days.Single();

        // Opened with 10,000 carried over, once; then 5,000 of the owners' money.
        await Voucher(New(SafeTransactionKind.OpeningBalance, 10_000, FundingSource.CarriedOver));
        await Assert.ThrowsAsync<DomainException>(() => Voucher(New(SafeTransactionKind.OpeningBalance, 1, FundingSource.CarriedOver)));
        await Voucher(New(SafeTransactionKind.Funding, 5_000, FundingSource.OwnerFinancing));
        Assert.Equal(15_000m, await Safe());

        // The day's cash: a table paid in cash.
        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "T1"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger, 4));
        await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        var cash = (await Takings()).CashSales;
        Assert.True(cash > 0);

        // Tomatoes paid straight from the day's takings: the safe does not move.
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var market = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(market, null, null, today, [new(tomatoes, 5, "kg", 1000, 100)], "Omar",
            PurchasePaymentMethod.DayRevenue, today));
        Assert.Equal(InvoicePaymentStatus.Paid, (await node.Send(new PostPurchaseInvoiceCommand(market, "Omar"))).PaymentStatus);
        Assert.Equal((100m, 15_000m), ((await Takings()).PaidOut, await Safe()));

        // The day's takings entered gross, 10 short of what the till took: the 100 paid out of them
        // stays out, the rest goes into the safe.
        await Voucher(New(SafeTransactionKind.RevenueTransfer, cash - 10, takings: today));
        Assert.Equal((cash - 10, cash - 110, 10m), ((await Takings()).Entered, (await Takings()).Transferred, (await Takings()).Left));
        Assert.Equal(15_000m + cash - 110, await Safe());

        // Nile Farms: owed 500 from before, then 2,500 on credit, which needs the supplier.
        var nileFarms = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms"))).Id;
        await node.Send(new SetSupplierOpeningBalanceCommand(Guid.NewGuid(), nileFarms, 500, today, "Omar"));
        var credit = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(credit, null, "F-9", today, [new(tomatoes, 10, "kg", 1000, 2_500)], "Omar", PurchasePaymentMethod.Credit));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new PostPurchaseInvoiceCommand(credit, "Omar")));
        await node.Send(new SavePurchaseInvoiceCommand(credit, nileFarms, "F-9", today, [new(tomatoes, 10, "kg", 1000, 2_500)], "Omar", PurchasePaymentMethod.Credit));
        Assert.Equal(InvoicePaymentStatus.OnCredit, (await node.Send(new PostPurchaseInvoiceCommand(credit, "Omar"))).PaymentStatus);
        var safeBefore = await Safe();

        // The accountant's payment voucher: asked, nothing moves; more than is owed is refused.
        await Assert.ThrowsAsync<DomainException>(() => Voucher(New(SafeTransactionKind.SupplierPayment, 3_001, supplier: nileFarms), manager: false));
        var asked = await Voucher(New(SafeTransactionKind.SupplierPayment, 1_000, supplier: nileFarms), manager: false);
        Assert.Equal(ApprovalOutcome.Requested, asked.Outcome);
        Assert.Contains(new ApprovalChange("Owed to Nile Farms", "L.E 3,000.00", "L.E 2,000.00"), asked.Request!.Changes);
        Assert.Equal((safeBefore, 3_000m), (await Safe(), (await treasury.GetSupplierStatementAsync(nileFarms, ct))!.Balance));

        // Approved: out of the safe, off the debt, set against the opening debt first.
        await node.Send(new ApproveRequestCommand(asked.Request.Id, "Omar"));
        var statement = (await treasury.GetSupplierStatementAsync(nileFarms, ct))!;
        Assert.Equal((safeBefore - 1_000, 500m, 2_500m, 1_000m, 2_000m),
            (await Safe(), statement.OpeningDebt, statement.CreditPurchases, statement.Payments, statement.Balance));
        Assert.Equal((2_500m, 500m, 2_000m, OpenInvoiceStatus.PartlyPaid),
            statement.OpenInvoices.Select(i => (i.Total, i.Paid, i.Remaining, i.Status)).Single());
        Assert.Equal(["Owed before the treasury", "Invoice F-9 on credit", "Payment voucher"], statement.Lines.Select(l => l.Description));
        var payment = Assert.Single(await treasury.GetSupplierPaymentsAsync(today, today, ct));
        Assert.Equal(("Nile Farms", 1_000m, "Mona", "Omar"), (payment.Supplier, payment.Amount, payment.RecordedBy, payment.ApprovedBy));

        // Expense vouchers: the electricity bill and the wages, on the profit and loss.
        async Task<Guid> Category(string name, ExpenseLine line) => (await node.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), name, null, line))).Id;
        var electricity = (await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), "Electricity", await Category("Utilities", ExpenseLine.Controllable), null, null))).Id;
        var salaries = (await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), "Salaries", await Category("Staff", ExpenseLine.Wages), null, null))).Id;
        var bill = (await Voucher(New(SafeTransactionKind.ExpenseVoucher, 1_250, expense: electricity))).Transaction!;
        await Voucher(New(SafeTransactionKind.ExpenseVoucher, 3_000, expense: salaries));
        Assert.Equal(safeBefore - 5_250, await Safe());
        var spending = await node.Service<PurchaseSpendingService>(scope).GetAsync(today, today, ct);
        Assert.Equal((4_250m, 2), (spending.Expenses, spending.Vouchers));
        var s = (await node.Service<KpiService>(scope).GetAsync(today.Year, today.Month, ct)).Statement;
        Assert.Equal((1_250m, 3_000m), (s.LoggedControllableCosts, s.LoggedWages));
        // Wages paid count as wages: the net profit is there without typing them in.
        Assert.NotNull(s.NetProfit);

        // The bill voided: its cash back today, and out of the reports.
        await node.Send(new RequestDeletionCommand(Guid.NewGuid(), ApprovalModule.Treasury, bill.Id, "Paid twice", "staff-omar", "Omar", CanApprove: true));
        Assert.Equal(safeBefore - 4_000, await Safe());
        Assert.Equal(3_000m, (await node.Service<PurchaseSpendingService>(scope).GetAsync(today, today, ct)).Expenses);
        Assert.Equal(1, (await treasury.GetExpenseVouchersAsync(today, today, ct)).Vouchers.Count(v => v.VoidedAtUtc is not null));

        // The accountant's cash invoice: its stock is in, its payment waits for approval.
        var urgent = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(urgent, null, null, today, [new(tomatoes, 1, "kg", 1000, 40)], "Mona", PurchasePaymentMethod.Safe));
        Assert.Equal(InvoicePaymentStatus.Pending,
            (await node.Send(new PostPurchaseInvoiceCommand(urgent, "Mona", "staff-mona", CanApprove: false))).PaymentStatus);
        Assert.Equal(safeBefore - 4_000, await Safe());
        var waiting = (await node.Send(new GetApprovalRequestsQuery())).Pending.Single(r => r.RecordId == urgent);
        // Rejected, it stays unpaid, and is paid again another way.
        await node.Send(new RejectRequestCommand(waiting.Id, "Not from the safe", "Omar"));
        Assert.Equal(InvoicePaymentStatus.Unpaid, (await node.Send(new GetPurchaseInvoiceQuery(urgent)))!.PaymentStatus);
        Assert.Equal(InvoicePaymentStatus.Paid,
            await node.Send(new PayPurchaseInvoiceCommand(Guid.NewGuid(), urgent, PurchasePaymentMethod.DayRevenue, today, "staff-omar", "Omar", CanApprove: true)));
        Assert.Equal(140m, (await Takings()).PaidOut);

        // Voiding the credit invoice takes it off Nile Farms' account; voiding the cash one puts
        // its cash back into the day's takings.
        await node.Send(new RequestDeletionCommand(Guid.NewGuid(), ApprovalModule.PurchaseInvoice, credit, null, "staff-omar", "Omar", CanApprove: true));
        await node.Send(new RequestDeletionCommand(Guid.NewGuid(), ApprovalModule.PurchaseInvoice, urgent, null, "staff-omar", "Omar", CanApprove: true));
        Assert.Equal(-500m, (await treasury.GetSupplierStatementAsync(nileFarms, ct))!.Balance);
        Assert.Equal(100m, (await Takings()).PaidOut);

        // The month: opened by its own opening entry, carried over; what came in and went out.
        var month = await treasury.GetSafeAsync(today.Year, today.Month, ct);
        Assert.Equal((0m, SafeOpening.OpeningEntry, (FundingSource?)FundingSource.CarriedOver), (month.OpeningBalance, month.OpeningKind, month.OpeningSource));
        Assert.Equal(month.ClosingBalance, month.BalanceNow);
        Assert.Equal(month.In - month.Out, month.ClosingBalance);
        // Voided, with their voids, in neither In nor Out but in what was voided: the electricity
        // bill, and the accountant's 40 of tomatoes, paid out of the day's takings once they were in
        // the safe.
        Assert.Equal((15_000m + cash - 110, 4_000m, 0m, 1_290m), (month.In, month.Out, month.VoidedIn, month.VoidedOut));
        Assert.Equal("Opening balance · carried over", month.Entries[0].Description);
        var funding = await treasury.GetFundingAsync(today, today, ct);
        Assert.Equal((10_000m, 5_000m, cash - 110), (funding.CarriedOver, funding.OwnerFinancing, funding.Revenue));
        // The days before the treasury opened are in its opening balance, not still to be moved in.
        Assert.Equal([today], (await treasury.GetDayRevenueAsync(today.AddDays(-7), today, ct)).Days.Select(d => d.Date));

        // Money put in last month and voided now: last month closed with it, so it stays in that
        // month's In, and its void is this month's, in Out.
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        var late = (await Voucher(New(SafeTransactionKind.Funding, 700, FundingSource.OwnerFinancing) with { Date = lastMonth })).Transaction!;
        await node.Send(new RequestDeletionCommand(Guid.NewGuid(), ApprovalModule.Treasury, late.Id, "Never came in", "staff-omar", "Omar", CanApprove: true));
        var before = await treasury.GetSafeAsync(lastMonth.Year, lastMonth.Month, ct);
        Assert.Equal((700m, 0m, 0m, 700m), (before.In, before.Out, before.VoidedIn, before.ClosingBalance));
        var after = await treasury.GetSafeAsync(today.Year, today.Month, ct);
        Assert.Equal((700m, month.In, month.Out + 700, 0m, 1_290m), (after.OpeningBalance, after.In, after.Out, after.VoidedIn, after.VoidedOut));
        Assert.Equal((month.ClosingBalance, after.ClosingBalance), (after.OpeningBalance + after.In - after.Out, after.BalanceNow));
    }

    // A day's takings are entered gross (توريد إيراد), as the POS totals them, once. What was paid
    // straight out of them before came out of the drawer: the safe gets the rest. Paid out of them,
    // or put back by a void, once they are in, moves the safe. Either way the safe ends with the
    // gross less what the day's takings paid, and the daily cash, the reconciliation and the
    // day's cash say so (يومية الإيراد).
    [Fact]
    public async Task A_days_takings_are_entered_gross_and_the_safe_gets_them_less_what_was_paid_out_of_them()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var treasury = node.Service<TreasuryService>(scope);
        var today = treasury.Today;
        var (yesterday, before) = (today.AddDays(-1), today.AddDays(-2));
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var onions = (await node.Send(new CreateRawMaterialCommand("Onions", UnitOfMeasure.Gram))).Id;
        var nileFarms = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms"))).Id;
        async Task<Guid> PaidFromTakings(Guid? supplier, string? number, DateOnly takings, IReadOnlyList<PurchaseInvoiceLineEntry> lines)
        {
            var id = Guid.NewGuid();
            await node.Send(new SavePurchaseInvoiceCommand(id, supplier, number, today, lines, "Omar", PurchasePaymentMethod.DayRevenue, takings));
            Assert.Equal(InvoicePaymentStatus.Paid, (await node.Send(new PostPurchaseInvoiceCommand(id, "Omar"))).PaymentStatus);
            return id;
        }
        Task<SafeVoucherResultDto> Takings(decimal gross, DateOnly day) => node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), SafeTransactionKind.RevenueTransfer, gross, today, RevenueDate: day), null,
            "staff-omar", "Omar", true));
        Task Void(ApprovalModule module, Guid id) =>
            node.Send(new RequestDeletionCommand(Guid.NewGuid(), module, id, "Entered by mistake", "staff-omar", "Omar", CanApprove: true));
        async Task<decimal> Safe() => (await treasury.GetSafeAsync(today.Year, today.Month, ct)).BalanceNow;

        // Through the day, the drawer pays Nile Farms and the market; one invoice is voided. The
        // safe does not move.
        var nile = await PaidFromTakings(nileFarms, "F-1", today, [new(tomatoes, 5, "kg", 1000, 100), new(onions, 10, "kg", 1000, 300)]);
        var market = await PaidFromTakings(null, null, today, [new(tomatoes, 1, "kg", 1000, 50)]);
        await Void(ApprovalModule.PurchaseInvoice, await PaidFromTakings(null, null, today, [new(onions, 1, "kg", 1000, 999)]));
        Assert.Equal(0m, await Safe());
        Assert.Equal(new TakingsPreview(today, null, 450m, 2, 0), await treasury.GetTakingsPreviewAsync(today, ct));

        // The POS says 10,495: into the safe go 10,045. Once a day.
        var entered = (await Takings(10_495, today)).Transaction!;
        Assert.Equal((10_495m, 10_045m), (entered.GrossAmount, entered.Amount));
        Assert.Equal(10_045m, await Safe());
        Assert.Contains("entered already", (await Assert.ThrowsAsync<DomainException>(() => Takings(1, today))).Message);
        Assert.Equal(10_495m, (await treasury.GetTakingsPreviewAsync(today, ct)).Entered);

        // Once they are in: another invoice paid out of them takes from the safe, and the market's,
        // voided, puts its 50 back into it.
        await PaidFromTakings(null, "L-2", today, [new(onions, 1, "kg", 1000, 45)]);
        Assert.Equal(10_000m, await Safe());
        await Void(ApprovalModule.PurchaseInvoice, market);
        Assert.Equal(10_050m, await Safe());

        // Entered wrong: voided, and entered again at 10,500. The safe has 10,500 less what stands
        // paid out of them, 445.
        await Void(ApprovalModule.Treasury, entered.Id);
        await Takings(10_500, today);
        Assert.Equal(10_055m, await Safe());

        // Yesterday's paid out more than its gross: the safe pays the rest. The day before's are
        // not entered yet: what was paid out of them is the drawer's.
        await PaidFromTakings(null, "M-7", yesterday, [new(onions, 2, "kg", 1000, 70)]);
        await Takings(50, yesterday);
        await PaidFromTakings(null, null, before, [new(tomatoes, 1, "kg", 1000, 30)]);
        Assert.Equal(10_035m, await Safe());

        var report = await treasury.GetDailyCashAsync(before, today, ct);
        Assert.Equal((10_550m, 545m, 10_035m), (report.Gross, report.Deductions, report.NetToSafe));
        Assert.Equal(
            [(today, 10_500m, 445m, 10_055m), (yesterday, 50m, 70m, -20m), (before, 0m, 30m, 0m)],
            report.Days.Select(d => (d.Date, d.Gross, d.Deductions, d.NetToSafe)));
        var day = report.Days[0];
        Assert.Equal(
            [("Invoice F-1 · Nile Farms", 400m, (Guid?)nile, "Onions, Tomatoes"), ("Invoice L-2", 45m, day.Paid[1].PurchaseInvoiceId, "Onions")],
            day.Paid.Select(l => (l.Description, l.Amount, l.PurchaseInvoiceId, string.Join(", ", l.Items))));
        Assert.Equal(("Takings entered", 10_500m, "Omar"), day.Entered.Select(l => (l.Description, l.Amount, l.RecordedBy)).Single());
        Assert.Empty(report.Days[2].Entered);

        // The safe's line, the reconciliation and the day's cash: the gross as entered.
        var line = (await treasury.GetSafeAsync(today.Year, today.Month, ct)).Entries
            .Last(e => e.Kind == SafeTransactionKind.RevenueTransfer && e.RevenueDate == today);
        Assert.Equal(
            ($"Takings of {today.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)} · L.E 10,500.00 gross, less L.E 450.00 paid out of them",
                10_050m, (decimal?)10_500m),
            (line.Description, line.In, line.Gross));
        var reconciled = (await treasury.GetReconciliationAsync(today, today, ct)).Days.Single();
        Assert.Equal((10_500m, 445m, 10_500m), (reconciled.TakingsEntered, reconciled.PaidOut, reconciled.NetCash));
        var cash = (await treasury.GetDayRevenueAsync(today, today, ct)).Days.Single();
        Assert.Equal((10_500m, 445m, 10_055m, -10_500m), (cash.Entered, cash.PaidOut, cash.Transferred, cash.Left));
    }

    // A day's purchases entered together (يومية المشتريات المجمعة): an invoice for each supplier, each
    // its own posted invoice with its own payment, never one for the lot; none saved unless all can be.
    [Fact]
    public async Task A_days_purchases_entered_together_are_an_invoice_for_each_supplier()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var treasury = node.Service<TreasuryService>(scope);
        var today = treasury.Today;
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var milk = (await node.Send(new CreateRawMaterialCommand("Milk", UnitOfMeasure.Millilitre))).Id;
        var nile = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms"))).Id;
        var delta = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Delta Dairy"))).Id;
        await node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), SafeTransactionKind.OpeningBalance, 10_000, today, FundingSource.CarriedOver),
            null, "staff-omar", "Omar", CanApprove: true));
        async Task<decimal> Safe() => (await treasury.GetSafeAsync(today.Year, today.Month, ct)).BalanceNow;
        Task<decimal> OnHand(Guid id) => node.Db(db => db.RawMaterials.Where(m => m.Id == id).Select(m => m.QuantityOnHand).SingleAsync());
        BatchInvoice Tomatoes(Guid? supplier, decimal cost = 100) => new(Guid.NewGuid(), supplier, "F-1", [new(tomatoes, 5, "kg", 1000, cost)]);
        BatchInvoice Milk(Guid? supplier) => new(Guid.NewGuid(), supplier, null, [new(milk, 10, "L", 1000, 420)]);
        async Task<IReadOnlyList<PurchaseInvoiceDto>> Batch(PurchasePaymentMethod method, bool manager, params BatchInvoice[] invoices) =>
            (await node.Send(new PostPurchaseBatchCommand(today, method, null, invoices, manager ? "Omar" : "Mona", manager ? "staff-omar" : "staff-mona", manager)))
            .Invoices;

        // A manager's, paid from the safe: two invoices, each its supplier's, each paid on its own.
        var paid = await Batch(PurchasePaymentMethod.Safe, true, Tomatoes(nile), Milk(delta));
        Assert.Equal([("Nile Farms", 100m), ("Delta Dairy", 420m)], paid.Select(i => (i.SupplierName!, i.Total)));
        Assert.All(paid, i => Assert.Equal((PurchaseInvoiceStatus.Posted, today, InvoicePaymentStatus.Paid), (i.Status, i.InvoiceDate!.Value, i.PaymentStatus)));
        Assert.Equal(10_000m - 520, await Safe());
        var payments = await node.Db(db => db.SafeTransactions.Where(t => t.Kind == SafeTransactionKind.InvoicePayment).ToListAsync());
        Assert.Equal(paid.Select(i => (Guid?)i.Id).Order(), payments.Select(t => t.PurchaseInvoiceId).Order());
        Assert.Equal((5_000m, 10_000m), (await OnHand(tomatoes), await OnHand(milk)));

        // On credit, one with no supplier: nothing is saved, and the problem names its invoice.
        var refused = await Assert.ThrowsAsync<DomainException>(() => Batch(PurchasePaymentMethod.Credit, true, Tomatoes(nile), Milk(null)));
        Assert.StartsWith("Invoice 2: ", refused.Message);
        Assert.Equal(2, await node.Db(db => db.PurchaseInvoices.CountAsync()));
        Assert.Equal(5_000m, await OnHand(tomatoes));

        // On credit, each owed to its own supplier.
        await Batch(PurchasePaymentMethod.Credit, true, Tomatoes(nile, 150), Milk(delta));
        Assert.Equal((150m, 420m), ((await treasury.GetSupplierStatementAsync(nile, ct))!.Balance, (await treasury.GetSupplierStatementAsync(delta, ct))!.Balance));

        // The accountant's from the safe: the stock is in, a payment asked of a manager for each, the safe as it was.
        var safeBefore = await Safe();
        var asked = await Batch(PurchasePaymentMethod.Safe, false, Tomatoes(nile), Milk(delta));
        Assert.All(asked, i => Assert.Equal(InvoicePaymentStatus.Pending, i.PaymentStatus));
        var pending = (await node.Send(new GetApprovalRequestsQuery())).Pending.Where(r => r.Module == ApprovalModule.Treasury).ToList();
        Assert.Equal(asked.Select(i => i.Id).Order(), pending.Select(r => r.RecordId).Order());
        Assert.Equal(safeBefore, await Safe());
        Assert.Equal(15_000m, await OnHand(tomatoes));

        // Sent again after a lost answer: the same invoices, nothing twice.
        var again = new[] { Tomatoes(nile), Milk(delta) };
        await Batch(PurchasePaymentMethod.Safe, true, again);
        var count = await node.Db(db => db.PurchaseInvoices.CountAsync());
        Assert.Equal(again.Select(i => i.InvoiceId), (await Batch(PurchasePaymentMethod.Safe, true, again)).Select(i => i.Id));
        Assert.Equal(count, await node.Db(db => db.PurchaseInvoices.CountAsync()));
    }

    // A posted invoice corrected (تعديل فاتورة): voided and posted again as it should have been, as a
    // new invoice naming it, nothing posted rewritten. A manager's at once; an accountant's a request,
    // the invoice as it was and locked until a manager rejects it (with a reason, kept) or approves it.
    [Fact]
    public async Task A_posted_invoice_is_corrected_by_a_new_one_a_managers_at_once_an_accountants_on_approval()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var treasury = node.Service<TreasuryService>(scope);
        var today = treasury.Today;
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var onions = (await node.Send(new CreateRawMaterialCommand("Onions", UnitOfMeasure.Gram))).Id;
        var nile = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms"))).Id;
        await node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), SafeTransactionKind.OpeningBalance, 10_000, today, FundingSource.CarriedOver),
            null, "staff-omar", "Omar", CanApprove: true));
        async Task<decimal> Safe() => (await treasury.GetSafeAsync(today.Year, today.Month, ct)).BalanceNow;
        Task<decimal> OnHand(Guid id) => node.Db(db => db.RawMaterials.Where(m => m.Id == id).Select(m => m.QuantityOnHand).SingleAsync());
        Task<PurchaseInvoiceDto> Invoice(Guid id) => node.Send(new GetPurchaseInvoiceQuery(id))!;
        InvoiceCorrection Correction(PurchasePaymentMethod method, string? number, IReadOnlyList<PurchaseInvoiceLineEntry> lines) =>
            new(Guid.NewGuid(), nile, number, today, lines, method, null);
        Task<ApprovalResultDto> Correct(Guid id, InvoiceCorrection change, bool manager = true) => node.Send(new CorrectPurchaseInvoiceCommand(
            Guid.NewGuid(), id, change, manager ? null : "Typed 5 for 6", manager ? "staff-omar" : "staff-mona", manager ? "Omar" : "Mona", manager));

        // Five kilos of tomatoes from Nile Farms, paid from the safe.
        var first = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(first, nile, "F-1", today, [new(tomatoes, 5, "kg", 1000, 100)], "Omar", PurchasePaymentMethod.Safe));
        await node.Send(new PostPurchaseInvoiceCommand(first, "Omar"));
        Assert.Equal((9_900m, 5_000m), (await Safe(), await OnHand(tomatoes)));

        // The manager's correction: six kilos, and the onions forgotten. Done at once: the first
        // voided, replaced; its cash back, the correction's paid; the stock as it should have been.
        var fix = Correction(PurchasePaymentMethod.Safe, "F-1", [new(tomatoes, 6, "kg", 1000, 120), new(onions, 2, "kg", 1000, 30)]);
        var done = await Correct(first, fix);
        Assert.Equal(ApprovalOutcome.Applied, done.Outcome);
        Assert.Equal("Invoice F-1 · Nile Farms corrected · L.E 100.00 → L.E 150.00", done.Request.Summary);
        Assert.Equal(
            [new ApprovalChange("Tomatoes", "5 kg · L.E 100.00", "6 kg · L.E 120.00"), new ApprovalChange("Onions", null, "2 kg · L.E 30.00"),
                new ApprovalChange("Total", "L.E 100.00", "L.E 150.00")],
            done.Request.Changes);
        var (was, now) = (await Invoice(first), await Invoice(fix.NewInvoiceId));
        Assert.Equal((PurchaseInvoiceStatus.Voided, (Guid?)fix.NewInvoiceId), (was.Status, was.ReplacedBy));
        Assert.Equal((PurchaseInvoiceStatus.Posted, (Guid?)first, InvoicePaymentStatus.Paid, 150m), (now.Status, now.RevisionOf, now.PaymentStatus, now.Total));
        Assert.Equal((9_850m, 6_000m, 2_000m), (await Safe(), await OnHand(tomatoes), await OnHand(onions)));

        // Corrected already, or nothing changed: refused.
        Assert.Contains("already been corrected", (await Assert.ThrowsAsync<DomainException>(() => Correct(first, fix with { NewInvoiceId = Guid.NewGuid() }))).Message);
        Assert.Contains("Nothing has changed",
            (await Assert.ThrowsAsync<DomainException>(() => Correct(fix.NewInvoiceId, fix with { NewInvoiceId = Guid.NewGuid() }))).Message);

        // The accountant's: on credit, numbered F-2. A request; the invoice, stock and safe as they
        // were, and the invoice locked: neither corrected nor voided again meanwhile.
        var credit = Correction(PurchasePaymentMethod.Credit, "F-2", [new(tomatoes, 6, "kg", 1000, 120), new(onions, 2, "kg", 1000, 30)]);
        var asked = await Correct(fix.NewInvoiceId, credit, manager: false);
        Assert.Equal(ApprovalOutcome.Requested, asked.Outcome);
        Assert.Equal(
            [new ApprovalChange("Invoice no.", "F-1", "F-2"), new ApprovalChange("Paid", "From the safe", "On credit"),
                new ApprovalChange("Total", "L.E 150.00", "L.E 150.00")],
            asked.Request.Changes);
        Assert.Equal((PurchaseInvoiceStatus.Posted, 9_850m), ((await Invoice(fix.NewInvoiceId)).Status, await Safe()));
        await Assert.ThrowsAsync<DomainException>(() => Correct(fix.NewInvoiceId, credit with { NewInvoiceId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new RequestDeletionCommand(
            Guid.NewGuid(), ApprovalModule.PurchaseInvoice, fix.NewInvoiceId, null, "staff-omar", "Omar", CanApprove: true)));

        // Rejected, with the reason: kept, and the invoice as it was, open to correction again.
        var rejected = await node.Send(new RejectRequestCommand(asked.Request.Id, "F-1 is right", "Omar"));
        Assert.Equal((ApprovalStatus.Rejected, "F-1 is right"), (rejected.Status, rejected.DecisionNote));
        Assert.Equal(PurchaseInvoiceStatus.Posted, (await Invoice(fix.NewInvoiceId)).Status);

        // Asked again, and approved: the correction is on credit, owed to Nile Farms; the safe has
        // the 150 back. Entered by the accountant, posted by the manager who approved it.
        var again = credit with { NewInvoiceId = Guid.NewGuid() };
        await node.Send(new ApproveRequestCommand((await Correct(fix.NewInvoiceId, again, manager: false)).Request.Id, "Omar"));
        var onCredit = await Invoice(again.NewInvoiceId);
        Assert.Equal((InvoicePaymentStatus.OnCredit, "Mona", "Omar", (Guid?)fix.NewInvoiceId), (onCredit.PaymentStatus, onCredit.CreatedBy, onCredit.PostedBy, onCredit.RevisionOf));
        Assert.Equal((10_000m, 150m), (await Safe(), (await treasury.GetSupplierStatementAsync(nile, ct))!.Balance));
        Assert.Equal((6_000m, 2_000m), (await OnHand(tomatoes), await OnHand(onions)));

        // An invoice from before the treasury is corrected as one: no cash, no supplier's line.
        var old = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(old, nile, "OLD-1", today.AddDays(-30), [new(onions, 1, "kg", 1000, 10)], "Omar"));
        await node.Db(db => db.PurchaseInvoices.Where(i => i.Id == old)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, PurchaseInvoiceStatus.Posted).SetProperty(i => i.PaymentMethod, PurchasePaymentMethod.BeforeTreasury)));
        Assert.Contains("before the treasury", (await Assert.ThrowsAsync<DomainException>(() =>
            Correct(old, Correction(PurchasePaymentMethod.Safe, "OLD-1", [new(onions, 1, "kg", 1000, 12)])))).Message);
        var oldFix = Correction(PurchasePaymentMethod.BeforeTreasury, "OLD-1", [new(onions, 1, "kg", 1000, 12)]);
        await Correct(old, oldFix);
        Assert.Equal(PurchasePaymentMethod.BeforeTreasury, (await Invoice(oldFix.NewInvoiceId)).PaymentMethod);
        Assert.Equal((10_000m, 150m), (await Safe(), (await treasury.GetSupplierStatementAsync(nile, ct))!.Balance));
    }

    // A material created in the wrong unit (عدّ بـ): tomatoes in grams that the kitchen counts by
    // the piece. While nothing records it, the unit simply changes, for anyone. Once something does,
    // the change says how many grams a piece is, and every record naming it is restated with it, the
    // money as it was: at once for a manager, on approval for anyone else, the material locked
    // meanwhile. Without the conversion it is refused: archive it and create a new one.
    [Fact]
    public async Task A_materials_unit_changes_at_once_while_unused_and_once_used_restates_everything_on_a_managers_say()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        DateOnly today;
        using (var scope = node.Scope())
            today = node.Service<TreasuryService>(scope).Today;
        async Task<Guid> Material(string name, UnitOfMeasure unit) => (await node.Send(new CreateRawMaterialCommand(name, unit))).Id;
        Task<RawMaterial> Read(Guid id) => node.Db(db => db.RawMaterials.AsNoTracking().SingleAsync(m => m.Id == id));
        Task<MaterialUnitChangeResult> Change(Guid id, MaterialUnitChange change, bool manager = true) => node.Send(new ChangeMaterialUnitCommand(
            Guid.NewGuid(), id, change, manager ? null : "The kitchen counts them", manager ? "staff-omar" : "staff-mona", manager ? "Omar" : "Mona", manager));
        async Task<decimal> BurgerCost()
        {
            using var scope = node.Scope();
            return (await node.Service<CostingService>(scope).GetRecipeCardAsync(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, ct)).CostPerRecipe;
        }

        // Lemons, entered in grams and not bought yet: changed at once, even by the accountant, with
        // their price and reorder level for the new unit.
        var lemons = await Material("Lemons", UnitOfMeasure.Gram);
        var unused = await Change(lemons, new MaterialUnitChange(UnitOfMeasure.Piece, null, "piece", 1, CostPerPurchaseUnit: 3, ReorderLevel: 20), manager: false);
        Assert.Equal((ApprovalOutcome.Applied, (ApprovalRequestDto?)null), (unused.Outcome, unused.Request));
        Assert.Equal((UnitOfMeasure.Piece, "piece", 1m, 20m), (unused.Material.Unit, unused.Material.PurchaseUnit, unused.Material.PurchaseUnitSize, unused.Material.ReorderLevel));
        Assert.Equal(3m, (await Read(lemons)).AverageCost);

        // Tomatoes, in grams: 6 kg bought at 180, 120 g in a burger, 3 kg in a batch of sauce, and
        // 2.4 kg counted.
        var tomatoes = await Material("Tomatoes", UnitOfMeasure.Gram);
        var nile = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms"))).Id;
        var invoice = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(invoice, nile, "F-1", today, [new(tomatoes, 6, "kg", 1000, 180)], "Omar", PurchasePaymentMethod.Credit));
        await node.Send(new PostPurchaseInvoiceCommand(invoice, "Omar"));
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Burger, null, [new(tomatoes, 120)]));
        var sauce = await node.Send(new SavePrepItemCommand(null, "Tomato sauce", UnitOfMeasure.Gram, 5_000, [new(tomatoes, 3_000)]));
        await node.Send(new MakeProductionOrderCommand(Guid.NewGuid(), sauce.Id, 5_000, 5_000, null, "Omar"));
        await node.Send(new RecordStockCountCommand(Guid.NewGuid(), [new(tomatoes, 2_400)], "Omar"));
        Assert.Equal((2_400m, 0.03m), ((await Read(tomatoes)).QuantityOnHand, (await Read(tomatoes)).AverageCost));
        Assert.Equal(3.6m, await BurgerCost());
        Assert.Equal(new MaterialUsage(3, 1, 1, 1, 1, 1, 0), await node.Send(new GetMaterialUsageQuery(tomatoes)));

        // Without the conversion, refused; nor to the unit it is in.
        var crate = new MaterialUnitChange(UnitOfMeasure.Piece, null, "crate", 100);
        Assert.Contains("archive it and create a new material", (await Assert.ThrowsAsync<DomainException>(() => Change(tomatoes, crate))).Message);
        await Assert.ThrowsAsync<DomainException>(() => Change(tomatoes, new MaterialUnitChange(UnitOfMeasure.Gram, 1, "kg", 1000)));

        // Nor while a request waiting for a manager holds them in grams: the sauce asked to take more.
        var piece = crate with { OneNewIsOld = 60 };
        var more = await node.Send(new RequestPrepItemChangeCommand(
            Guid.NewGuid(), sauce.Id, new SavePrepItemCommand(sauce.Id, "Tomato sauce", UnitOfMeasure.Gram, 5_000, [new(tomatoes, 3_500)]).Change, null,
            "staff-mona", "Mona"));
        Assert.Contains("waiting for a manager", (await Assert.ThrowsAsync<DomainException>(() => Change(tomatoes, piece))).Message);
        await node.Send(new RejectRequestCommand(more.Request.Id, null, "Omar"));

        // The accountant's, 60 g a piece: asked, and said line by line; the tomatoes as they were,
        // and locked until a manager decides.
        var asked = await Change(tomatoes, piece, manager: false);
        Assert.Equal(ApprovalOutcome.Requested, asked.Outcome);
        Assert.Equal("Tomatoes counted in pieces, not g: 1 piece is 60 g", asked.Request!.Summary);
        Assert.Equal(
            [new ApprovalChange("Counted in", "g", "pieces"), new ApprovalChange("Conversion", null, "1 piece is 60 g"),
                new ApprovalChange("On hand", "2,400 g", "40 pieces"), new ApprovalChange("Average cost", "L.E 0.03 a g", "L.E 1.80 a piece"),
                new ApprovalChange("Bought by", "kg of 1,000 g", "crate of 100 pieces"),
                new ApprovalChange("Restated in the new unit", null, "3 stock movements, 1 invoice line, 1 recipe, 1 prep recipe, 1 stock count, 1 batch")],
            asked.Request.Changes);
        Assert.Equal((UnitOfMeasure.Gram, 2_400m), ((await Read(tomatoes)).Unit, (await Read(tomatoes)).QuantityOnHand));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new UpdateRawMaterialCommand(tomatoes, "Plum tomatoes", 0)));
        await Assert.ThrowsAsync<DomainException>(() => Change(tomatoes, piece));

        // Rejected: kept, and the tomatoes open again. Asked again, and approved: in pieces.
        await node.Send(new RejectRequestCommand(asked.Request.Id, "Weigh them first", "Omar"));
        Assert.Equal(UnitOfMeasure.Gram, (await Read(tomatoes)).Unit);
        var again = await Change(tomatoes, piece, manager: false);
        Assert.Equal(ApprovalStatus.Approved, (await node.Send(new ApproveRequestCommand(again.Request!.Id, "Omar"))).Status);
        var now = await Read(tomatoes);
        Assert.Equal((UnitOfMeasure.Piece, 40m, 1.8m, "crate", 100m), (now.Unit, now.QuantityOnHand, now.AverageCost, now.PurchaseUnit, now.PurchaseUnitSize));

        // Every record restated, every amount of money as it was: the ledger, the invoice (6 kg, 180),
        // the burger (2 pieces, 3.60), the sauce, the count (40 of 50 on the books) and the batch.
        Assert.Equal([(-50m, (decimal?)1.8m), (-10m, 1.8m), (100m, 1.8m)], await node.Db(db => db.StockMovements.Where(m => m.RawMaterialId == tomatoes)
            .OrderBy(m => m.Quantity).Select(m => new ValueTuple<decimal, decimal?>(m.Quantity, m.UnitCost)).ToListAsync()));
        var line = await node.Db(db => db.Set<PurchaseInvoiceLine>().AsNoTracking().SingleAsync(l => l.RawMaterialId == tomatoes));
        Assert.Equal((6m, 16.667m, 100m, (decimal?)180m), (line.EnteredQuantity, line.UnitSize, line.Quantity, line.Cost));
        Assert.Equal(2m, await node.Db(db => db.Set<RecipeIngredient>().Where(i => i.RawMaterialId == tomatoes).Select(i => i.Quantity).SingleAsync()));
        Assert.Equal(3.6m, await BurgerCost());
        Assert.Equal(50m, await node.Db(db => db.Set<PrepIngredient>().Where(i => i.RawMaterialId == tomatoes).Select(i => i.Quantity).SingleAsync()));
        var counted = await node.Db(db => db.Set<OtantikPos.Inventory.Domain.StockCounts.StockCountLine>().AsNoTracking().SingleAsync(l => l.RawMaterialId == tomatoes));
        Assert.Equal((40m, (decimal?)50m, (decimal?)1.8m), (counted.CountedQuantity, counted.BookQuantity, counted.UnitCost));
        var used = await node.Db(db => db.Set<ProductionOrderLine>().AsNoTracking().SingleAsync(l => l.RawMaterialId == tomatoes));
        Assert.Equal((50m, 1.8m, 90m), (used.Quantity, used.UnitCost, used.Cost));

        // A burger sold now takes two pieces.
        var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "V1"));
        await node.Send(new AddOrderItemCommand(order.PublicId, FakeDeliverySystem.Burger));
        await node.Send(new SendToKitchenCommand(order.PublicId));
        await node.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        Assert.True(await TestNode.WaitFor(async () => (await Read(tomatoes)).QuantityOnHand == 38m));

        // The manager's own, at once, recorded as decided by them: milk entered in grams, a gram to
        // the millilitre.
        var milk = await Material("Milk", UnitOfMeasure.Gram);
        await node.Send(new ReceivePurchaseCommand(Guid.NewGuid(), [new(milk, 2_000, 60)]));
        var done = await Change(milk, new MaterialUnitChange(UnitOfMeasure.Millilitre, 1, "L", 1000));
        Assert.Equal((ApprovalOutcome.Applied, ApprovalStatus.Approved, "Omar"), (done.Outcome, done.Request!.Status, done.Request.DecidedBy));
        Assert.Equal("Milk counted in ml, not g: 1 ml is 1 g", done.Request.Summary);
        Assert.Equal((UnitOfMeasure.Millilitre, 2_000m, 0.03m), ((await Read(milk)).Unit, (await Read(milk)).QuantityOnHand, (await Read(milk)).AverageCost));

        // A prep item keeps its unit.
        Assert.Contains("prep item", (await Assert.ThrowsAsync<DomainException>(() =>
            Change(sauce.Id, new MaterialUnitChange(UnitOfMeasure.Piece, 250, "jar", 1)))).Message);
    }

    // A material's purchase history card (كارت الصنف): every invoice line that bought it, newest first,
    // as the paper has it, to hold against the paper invoices. A voided invoice is on it, marked, its
    // correction named, and out of the totals; a draft has bought nothing; another material's lines
    // are not its.
    [Fact]
    public async Task A_materials_purchase_history_lists_each_invoice_line_that_bought_it()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var today = node.Service<TreasuryService>(scope).Today;
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var onions = (await node.Send(new CreateRawMaterialCommand("Onions", UnitOfMeasure.Gram))).Id;
        var nile = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Farms"))).Id;
        var cairo = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Cairo Foods"))).Id;
        async Task<Guid> Posted(Guid supplier, string number, DateOnly date, IReadOnlyList<PurchaseInvoiceLineEntry> lines)
        {
            var id = Guid.NewGuid();
            await node.Send(new SavePurchaseInvoiceCommand(id, supplier, number, date, lines, "Omar", PurchasePaymentMethod.Credit));
            await node.Send(new PostPurchaseInvoiceCommand(id, "Omar"));
            return id;
        }

        // 5 kg from Nile Farms, corrected to 6 kg; 500 g from Cairo Foods beside some onions; 1 kg
        // with no price yet; and a draft of 9 kg, not posted.
        var first = await Posted(nile, "F-1", today.AddDays(-3), [new(tomatoes, 5, "kg", 1000, 100)]);
        var corrected = new InvoiceCorrection(Guid.NewGuid(), nile, "F-1", today.AddDays(-3), [new(tomatoes, 6, "kg", 1000, 120)], PurchasePaymentMethod.Credit, null);
        await node.Send(new CorrectPurchaseInvoiceCommand(Guid.NewGuid(), first, corrected, null, "staff-omar", "Omar", CanApprove: true));
        var cairos = await Posted(cairo, "C-7", today.AddDays(-1), [new(tomatoes, 500, "g", 1, 15), new(onions, 2, "kg", 1000, 30)]);
        var unpriced = await Posted(nile, "F-2", today, [new(tomatoes, 1, "kg", 1000, null)]);
        await node.Send(new SavePurchaseInvoiceCommand(Guid.NewGuid(), nile, "F-3", today, [new(tomatoes, 9, "kg", 1000, 270)], "Omar"));

        var card = await node.Service<PurchaseSpendingService>(scope).MaterialHistoryAsync(tomatoes, ct);

        Assert.Equal(("Tomatoes", UnitOfMeasure.Gram, "kg", 1000m), (card.Material, card.Unit, card.PurchaseUnit, card.PurchaseUnitSize));
        Assert.Equal(
            [
                new MaterialPurchase(unpriced, today, "Nile Farms", "F-2", 1, "kg", 1_000, null, null, false, null, null),
                new MaterialPurchase(cairos, today.AddDays(-1), "Cairo Foods", "C-7", 500, "g", 500, 0.03m, 15, false, null, null),
                new MaterialPurchase(corrected.NewInvoiceId, today.AddDays(-3), "Nile Farms", "F-1", 6, "kg", 6_000, 20, 120, false, null, first),
                new MaterialPurchase(first, today.AddDays(-3), "Nile Farms", "F-1", 5, "kg", 5_000, 20, 100, true, corrected.NewInvoiceId, null),
            ],
            card.Lines);
        // What stands: 7.5 kg, 135 for the 6.5 kg priced, 20.77 a kg.
        Assert.Equal((7_500m, 135m, (decimal?)20.77m, 1), (card.BaseQuantity, card.Total, card.AveragePerPurchaseUnit, card.UnpricedLines));

        // Nothing bought: an empty card, no average.
        var nothing = await node.Service<PurchaseSpendingService>(scope).MaterialHistoryAsync(
            (await node.Send(new CreateRawMaterialCommand("Saffron", UnitOfMeasure.Gram))).Id, ct);
        Assert.Equal((0, 0m, (decimal?)null), (nothing.Lines.Count, nothing.Total, nothing.AveragePerPurchaseUnit));
        await Assert.ThrowsAsync<NotFoundException>(() => node.Service<PurchaseSpendingService>(scope).MaterialHistoryAsync(Guid.NewGuid(), ct));
    }

    // A category made for stock by mistake (cleaning sprays filed as raw materials), deleted and
    // its raw materials made expense items on the way: their invoice lines restated as expenses on
    // their own dates, their ledger and count lines taken out, the invoices' totals as they were.
    // An accountant's waits for a manager, the category locked; a manager's is done at once.
    // Refused for one a recipe uses or with an unpriced line, and on approval if the category has
    // changed since it was asked.
    [Fact]
    public async Task A_categorys_raw_materials_become_expense_items_as_it_is_deleted_their_purchases_restated()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var today = node.Service<TreasuryService>(scope).Today;
        var spending = node.Service<PurchaseSpendingService>(scope);
        async Task<Guid> Category(string name) => (await node.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), name))).Id;
        async Task<Guid> Material(string name, Guid category) =>
            (await node.Send(new CreateRawMaterialCommand(name, UnitOfMeasure.Piece, CategoryId: category, PurchaseUnit: "piece", PurchaseUnitSize: 1))).Id;
        var nile = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), "Nile Supplies"))).Id;
        async Task<Guid> Posted(string number, DateOnly date, IReadOnlyList<PurchaseInvoiceLineEntry> lines)
        {
            var id = Guid.NewGuid();
            await node.Send(new SavePurchaseInvoiceCommand(id, nile, number, date, lines, "Omar", PurchasePaymentMethod.Credit));
            await node.Send(new PostPurchaseInvoiceCommand(id, "Omar"));
            return id;
        }
        Task<RawMaterial> Read(Guid id) => node.Db(db => db.RawMaterials.AsNoTracking().SingleAsync(m => m.Id == id));
        Task<ExpenseItem> Expense(string name) => node.Db(db => db.ExpenseItems.AsNoTracking().SingleAsync(e => e.Name == name));
        DeletePurchaseCategoryCommand Delete(Guid category, Guid to, bool manager) => new(
            category, to, CategoryConversion.ToExpenses, null, Guid.NewGuid(), manager ? null : "Cleaning is an expense",
            manager ? "staff-omar" : "staff-mona", manager ? "Omar" : "Mona", manager);

        var cleaning = await Category("Cleaning stock");
        var maintenance = (await node.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), "Maintenance", null, ExpenseLine.Controllable))).Id;
        var bleach = await Material("Bleach", cleaning);
        var spray = await Material("Glass spray", cleaning);
        await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), "Gloves", cleaning));
        // An expense item of the spray's name, archived: the spray joins it, and it comes back.
        var oldSpray = (await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), "glass  spray", maintenance))).Id;
        await node.Db(db => db.ExpenseItems.Where(e => e.Id == oldSpray).ExecuteUpdateAsync(u => u.SetProperty(e => e.IsArchived, true)));

        // Bleach bought twice and counted; the spray bought, and one thrown away.
        var first = await Posted("C-1", today.AddDays(-5), [new(bleach, 10, "piece", 1, 100), new(spray, 4, "piece", 1, 60)]);
        var second = await Posted("C-2", today.AddDays(-2), [new(bleach, 5, "piece", 1, 55)]);
        await node.Send(new RecordStockCountCommand(Guid.NewGuid(), [new(bleach, 12)], "Omar"));
        await node.Send(new RecordSpoilageCommand(Guid.NewGuid(), [new(spray, 1, "Leaked")], "Omar"));
        var before = await spending.GetAsync(today.AddDays(-30), today, ct);
        Assert.Equal((215m, 0m), (before.Inventory, before.Expenses));

        // The accountant's: asked, line by line; nothing changes, and the category is locked.
        var asked = await node.Send(Delete(cleaning, maintenance, manager: false));
        Assert.Equal(ApprovalOutcome.Requested, asked.Outcome);
        Assert.Equal("Category Cleaning stock deleted: 2 raw materials become expense items in Maintenance", asked.Request!.Summary);
        Assert.Equal(
            [
                new ApprovalChange("Bleach", "Raw material, 12 pieces on hand", "Expense item"),
                new ApprovalChange("Glass spray", "Raw material, 3 pieces on hand", "Expense item (joins glass spray)"),
                new ApprovalChange("Invoice lines", null, "3 lines (L.E 215.00) restated as expenses, each on its invoice's date"),
                new ApprovalChange("Stock ledger", null, "5 entries and 1 count line taken out"),
                new ApprovalChange("Their expenses count as", null, "Controllable costs"),
                new ApprovalChange("Moved as they are", null, "1 item to Maintenance"),
            ],
            asked.Request.Changes);
        Assert.Equal((12m, false), ((await Read(bleach)).QuantityOnHand, (await Read(bleach)).IsArchived));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new SavePurchaseCategoryCommand(cleaning, "Cleaning")));
        await Assert.ThrowsAsync<DomainException>(() => node.Send(new DeletePurchaseCategoryCommand(cleaning, maintenance)));

        // Approved: as if they had always been expenses.
        await node.Send(new ApproveRequestCommand(asked.Request.Id, "Omar"));
        Assert.False(await node.Db(db => db.PurchaseCategories.AnyAsync(c => c.Id == cleaning)));
        var gone = await Read(bleach);
        Assert.Equal((true, 0m, maintenance), (gone.IsArchived, gone.QuantityOnHand, gone.CategoryId));
        var bleachExpense = await Expense("Bleach");
        Assert.Equal((maintenance, "piece", false), (bleachExpense.CategoryId, bleachExpense.Unit, bleachExpense.IsArchived));
        Assert.Equal((maintenance, false), ((await Expense("glass spray")).CategoryId, (await Expense("glass spray")).IsArchived));
        Assert.Equal(maintenance, (await Expense("Gloves")).CategoryId);
        var lines = await node.Db(db => db.Set<PurchaseInvoiceLine>().AsNoTracking().Where(l => l.InvoiceId == first || l.InvoiceId == second)
            .OrderBy(l => l.Cost).Select(l => new ValueTuple<PurchaseItemType, Guid?, Guid?, decimal, decimal, decimal?>(
                l.ItemType, l.RawMaterialId, l.ExpenseItemId, l.Quantity, l.UnitSize, l.Cost)).ToListAsync());
        Assert.Equal(
            [
                (PurchaseItemType.Expense, null, bleachExpense.Id, 5m, 1m, 55m),
                (PurchaseItemType.Expense, null, oldSpray, 4m, 1m, 60m),
                (PurchaseItemType.Expense, null, bleachExpense.Id, 10m, 1m, 100m),
            ],
            lines);
        Assert.Equal(0, await node.Db(db => db.StockMovements.CountAsync(m => m.RawMaterialId == bleach || m.RawMaterialId == spray)));
        Assert.Equal(0, await node.Db(db => db.Set<OtantikPos.Inventory.Domain.StockCounts.StockCountLine>().CountAsync(l => l.RawMaterialId == bleach)));
        var after = await spending.GetAsync(today.AddDays(-30), today, ct);
        Assert.Equal((0m, 215m), (after.Inventory, after.Expenses));
        Assert.Equal((PurchaseInvoiceStatus.Posted, 160m), ((await node.Send(new GetPurchaseInvoiceQuery(first)))!.Status, (await node.Send(new GetPurchaseInvoiceQuery(first)))!.Total));

        // Refused for a material a recipe uses, and for one bought with no price.
        var bar = await Category("Bar stock");
        var lemons = await Material("Lemons", bar);
        var mop = await Material("Mop", bar);
        await node.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, FakeDeliverySystem.Lemonade, null, [new(lemons, 1)]));
        await Posted("C-3", today, [new(mop, 1, "piece", 1, null)]);
        var refused = await Assert.ThrowsAsync<DomainException>(() => node.Send(
            new PreviewCategoryDeletionQuery(bar, maintenance, CategoryConversion.ToExpenses, null)));
        Assert.Contains("Lemons is in a recipe.", refused.Message);
        Assert.Contains("Mop is on an invoice with no price", refused.Message);
        // Moved as they are, it is a plain delete, for anyone, at once.
        Assert.Equal((ApprovalOutcome.Applied, (ApprovalRequestDto?)null),
            ((await node.Send(new DeletePurchaseCategoryCommand(bar, maintenance, StaffName: "Mona", CanApprove: false))) is var plain ? (plain.Outcome, plain.Request) : default));
        Assert.Equal(maintenance, (await Read(lemons)).CategoryId);

        // A manager's own: done at once, kept with the decisions.
        var paper = await Category("Paper");
        var napkins = await Material("Napkins", paper);
        await Posted("C-4", today, [new(napkins, 100, "piece", 1, 80)]);
        var done = await node.Send(Delete(paper, maintenance, manager: true));
        Assert.Equal((ApprovalOutcome.Applied, ApprovalStatus.Approved, "Omar"), (done.Outcome, done.Request!.Status, done.Request.DecidedBy));
        Assert.True((await Read(napkins)).IsArchived);

        // Asked, then something put in the category: the approval is refused, the request waits.
        var cups = await Category("Cups");
        await Material("Paper cups", cups);
        var stale = await node.Send(Delete(cups, maintenance, manager: false));
        await Material("Lids", cups);
        Assert.Contains("has changed since it was asked", (await Assert.ThrowsAsync<DomainException>(() => node.Send(new ApproveRequestCommand(stale.Request!.Id, "Omar")))).Message);
        Assert.Equal(ApprovalStatus.Pending, await node.Db(db => db.ApprovalRequests.Where(r => r.Id == stale.Request!.Id).Select(r => r.Status).SingleAsync()));
    }

    // Expense items made raw materials as their category is deleted: each counted in the unit
    // asked and bought as said, starting with nothing on hand; their past expense lines stay
    // expenses, and the expense items are archived. Each needs its unit, and no raw material of its
    // name.
    [Fact]
    public async Task A_categorys_expense_items_become_raw_materials_as_it_is_deleted_starting_at_zero()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var today = node.Service<TreasuryService>(scope).Today;
        async Task<Guid> Category(string name) => (await node.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), name))).Id;
        var packing = await Category("Packing");
        var packaging = await Category("Packaging");
        var boxes = (await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), "Boxes", packing, null, "box"))).Id;
        var cupsItem = (await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), "Cups", packing, null, "sleeve"))).Id;
        var bags = (await node.Send(new SaveExpenseItemCommand(Guid.NewGuid(), "Bags", packing))).Id;
        await node.Db(db => db.ExpenseItems.Where(e => e.Id == bags).ExecuteUpdateAsync(u => u.SetProperty(e => e.IsArchived, true)));
        var tape = (await node.Send(new CreateRawMaterialCommand("Tape", UnitOfMeasure.Piece, CategoryId: packing))).Id;
        var bought = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(bought, null, "P-1", today,
            [new(null, 2, "box", 1, 50, PurchaseItemType.Expense, boxes)], "Omar", PurchasePaymentMethod.Safe));
        StockUnit[] units = [new(boxes, UnitOfMeasure.Piece, "box", 50), new(cupsItem, UnitOfMeasure.Piece, "sleeve", 25)];
        DeletePurchaseCategoryCommand Delete(IReadOnlyList<StockUnit>? given) =>
            new(packing, packaging, CategoryConversion.ToStock, given, Guid.NewGuid(), null, "staff-omar", "Omar", CanApprove: true);

        // Each needs its unit; and a raw material already called Cups is in the way until renamed.
        Assert.Contains("Say what Cups is counted in", (await Assert.ThrowsAsync<DomainException>(() => node.Send(Delete([units[0]])))).Message);
        var cups = (await node.Send(new CreateRawMaterialCommand("cups", UnitOfMeasure.Piece))).Id;
        Assert.Contains("already a raw material called Cups", (await Assert.ThrowsAsync<DomainException>(() => node.Send(Delete(units)))).Message);
        await node.Send(new UpdateRawMaterialCommand(cups, "Paper cups", 0));

        var done = await node.Send(Delete(units));
        Assert.Equal("Category Packing deleted: 2 expense items become raw materials in Packaging", done.Request!.Summary);
        Assert.Equal(
            [
                new ApprovalChange("Boxes", "Expense item", "Raw material counted in pieces, bought by the box of 50 pieces"),
                new ApprovalChange("Cups", "Expense item", "Raw material counted in pieces, bought by the sleeve of 25 pieces"),
                new ApprovalChange("Past expense lines", null, "Stay expenses; each starts with nothing on hand"),
                new ApprovalChange("Moved as they are", null, "2 items to Packaging"),
            ],
            done.Request.Changes);
        var made = await node.Db(db => db.RawMaterials.AsNoTracking().SingleAsync(m => m.Name == "Boxes"));
        Assert.Equal((UnitOfMeasure.Piece, "box", 50m, packaging, 0m, (decimal?)null, false),
            (made.Unit, made.PurchaseUnit, made.PurchaseUnitSize, made.CategoryId, made.QuantityOnHand, made.AverageCost, made.IsArchived));
        var items = await node.Db(db => db.ExpenseItems.AsNoTracking().Where(e => e.Id == boxes || e.Id == cupsItem || e.Id == bags)
            .OrderBy(e => e.Name).Select(e => new ValueTuple<string, bool, Guid>(e.Name, e.IsArchived, e.CategoryId)).ToListAsync());
        Assert.Equal([("Bags", true, packaging), ("Boxes", true, packaging), ("Cups", true, packaging)], items);
        Assert.Equal(packaging, (await node.Db(db => db.RawMaterials.AsNoTracking().SingleAsync(m => m.Id == tape))).CategoryId);
        // The draft's line stays an expense line on Boxes, as entered.
        Assert.Equal((PurchaseItemType.Expense, (Guid?)boxes), await node.Db(db => db.Set<PurchaseInvoiceLine>().Where(l => l.InvoiceId == bought)
            .Select(l => new ValueTuple<PurchaseItemType, Guid?>(l.ItemType, l.ExpenseItemId)).SingleAsync()));
    }

    // A supplier paid in the daily batch (سداد مورد): an old debt settled with no invoice and no
    // stock, from the day's takings or the safe as the batch is paid, against what they are owed and
    // off it on their account; never on credit, never more than owed, and nothing saved unless all is.
    [Fact]
    public async Task Suppliers_are_paid_in_the_daily_batch_against_what_they_are_owed()
    {
        await using var node = await TestNode.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var treasury = node.Service<TreasuryService>(scope);
        var today = treasury.Today;
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        async Task<Guid> Supplier(string name, decimal owedBefore)
        {
            var id = (await node.Send(new SaveSupplierCommand(Guid.NewGuid(), name))).Id;
            await node.Send(new SetSupplierOpeningBalanceCommand(Guid.NewGuid(), id, owedBefore, today, "Omar"));
            return id;
        }
        var nile = await Supplier("Nile Farms", 1_000);
        var cairo = await Supplier("Cairo Foods", 500);
        await node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), SafeTransactionKind.OpeningBalance, 10_000, today, FundingSource.CarriedOver),
            null, "staff-omar", "Omar", CanApprove: true));
        async Task<decimal> Safe() => (await treasury.GetSafeAsync(today.Year, today.Month, ct)).BalanceNow;
        async Task<decimal> Owed(Guid supplier) => (await treasury.GetSupplierStatementAsync(supplier, ct))!.Balance;
        Task<decimal> OnHand() => node.Db(db => db.RawMaterials.Where(m => m.Id == tomatoes).Select(m => m.QuantityOnHand).SingleAsync());
        BatchPayment Pay(Guid supplier, decimal amount, string? notes = null) => new(Guid.NewGuid(), supplier, amount, notes);
        Task<PurchaseBatchResult> Batch(PurchasePaymentMethod method, BatchInvoice[] invoices, BatchPayment[] payments, bool manager = true) =>
            node.Send(new PostPurchaseBatchCommand(
                today, method, method == PurchasePaymentMethod.DayRevenue ? today : null, invoices, manager ? "Omar" : "Mona",
                manager ? "staff-omar" : "staff-mona", manager, payments));

        // Out of the day's takings, beside the market's invoice: Nile Farms paid 600 and 300 of the
        // 1,000 owed. The drawer pays; the safe and the stock are as they were but for the invoice.
        var first = Pay(nile, 600, "Receipt R-12");
        var made = await Batch(PurchasePaymentMethod.DayRevenue, [new(Guid.NewGuid(), null, null, [new(tomatoes, 5, "kg", 1000, 100)])], [first, Pay(nile, 300)]);
        Assert.Equal([("Nile Farms", 600m, "Receipt R-12", ApprovalOutcome.Applied), ("Nile Farms", 300m, (string?)null, ApprovalOutcome.Applied)],
            made.Payments.Select(p => (p.Supplier, p.Amount, p.Notes, p.Outcome)));
        Assert.Single(made.Invoices);
        Assert.Equal((100m, 10_000m, 5_000m), (await Owed(nile), await Safe(), await OnHand()));
        Assert.Equal(new TakingsPreview(today, null, 1_000m, 1, 2), await treasury.GetTakingsPreviewAsync(today, ct));

        // The day's takings entered, 5,000 gross: the 1,000 paid out of them stays out of the safe.
        await node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), SafeTransactionKind.RevenueTransfer, 5_000, today, RevenueDate: today), null,
            "staff-omar", "Omar", true));
        Assert.Equal(14_000m, await Safe());
        // Paid out of them now they are in: out of the safe.
        await Batch(PurchasePaymentMethod.DayRevenue, [], [Pay(nile, 100)]);
        Assert.Equal((0m, 13_900m), (await Owed(nile), await Safe()));

        // More than owed, two payments together more than owed, or on credit: refused, nothing saved.
        Assert.Equal("Payment 1 · Nile Farms: Nothing is owed to Nile Farms.",
            (await Assert.ThrowsAsync<DomainException>(() => Batch(PurchasePaymentMethod.Safe, [], [Pay(nile, 1)]))).Message);
        Assert.Equal("Payment 2 · Cairo Foods: Cairo Foods is owed L.E 200.00 after the payments above: pay that much or less.",
            (await Assert.ThrowsAsync<DomainException>(() => Batch(PurchasePaymentMethod.Safe, [], [Pay(cairo, 300), Pay(cairo, 300)]))).Message);
        Assert.Contains("A supplier payment is cash",
            (await Assert.ThrowsAsync<DomainException>(() => Batch(PurchasePaymentMethod.Credit, [], [Pay(cairo, 100)]))).Message);
        Assert.Equal((500m, 13_900m), (await Owed(cairo), await Safe()));

        // The accountant's, from the safe: asked of a manager, nothing moves until approved. Sent
        // again, it is not asked twice.
        var hers = Pay(cairo, 200);
        Assert.Equal(ApprovalOutcome.Requested, (await Batch(PurchasePaymentMethod.Safe, [], [hers], manager: false)).Payments.Single().Outcome);
        await Batch(PurchasePaymentMethod.Safe, [], [hers], manager: false);
        var asked = (await node.Send(new GetApprovalRequestsQuery())).Pending.Single(r => r.RecordId == hers.TransactionId);
        Assert.Equal((500m, 13_900m), (await Owed(cairo), await Safe()));
        await node.Send(new ApproveRequestCommand(asked.Id, "Omar"));
        Assert.Equal((300m, 13_700m), (await Owed(cairo), await Safe()));

        // In the day's cash and the supplier payments: from the takings, with the receipt.
        var day = (await treasury.GetDailyCashAsync(today, today, ct)).Days.Single();
        Assert.Equal((5_000m, 1_100m, 3_900m), (day.Gross, day.Deductions, day.NetToSafe));
        Assert.Contains(day.Paid, l => (l.Description, l.Amount, string.Join(", ", l.Items)) == ("Payment to Nile Farms", 600m, "Receipt R-12"));
        var rows = await treasury.GetSupplierPaymentsAsync(today, today, ct);
        Assert.Equal([(DateOnly?)today, today, today, null], rows.Select(r => r.FromTakingsOf).OrderByDescending(d => d.HasValue));

        // The first, voided now the takings are in: its 600 back into the safe, and owed again.
        await node.Send(new RequestDeletionCommand(Guid.NewGuid(), ApprovalModule.Treasury, first.TransactionId, "Paid twice", "staff-omar", "Omar", CanApprove: true));
        Assert.Equal((600m, 14_300m), (await Owed(nile), await Safe()));
    }

    // The reconciliation (مطابقة الإيرادات): the day's cash before tax against what its sales, at
    // their prices before tax, should have brought in as cash.
    [Fact]
    public async Task Each_days_cash_before_tax_is_reconciled_with_the_cash_its_sales_should_bring_in()
    {
        await using var node = await TestNode.StartAsync();
        Assert.True(await MenuSynced(node));
        var ct = TestContext.Current.CancellationToken;
        using var scope = node.Scope();
        var treasury = node.Service<TreasuryService>(scope);
        var today = treasury.Today;
        async Task<Order> Sell(string table, PaymentMethod method, params (int Item, int Quantity)[] items)
        {
            var order = await node.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: table));
            foreach (var (item, quantity) in items)
                order = await node.Send(new AddOrderItemCommand(order.PublicId, item, quantity));
            return await node.Send(new CheckoutCommand(order.PublicId, method));
        }
        async Task<SafeTransactionDto> Takings(decimal amount) => (await node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), SafeTransactionKind.RevenueTransfer, amount, today, RevenueDate: today, IncludesTax: true),
            null, "staff-omar", "Omar", CanApprove: true))).Transaction!;

        // Two burgers and two lemonades paid in cash, one lemonade handed back after; a burger by card.
        var cash = await Sell("R1", PaymentMethod.Cash, (FakeDeliverySystem.Burger, 2), (FakeDeliverySystem.Lemonade, 2));
        cash = await node.Send(new VoidOrderItemCommand(
            cash.PublicId, cash.OrderItems.Single(i => i.MenuItemId == FakeDeliverySystem.Lemonade).PublicId, 1, "Spilt"));
        var card = await Sell("R2", PaymentMethod.Visa, (FakeDeliverySystem.Burger, 1));
        Assert.True(cash.RefundedAmount > 0);

        // 100 of the cash paid out for tomatoes; the day's takings entered gross, 10 short, with the
        // tax in them. A mistaken entry, voided, nets out.
        var tomatoes = (await node.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram))).Id;
        var market = Guid.NewGuid();
        await node.Send(new SavePurchaseInvoiceCommand(market, null, null, today, [new(tomatoes, 5, "kg", 1000, 100)], "Omar",
            PurchasePaymentMethod.DayRevenue, today));
        await node.Send(new PostPurchaseInvoiceCommand(market, "Omar"));
        var wrong = await Takings(999);
        await node.Send(new RequestDeletionCommand(Guid.NewGuid(), ApprovalModule.Treasury, wrong.Id, "Mistyped", "staff-omar", "Omar", CanApprove: true));
        var gross = cash.TotalAmount - cash.RefundedAmount - 10;
        var entry = await Takings(gross);
        Assert.Equal(gross - 100, entry.Amount);

        var report = await treasury.GetReconciliationAsync(today, today, ct);
        var day = Assert.Single(report.Days);
        Assert.Equal(day with { Date = null }, report.Total);
        var rate = entry.TaxPercent!.Value;
        Assert.Equal(rate, report.TaxPercent);

        // A: the day's takings as entered, what was paid out of them too, less the tax in them.
        var tax = SafeTransaction.TaxIn(gross, rate);
        Assert.Equal((gross, 100m, tax, gross - tax), (day.TakingsEntered, day.PaidOut, day.Tax, day.NetCash));

        // B: every item sold at its price, the lemonade handed back and the card's burger too.
        Assert.Equal(2 * 120 + 2 * 30 + 120m, day.ItemSales);
        // Then before tax: the card's bill, and the refund at its bill's own proportion of tax.
        var refund = Math.Round(cash.RefundedAmount * (cash.TotalAmount - cash.TaxAmount) / cash.TotalAmount, 2, MidpointRounding.AwayFromZero);
        Assert.Equal((card.TotalAmount - card.TaxAmount, refund), (day.CardAndOnline, day.Refunds));
        Assert.Equal(cash.TotalAmount - cash.TaxAmount - refund, day.ExpectedCash);
        Assert.Equal(day.ItemSales - day.Discounts - day.Points + day.DeliveryFees - day.Refunds - day.CardAndOnline, day.ExpectedCash);

        // Short by the 10 not counted, before its tax.
        Assert.Equal(day.NetCash - day.ExpectedCash, day.Difference);
        Assert.InRange(day.Difference, -10m, -8m);

        // A day whose only takings were voided, with nothing sold, has nothing to reconcile.
        var yesterday = await node.Send(new RecordSafeVoucherCommand(
            Guid.NewGuid(), new SafeVoucher(Guid.NewGuid(), SafeTransactionKind.RevenueTransfer, 50, today, RevenueDate: today.AddDays(-1)),
            null, "staff-omar", "Omar", CanApprove: true));
        await node.Send(new RequestDeletionCommand(
            Guid.NewGuid(), ApprovalModule.Treasury, yesterday.Transaction!.Id, null, "staff-omar", "Omar", CanApprove: true));
        Assert.Equal([today], (await treasury.GetReconciliationAsync(today.AddDays(-1), today, ct)).Days.Select(d => d.Date!.Value));
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
