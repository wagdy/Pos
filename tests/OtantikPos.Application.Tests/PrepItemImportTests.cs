using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.Production;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Application.Tests;

// Prep items brought in from a sheet of batch recipes: each a prep item under المصنعات, its batch
// the weight of its ingredients; ingredients found by name or added; a line the till cannot use
// left out and said; a manager's made at once, anyone else's asked of a manager.
public class PrepItemImportTests
{
    private readonly TestPos _pos = new();

    private static PrepSheetLine Line(int row, string ingredient, string? unit, string? quantity) => new(row, ingredient, unit, quantity);

    private Task<PrepImportResult> Import(bool manager, params PrepSheetItem[] items) =>
        _pos.Send(new ImportPrepItemsCommand(items, manager ? "staff-omar" : "staff-mona", manager ? "Omar" : "Mona", manager));

    private async Task<RawMaterial> Material(string name) => (await _pos.Inventory.GetAllAsync()).Single(m => m.Name == name);

    // The materials a kitchen has already: butter by weight, salt by the piece (a packet).
    private async Task Stocked()
    {
        await _pos.Send(new CreateRawMaterialCommand("زبدة_فيرن_", UnitOfMeasure.Gram));
        await _pos.Send(new CreateRawMaterialCommand("ملح_", UnitOfMeasure.Piece));
        await _pos.Send(new CreateRawMaterialCommand("بيض", UnitOfMeasure.Piece));
    }

    // كبه, made with its dough, comes first on the sheet: its dough is made before it.
    private static readonly PrepSheetItem Kibbeh = new(2, "كبه", [Line(3, "عجين كبه", "جرام", "50"), Line(4, "حشو كبه", "جرام", "25")]);
    private static readonly PrepSheetItem Dough = new(5, "عجين كبه", [Line(6, "برغل", "كيلو", "1")]);
    private static readonly PrepSheetItem Filling = new(7, "حشو كبه", [Line(8, "لحم", "كيلو", "0.5")]);

    [Fact]
    public async Task A_managers_sheet_makes_prep_items_their_recipes_and_the_materials_they_need()
    {
        await Stocked();

        var result = await Import(true,
            new PrepSheetItem(10, "صوص لبن", [
                Line(11, "زبده فيرن", "كيلو", "0.2"),
                Line(12, "كريمه", "لتر", "0.3"),
                Line(13, "زبدة_فيرن_", "كيلو", "0.05"),
                Line(14, "بيض", "عدد", "4"),
                Line(15, "ملح_", "كيلو", "0.025"),
                Line(16, "ماء", "لتر", "0"),
                Line(17, "حبهان", "جرام", "0.03"),
                Line(18, "حمص", null, "4"),
                Line(19, "شبت", "ربطه", "1"),
            ]),
            Kibbeh, Dough, Filling,
            new PrepSheetItem(20, "صوص كرز", []));

        Assert.Empty(result.Skipped);
        Assert.Equal("مصنعات", result.CategoryAdded);
        // Made after what they are made with.
        Assert.Equal(["عجين كبه", "حشو كبه", "كبه", "صوص لبن", "صوص كرز"], result.Imported.Select(i => i.Name));
        Assert.All(result.Imported, i => Assert.False(i.Requested));

        // The sauce: butter found under either spelling and its two lines added together, the cream
        // and cardamom added as materials, the eggs in pieces; salt (kept in pieces, weighed in
        // kilos), water with no quantity and chickpeas with no unit left out and said.
        var sauce = result.Imported.Single(i => i.Name == "صوص لبن");
        Assert.Equal((550.03m, 5), (sauce.BatchQuantity, sauce.Ingredients));
        Assert.Equal(
            [
                "Makes 550.03 g a batch: the weight of its ingredients. Correct it where cooking changes the weight.",
                "زبدة_فيرن_ is on it twice: the quantities are added together.",
                "ملح_ 0.025 كيلو: the till keeps ملح_ in pieces, left out.",
                "ماء 0 لتر: no quantity, left out.",
                "حبهان 0.03 جرام: taken as 0.03 g. Check it is not kilos.",
                "حمص 4: no unit, left out.",
            ],
            sauce.Notes);
        var recipe = await _pos.Inventory.PrepRecipes.GetByIdAsync(sauce.Id);
        var butter = await Material("زبدة_فيرن_");
        Assert.Equal(250m, recipe!.Ingredients.Single(i => i.RawMaterialId == butter.Id).Quantity);
        var eggs = await Material("بيض");
        Assert.Equal(4m, recipe.Ingredients.Single(i => i.RawMaterialId == eggs.Id).Quantity);

        // Each a prep item in grams, under المصنعات.
        var made = await Material("صوص لبن");
        var category = (await _pos.Inventory.PurchaseCategories.GetAllAsync()).Single(c => c.Name == "مصنعات");
        Assert.Equal((MaterialKind.Prep, UnitOfMeasure.Gram, category.Id), (made.Kind, made.Unit, made.CategoryId));

        // كبه is made with the dough and filling made just before it: 75 g a piece.
        var kibbeh = result.Imported.Single(i => i.Name == "كبه");
        Assert.Equal((75m, 2), (kibbeh.BatchQuantity, kibbeh.Ingredients));
        var kibbehRecipe = await _pos.Inventory.PrepRecipes.GetByIdAsync(kibbeh.Id);
        Assert.Equal((await Material("عجين كبه")).Id, kibbehRecipe!.Ingredients.Single(i => i.Quantity == 50).RawMaterialId);

        // Nothing to weigh: 1 kg until set.
        var cherry = result.Imported.Single(i => i.Name == "صوص كرز");
        Assert.Equal((1000m, 0, "Nothing on its recipe to weigh: 1 kg a batch until it is set."), (cherry.BatchQuantity, cherry.Ingredients, cherry.Notes.Single()));

        // The materials added: purchased stock items in the sheet's unit, no price yet.
        Assert.Equal(
            [("برغل", UnitOfMeasure.Gram), ("لحم", UnitOfMeasure.Gram), ("كريمه", UnitOfMeasure.Millilitre), ("حبهان", UnitOfMeasure.Gram), ("شبت", UnitOfMeasure.Piece)],
            result.MaterialsAdded.Select(m => (m.Name, m.Unit)));
        var cream = await Material("كريمه");
        Assert.Equal((MaterialKind.Purchased, (decimal?)null), (cream.Kind, cream.AverageCost));

        // The same sheet again: all there already, nothing added.
        var again = await Import(true, Kibbeh, Dough, Filling);
        Assert.Empty(again.Imported);
        Assert.Empty(again.MaterialsAdded);
        Assert.Equal(["كبه is already a prep item.", "عجين كبه is already a prep item.", "حشو كبه is already a prep item."], again.Skipped.Select(s => s.Reason));
    }

    [Fact]
    public async Task A_name_twice_on_the_sheet_or_already_taken_is_skipped()
    {
        await Stocked();

        var result = await Import(true,
            new PrepSheetItem(2, "صوص بيتزا", [Line(3, "زبدة_فيرن_", "كيلو", "1")]),
            new PrepSheetItem(4, "صوص_بيتزا", [Line(5, "زبدة_فيرن_", "كيلو", "2")]),
            new PrepSheetItem(6, "بيض", [Line(7, "زبدة_فيرن_", "كيلو", "1")]),
            new PrepSheetItem(8, null, [Line(8, "زبدة_فيرن_", "كيلو", "1")]));

        Assert.Equal(["صوص بيتزا"], result.Imported.Select(i => i.Name));
        Assert.Equal(
            [
                (4, "Also on row 2, which is the one imported."),
                (6, "بيض is already a raw material: a prep item cannot take its name."),
                (8, "Ingredients with no prep item named above them."),
            ],
            result.Skipped.Select(s => (s.Row, s.Reason)));
    }

    [Fact]
    public async Task An_accountants_sheet_asks_a_manager_for_each_and_one_made_with_another_waits_for_it()
    {
        var result = await Import(false, Kibbeh, Dough, Filling);

        // The dough and filling asked for; كبه waits for them. The materials they need are added.
        Assert.Equal([("عجين كبه", true), ("حشو كبه", true)], result.Imported.Select(i => (i.Name, i.Requested)));
        Assert.Equal((2, "Made with عجين كبه, which waits for a manager's approval: import the sheet again once it is approved."),
            (result.Skipped.Single().Row, result.Skipped.Single().Reason));
        Assert.Equal(["برغل", "لحم"], result.MaterialsAdded.Select(m => m.Name));
        Assert.DoesNotContain(await _pos.Inventory.GetAllAsync(), m => m.IsPrepItem);
        var pending = (await _pos.Send(new GetApprovalRequestsQuery())).Pending;
        Assert.All(pending, r => Assert.Equal((ApprovalModule.PrepItem, ApprovalAction.Create, "Mona"), (r.Module, r.Action, r.RequestedBy)));
        Assert.Equal(2, pending.Count);

        // Imported again before a decision: what was asked is not asked twice.
        var again = await Import(false, Kibbeh, Dough, Filling);
        Assert.Empty(again.Imported);
        Assert.Contains(again.Skipped, s => s.Reason == "عجين كبه is already waiting for a manager's approval.");

        // Approved, and imported again: كبه is asked for now.
        foreach (var request in pending)
            await _pos.Send(new ApproveRequestCommand(request.Id, "Omar"));
        var third = await Import(false, Kibbeh, Dough, Filling);
        Assert.Equal([("كبه", true)], third.Imported.Select(i => (i.Name, i.Requested)));
        Assert.Equal(2, third.Skipped.Count);
    }

    [Fact]
    public async Task An_empty_sheet_is_refused()
    {
        var empty = await Assert.ThrowsAsync<DomainException>(() => Import(true));
        Assert.StartsWith("The sheet has no prep items on it", empty.Message);
    }
}
