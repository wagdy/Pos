using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Application.Tests;

// Raw materials brought in from a spreadsheet: each good row a purchased stock item, filed under
// its category (added when new); a name already taken skipped, never made twice; a bad row said why.
public class RawMaterialImportTests
{
    private readonly TestPos _pos = new();

    private Task<RawMaterialImportResult> Import(params RawMaterialSheetRow[] rows) => _pos.Send(new ImportRawMaterialsCommand(rows));

    private async Task<RawMaterial> Material(string name) => (await _pos.Inventory.GetAllAsync()).Single(m => m.Name == name);

    [Fact]
    public async Task Each_good_row_is_a_purchased_stock_item_in_its_unit_category_and_price()
    {
        await _pos.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), "Vegetables", "خضروات"));

        var result = await Import(
            new(2, "Chicken breast", "Poultry", "Kg", "185"),
            new(3, " Full-cream   milk ", "Dairy", "لتر", "42.5"),
            new(4, "Eggs", " dairy ", "pcs", "٤٫٢٥"),
            new(5, "Parsley", "خضروات", "كيلو", null),
            new(6, "Paper cups", null, "Piece", "1,250"));

        Assert.Empty(result.Skipped);
        Assert.Equal(["Chicken breast", "Full-cream milk", "Eggs", "Parsley", "Paper cups"], result.Created.Select(m => m.Name));
        // New categories added once each; one already there, named in Arabic, found.
        Assert.Equal(["Poultry", "Dairy"], result.CategoriesAdded);
        Assert.Equal(["Poultry", "Dairy", "Dairy", "Vegetables", PurchaseCategory.GeneralName], result.Created.Select(m => m.Category));

        // Stock items, counted in grams, millilitres or pieces and priced per kg, litre or piece.
        var chicken = await Material("Chicken breast");
        Assert.Equal((MaterialKind.Purchased, UnitOfMeasure.Gram, "kg", 1000m, (decimal?)185m),
            (chicken.Kind, chicken.Unit, chicken.PurchaseUnit, chicken.PurchaseUnitSize, chicken.CostPerPurchaseUnit));
        var milk = await Material("Full-cream milk");
        Assert.Equal((UnitOfMeasure.Millilitre, "L", (decimal?)42.5m), (milk.Unit, milk.PurchaseUnit, milk.CostPerPurchaseUnit));
        var eggs = await Material("Eggs");
        Assert.Equal((UnitOfMeasure.Piece, "piece", (decimal?)4.25m), (eggs.Unit, eggs.PurchaseUnit, eggs.CostPerPurchaseUnit));
        Assert.Equal((decimal?)1_250m, (await Material("Paper cups")).CostPerPurchaseUnit);
        // No cost: no price until a purchase brings one. No stock either way.
        var parsley = await Material("Parsley");
        Assert.Equal(((decimal?)null, 0m), (parsley.AverageCost, parsley.QuantityOnHand));
        Assert.All(await _pos.Inventory.GetAllAsync(), m => Assert.Equal(0m, m.QuantityOnHand));
    }

    [Fact]
    public async Task A_name_already_taken_is_skipped_never_made_twice_and_a_bad_row_says_why()
    {
        await _pos.Send(new CreateRawMaterialCommand("Tomatoes", UnitOfMeasure.Gram, CostPerPurchaseUnit: 15));
        var salt = await _pos.Send(new CreateRawMaterialCommand("Salt", UnitOfMeasure.Gram));
        (await _pos.Inventory.GetByIdAsync(salt.Id))!.Archive("Omar");
        await _pos.Send(new SavePurchaseCategoryCommand(Guid.NewGuid(), "Utilities", null, ExpenseLine.Controllable, CategoryKind.Expenses));

        var result = await Import(
            new(2, "tomatoes ", "Vegetables", "Kg", "20"),
            new(3, "SALT", null, "Kg", null),
            new(4, "Rice", "Dry goods", "Kg", "30"),
            new(5, " rice", "Dry goods", "Kg", "31"),
            new(6, null, "Dry goods", "Kg", "5"),
            new(7, "Flour", "Dry goods", "Sack", "500"),
            new(8, "Sugar", "Dry goods", null, null),
            new(9, "Oil", "Dry goods", "Liter", "abc"),
            new(10, "Vinegar", "Dry goods", "Liter", "-5"),
            new(11, new string('x', 201), null, "Kg", null),
            new(12, "Charcoal", "utilities", "Kg", null));

        Assert.Equal([(4, "Rice")], result.Created.Select(m => (m.Row, m.Name)));
        Assert.Equal(
            [
                (2, "Tomatoes is already on the list."),
                (3, "Salt was deleted: restore it from the deleted materials rather than import it again."),
                (5, "Also on row 4, which is the one imported."),
                (6, "No material name."),
                (7, "\"Sack\" is not a unit the till knows: Kg, Liter or Piece."),
                (8, "No unit of measure: Kg, Liter or Piece."),
                (9, "\"abc\" is not a cost: write a number, such as 85.50."),
                (10, "A cost cannot be negative."),
                (11, "The name is longer than 200 characters."),
                (12, "Utilities is a general expenses category: give it a raw materials category."),
            ],
            result.Skipped.Select(s => (s.Row, s.Reason)));
        // A skipped row adds no category; the one already there keeps its price.
        Assert.Equal(["Dry goods"], result.CategoriesAdded);
        Assert.Equal((decimal?)15m, (await Material("Tomatoes")).CostPerPurchaseUnit);
        Assert.Single(await _pos.Inventory.GetAllAsync(), m => m.Name == "Tomatoes");

        // The same sheet again: everything is there already.
        var again = await Import(new RawMaterialSheetRow(4, "Rice", "Dry goods", "Kg", "30"));
        Assert.Equal((0, "Rice is already on the list."), (again.Created.Count, again.Skipped.Single().Reason));
    }

    [Fact]
    public async Task An_empty_or_huge_sheet_is_refused()
    {
        var empty = await Assert.ThrowsAsync<DomainException>(() => Import());
        Assert.Equal("The sheet has no materials on it: fill in a row under the headings.", empty.Message);
        var rows = Enumerable.Range(2, 2_001).Select(r => new RawMaterialSheetRow(r, $"Material {r}", null, "Kg", null)).ToArray();
        await Assert.ThrowsAsync<DomainException>(() => Import(rows));
        Assert.Empty(await _pos.Inventory.GetAllAsync());
    }
}
