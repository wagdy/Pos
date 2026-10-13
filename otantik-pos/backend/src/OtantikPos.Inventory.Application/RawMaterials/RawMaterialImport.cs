using System.Globalization;
using System.Text;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.RawMaterials;

// Raw materials brought in from a spreadsheet, the template's (GET .../raw-materials/template): a
// name, a category, the unit it is bought in, and the price to start from when known. Every one is
// a purchased stock item, for recipes, purchases and stock counts; never an expense item, which is
// a thing of its own.
//
// Each row is read and checked on its own: a good row is made, a bad one skipped and said why, by
// its row number on the sheet. A name already taken, capitals and spacing aside and deleted
// materials included, is skipped rather than made twice: a second Tomatoes would split the stock
// and the average cost between them. The same name twice on the sheet: the first row is made.
// A category not on the list yet is added, once.

// One row as read from the sheet: its number there, and its cells as they were written.
public sealed record RawMaterialSheetRow(int Row, string? Name, string? Category, string? Unit, string? InitialCost);

public sealed record ImportedRawMaterial(
    int Row, Guid Id, string Name, string Category, UnitOfMeasure Unit, string PurchaseUnit, decimal? CostPerPurchaseUnit);

public sealed record SkippedSheetRow(int Row, string? Name, string Reason);

public sealed record RawMaterialImportResult(
    IReadOnlyList<ImportedRawMaterial> Created, IReadOnlyList<SkippedSheetRow> Skipped, IReadOnlyList<string> CategoriesAdded);

public sealed record ImportRawMaterialsCommand(IReadOnlyList<RawMaterialSheetRow> Rows) : IRequest<RawMaterialImportResult>;

// The units the sheet takes, in English or Arabic: what a material is bought and priced in. A
// material bought by the kg is counted in grams, by the litre in millilitres, by the piece in
// pieces; its initial cost is per kg, litre or piece.
public static class SheetUnits
{
    public static readonly IReadOnlyList<string> Names = ["Kg", "Liter", "Piece"];

    private static readonly Dictionary<string, UnitOfMeasure> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kg"] = UnitOfMeasure.Gram,
        ["kgs"] = UnitOfMeasure.Gram,
        ["kilo"] = UnitOfMeasure.Gram,
        ["kilos"] = UnitOfMeasure.Gram,
        ["kilogram"] = UnitOfMeasure.Gram,
        ["kilograms"] = UnitOfMeasure.Gram,
        ["كيلو"] = UnitOfMeasure.Gram,
        ["كجم"] = UnitOfMeasure.Gram,
        ["كغ"] = UnitOfMeasure.Gram,
        ["كيلوجرام"] = UnitOfMeasure.Gram,
        ["كيلوغرام"] = UnitOfMeasure.Gram,
        ["liter"] = UnitOfMeasure.Millilitre,
        ["liters"] = UnitOfMeasure.Millilitre,
        ["litre"] = UnitOfMeasure.Millilitre,
        ["litres"] = UnitOfMeasure.Millilitre,
        ["l"] = UnitOfMeasure.Millilitre,
        ["lt"] = UnitOfMeasure.Millilitre,
        ["ltr"] = UnitOfMeasure.Millilitre,
        ["لتر"] = UnitOfMeasure.Millilitre,
        ["piece"] = UnitOfMeasure.Piece,
        ["pieces"] = UnitOfMeasure.Piece,
        ["pc"] = UnitOfMeasure.Piece,
        ["pcs"] = UnitOfMeasure.Piece,
        ["unit"] = UnitOfMeasure.Piece,
        ["units"] = UnitOfMeasure.Piece,
        ["قطعة"] = UnitOfMeasure.Piece,
        ["حبة"] = UnitOfMeasure.Piece,
        ["عدد"] = UnitOfMeasure.Piece,
    };

    public static UnitOfMeasure? Parse(string? text) =>
        text is null ? null : ByName.TryGetValue(text.Trim().TrimEnd('.'), out var unit) ? unit : null;
}

internal sealed class ImportRawMaterialsHandler(IRawMaterialRepository materials, IPurchaseCategoryRepository categories, IUnitOfWork unitOfWork)
    : IRequestHandler<ImportRawMaterialsCommand, RawMaterialImportResult>
{
    public const int MaxRows = 2000;
    private const int MaxNameLength = 200;

    public async Task<RawMaterialImportResult> Handle(ImportRawMaterialsCommand request, CancellationToken cancellationToken)
    {
        if (request.Rows.Count == 0)
            throw new DomainException("The sheet has no materials on it: fill in a row under the headings.");
        if (request.Rows.Count > MaxRows)
            throw new DomainException($"Import at most {MaxRows:N0} materials at a time: split the sheet.");

        // Names already taken: deleted materials' too, which are restored rather than made again.
        var taken = new Dictionary<string, RawMaterial>();
        foreach (var material in await materials.GetAllAsync(cancellationToken))
            taken.TryAdd(PurchaseCategory.Normalize(material.Name), material);
        // Categories by their name, English or Arabic.
        var known = new Dictionary<string, PurchaseCategory>();
        foreach (var category in await categories.GetAllAsync(cancellationToken))
        {
            known.TryAdd(PurchaseCategory.Normalize(category.Name), category);
            if (category.NameAr is { } ar)
                known.TryAdd(PurchaseCategory.Normalize(ar), category);
        }

        var created = new List<ImportedRawMaterial>();
        var skipped = new List<SkippedSheetRow>();
        var added = new List<string>();
        var onSheet = new Dictionary<string, int>();
        foreach (var row in request.Rows.OrderBy(r => r.Row))
        {
            var name = Clean(row.Name);
            string? Problem()
            {
                if (name is null)
                    return "No material name.";
                if (name.Length > MaxNameLength)
                    return $"The name is longer than {MaxNameLength} characters.";
                var key = PurchaseCategory.Normalize(name);
                if (taken.TryGetValue(key, out var existing))
                    return existing.IsArchived
                        ? $"{existing.Name} was deleted: restore it from the deleted materials rather than import it again."
                        : $"{existing.Name} is already on the list.";
                if (onSheet.TryGetValue(key, out var first))
                    return $"Also on row {first}, which is the one imported.";
                if (SheetUnits.Parse(row.Unit) is null)
                    return Clean(row.Unit) is { } unit
                        ? $"\"{unit}\" is not a unit the till knows: Kg, Liter or Piece."
                        : "No unit of measure: Kg, Liter or Piece.";
                if (Clean(row.InitialCost) is { } cost)
                {
                    if (Amount(cost) is not { } amount)
                        return $"\"{cost}\" is not a cost: write a number, such as 85.50.";
                    if (amount < 0)
                        return "A cost cannot be negative.";
                }

                return Clean(row.Category)?.Length > 100 ? "The category's name is longer than 100 characters." : null;
            }

            if (Problem() is { } reason)
            {
                skipped.Add(new SkippedSheetRow(row.Row, name, reason));
                continue;
            }

            var made = new RawMaterial(name!, SheetUnits.Parse(row.Unit)!.Value);
            var filed = await CategoryAsync(Clean(row.Category), known, added, cancellationToken);
            made.Describe(null, filed.Id);
            var initialCost = Clean(row.InitialCost) is { } written ? Amount(written) : null;
            if (initialCost is { } price)
                made.SetCost(price);
            materials.Add(made);
            onSheet[PurchaseCategory.Normalize(made.Name)] = row.Row;
            created.Add(new ImportedRawMaterial(row.Row, made.Id, made.Name, filed.Name, made.Unit, made.PurchaseUnit, initialCost));
        }

        if (created.Count > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);
        return new RawMaterialImportResult(created, skipped, added);
    }

    // The category of that name, English or Arabic; one added when there is none; General when
    // the cell is blank.
    private async Task<PurchaseCategory> CategoryAsync(
        string? name, Dictionary<string, PurchaseCategory> known, List<string> added, CancellationToken cancellationToken)
    {
        if (name is null)
            return await categories.ResolveAsync(null, cancellationToken);
        if (known.TryGetValue(PurchaseCategory.Normalize(name), out var found))
            return found;
        var category = new PurchaseCategory(Guid.NewGuid(), name);
        categories.Add(category);
        known[PurchaseCategory.Normalize(name)] = category;
        added.Add(category.Name);
        return category;
    }

    // A cell as a person reads it: trimmed, and null when empty.
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // A number as written, in Western or Arabic digits, with or without thousands separators.
    private static decimal? Amount(string text)
    {
        var western = new StringBuilder(text.Length);
        foreach (var c in text)
            western.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                '٫' => '.',
                '٬' => ',',
                _ => c,
            });
        return decimal.TryParse(western.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }
}
