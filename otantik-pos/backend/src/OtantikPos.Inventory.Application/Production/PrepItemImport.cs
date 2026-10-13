using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Categories;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.Production;

// Prep items brought in from a spreadsheet of batch recipes (المصنعات): each prep item named on a
// row of its own, then its ingredients, a unit and a quantity for one batch each.
//
// Every one is a prep item, counted in grams and costed per kg; its batch is the weight of its
// ingredients (litres taken as kilos), for the kitchen to correct where cooking changes it. It
// is filed under المصنعات, added when not there. An ingredient is a material already in the till,
// another prep item on the sheet (made first), or else added as a purchased raw material with no
// price yet, in the sheet's unit. A line the till cannot read or convert (salt kept in pieces,
// weighed in kilos) is left out and said, as is a line with no quantity.
//
// A prep item whose name is taken, or already asked for, is skipped; so is the same name again on
// the sheet. A manager's import makes them at once; anyone else's asks a manager for each, as
// adding one by hand does: one made from another that is waiting for approval is skipped, to be
// imported again once that is approved.

// The sheet as read: each prep item by the row its name is on (no name: ingredients above any),
// and its ingredient rows as written.
public sealed record PrepSheetLine(int Row, string? Ingredient, string? Unit, string? Quantity);

public sealed record PrepSheetItem(int Row, string? Name, IReadOnlyList<PrepSheetLine> Lines);

// One prep item made, or asked of a manager (Requested): its batch in grams, how many ingredients
// its recipe has, and what to check.
public sealed record ImportedPrepItem(
    int Row, Guid Id, string Name, decimal BatchQuantity, int Ingredients, bool Requested, IReadOnlyList<string> Notes);

public sealed record AddedRawMaterial(Guid Id, string Name, UnitOfMeasure Unit);

public sealed record PrepImportResult(
    IReadOnlyList<ImportedPrepItem> Imported,
    IReadOnlyList<SkippedSheetRow> Skipped,
    IReadOnlyList<AddedRawMaterial> MaterialsAdded,
    string? CategoryAdded);

public sealed record ImportPrepItemsCommand(IReadOnlyList<PrepSheetItem> Items, string StaffId, string StaffName, bool CanApprove)
    : IRequest<PrepImportResult>;

internal sealed class ImportPrepItemsHandler(
    IRawMaterialRepository materials,
    IPurchaseCategoryRepository categories,
    IApprovalRequestRepository requests,
    PrepItemWriter writer,
    IUnitOfWork unitOfWork) : IRequestHandler<ImportPrepItemsCommand, PrepImportResult>
{
    public const int MaxItems = 500;
    public const string CategoryName = "مصنعات";
    private const int MaxNameLength = 200;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private enum Source
    {
        Existing,
        Added,
        OnSheet,
    }

    // What one line of a recipe is made of, in its own unit.
    private sealed record Ingredient(Source Source, string Key, string Name, UnitOfMeasure Unit, RawMaterial? Material);

    private sealed class Plan(int row, string name, string key)
    {
        public int Row { get; } = row;
        public string Name { get; } = name;
        public string Key { get; } = key;
        public Dictionary<string, (Ingredient Ingredient, decimal Quantity)> Lines { get; } = [];
        public List<string> Notes { get; } = [];
        public string? Blocked { get; set; }
    }

    public async Task<PrepImportResult> Handle(ImportPrepItemsCommand request, CancellationToken cancellationToken)
    {
        if (request.Items.Count == 0)
            throw new DomainException("The sheet has no prep items on it: a prep item's name on a row of its own, then its ingredients.");
        if (request.Items.Count > MaxItems)
            throw new DomainException($"Import at most {MaxItems} prep items at a time: split the sheet.");

        // Names already taken, deleted materials' too; a live one before a deleted one of its name.
        var taken = new Dictionary<string, RawMaterial>();
        foreach (var material in (await materials.GetAllAsync(cancellationToken)).OrderBy(m => m.IsArchived))
            taken.TryAdd(SheetText.Key(material.Name), material);
        // Prep items already asked for and not decided yet.
        var asked = new HashSet<string>();
        foreach (var pending in await requests.GetPendingAsync(cancellationToken))
            if (pending is { Module: ApprovalModule.PrepItem, Action: ApprovalAction.Create })
                asked.Add(SheetText.Key(ApprovalPayload.Read<PrepItemChange>(pending).Name));

        var skipped = new List<SkippedSheetRow>();
        var plans = Plans(request.Items, taken, asked, skipped);
        var onSheet = plans.ToDictionary(p => p.Key);
        var added = new Dictionary<string, RawMaterial>();
        foreach (var plan in plans)
            Read(plan, request.Items.Single(i => i.Row == plan.Row), onSheet, taken, asked, added);

        var ordered = Ordered(plans, onSheet, skipped);
        foreach (var plan in ordered.Where(p => p.Blocked is not null).ToList())
        {
            skipped.Add(new SkippedSheetRow(plan.Row, plan.Name, plan.Blocked!));
            ordered.Remove(plan);
        }

        // The category and the materials the recipes need go in first: a recipe names them.
        var usedAdded = ordered.SelectMany(p => p.Lines.Values).Where(l => l.Ingredient.Source == Source.Added)
            .Select(l => l.Ingredient.Material!).Distinct().ToList();
        PurchaseCategory? category = null;
        string? categoryAdded = null;
        if (ordered.Count > 0)
        {
            var key = PurchaseCategory.Normalize(CategoryName);
            category = (await categories.GetAllAsync(cancellationToken))
                .FirstOrDefault(c => PurchaseCategory.Normalize(c.Name) == key || (c.NameAr is { } ar && PurchaseCategory.Normalize(ar) == key));
            if (category is null)
            {
                categories.Add(category = new PurchaseCategory(Guid.NewGuid(), CategoryName));
                categoryAdded = category.Name;
            }

            var general = await categories.ResolveAsync(null, cancellationToken);
            foreach (var material in usedAdded)
            {
                material.Describe(null, general.Id);
                materials.Add(material);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var imported = new List<ImportedPrepItem>();
        var made = new Dictionary<string, Guid>();
        var waiting = new HashSet<string>();
        foreach (var plan in ordered)
        {
            var entries = new List<PrepIngredientEntry>();
            string? missing = null;
            foreach (var (ingredient, quantity) in plan.Lines.Values)
            {
                if (ingredient.Source != Source.OnSheet)
                    entries.Add(new PrepIngredientEntry(ingredient.Material!.Id, quantity));
                else if (made.TryGetValue(ingredient.Key, out var id))
                    entries.Add(new PrepIngredientEntry(id, quantity));
                else
                    missing ??= waiting.Contains(ingredient.Key)
                        ? $"Made with {ingredient.Name}, which waits for a manager's approval: import the sheet again once it is approved."
                        : $"Made with {ingredient.Name}, which was not imported.";
            }

            if (missing is not null)
            {
                skipped.Add(new SkippedSheetRow(plan.Row, plan.Name, missing));
                continue;
            }

            var weight = plan.Lines.Values.Where(l => l.Ingredient.Unit != UnitOfMeasure.Piece).Sum(l => l.Quantity);
            var batch = weight > 0 ? Math.Round(weight, 3) : 1000m;
            plan.Notes.Insert(0, weight > 0
                ? $"Makes {PrepItemCosting.Readable(batch, UnitOfMeasure.Gram)} a batch: the weight of its ingredients. Correct it where cooking changes the weight."
                : "Nothing on its recipe to weigh: 1 kg a batch until it is set.");
            var change = new PrepItemChange(plan.Name, UnitOfMeasure.Gram, batch, entries, CategoryId: category!.Id);
            try
            {
                await writer.CheckAsync(change, null, cancellationToken);
                Guid id;
                if (request.CanApprove)
                {
                    var (item, _, _) = await writer.SaveAsync(change, null, Guid.NewGuid(), cancellationToken);
                    id = item.Id;
                    made[plan.Key] = id;
                }
                else
                {
                    var (summary, changes) = await writer.DescribeAsync(change, null, cancellationToken);
                    var asking = ApprovalRequest.AskToChange(
                        Guid.NewGuid(), ApprovalModule.PrepItem, ApprovalAction.Create, Guid.NewGuid(), summary, changes,
                        ApprovalPayload.Write(change), "Imported from Excel", request.StaffId, request.StaffName);
                    requests.Add(asking);
                    id = asking.RecordId;
                    waiting.Add(plan.Key);
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                imported.Add(new ImportedPrepItem(plan.Row, id, plan.Name, batch, entries.Count, !request.CanApprove, plan.Notes));
            }
            catch (DomainException error)
            {
                skipped.Add(new SkippedSheetRow(plan.Row, plan.Name, error.Message));
            }
        }

        return new PrepImportResult(
            imported,
            skipped.OrderBy(s => s.Row).ToList(),
            usedAdded.Select(m => new AddedRawMaterial(m.Id, m.Name, m.Unit)).ToList(),
            categoryAdded);
    }

    // The prep items to make, by their names: a name taken, asked for, or on the sheet already is
    // skipped and said why.
    private static List<Plan> Plans(
        IReadOnlyList<PrepSheetItem> items, Dictionary<string, RawMaterial> taken, HashSet<string> asked, List<SkippedSheetRow> skipped)
    {
        var plans = new List<Plan>();
        var first = new Dictionary<string, int>();
        foreach (var item in items.OrderBy(i => i.Row))
        {
            var name = SheetText.Clean(item.Name);
            string? Problem()
            {
                if (name is null)
                    return "Ingredients with no prep item named above them.";
                if (name.Length > MaxNameLength)
                    return $"The name is longer than {MaxNameLength} characters.";
                var key = SheetText.Key(name);
                if (first.TryGetValue(key, out var row))
                    return $"Also on row {row}, which is the one imported.";
                first[key] = item.Row;
                if (taken.TryGetValue(key, out var existing))
                    return existing.IsArchived ? $"{existing.Name} was deleted: restore it rather than import it again."
                        : existing.IsPrepItem ? $"{existing.Name} is already a prep item."
                        : $"{existing.Name} is already a raw material: a prep item cannot take its name.";
                return asked.Contains(key) ? $"{name} is already waiting for a manager's approval." : null;
            }

            if (Problem() is { } reason)
                skipped.Add(new SkippedSheetRow(item.Row, name, reason));
            else
                plans.Add(new Plan(item.Row, name!, SheetText.Key(name!)));
        }

        return plans;
    }

    // Its recipe: each line's ingredient found or added, in its unit, the same one twice added
    // together; a line that cannot go on it is left out and said why.
    private static void Read(
        Plan plan, PrepSheetItem item, Dictionary<string, Plan> onSheet, Dictionary<string, RawMaterial> taken, HashSet<string> asked,
        Dictionary<string, RawMaterial> added)
    {
        foreach (var line in item.Lines)
        {
            var name = SheetText.Clean(line.Ingredient);
            var written = SheetText.Clean(line.Quantity);
            var unitText = SheetText.Clean(line.Unit);
            var said = $"{name} {written} {unitText}".Trim();
            if (name is null)
            {
                plan.Notes.Add($"Row {line.Row}: no ingredient named, left out.");
                continue;
            }

            if (written is null || SheetText.Amount(written) is not { } quantity || quantity <= 0)
            {
                plan.Notes.Add($"{said}: no quantity, left out.");
                continue;
            }

            if (Unit(unitText) is not { } unit)
            {
                plan.Notes.Add(unitText is null ? $"{said}: no unit, left out." : $"{said}: the till does not know the unit \"{unitText}\", left out.");
                continue;
            }

            if (unit.Unit == UnitOfMeasure.Gram && unit.Factor == 1 && quantity < 1)
                plan.Notes.Add($"{said}: taken as {quantity.ToString("0.###", Invariant)} g. Check it is not kilos.");

            var key = SheetText.Key(name);
            Ingredient ingredient;
            if (key == plan.Key)
            {
                plan.Notes.Add($"{said}: a prep item is not made from itself, left out.");
                continue;
            }

            if (onSheet.TryGetValue(key, out var other))
                ingredient = new Ingredient(Source.OnSheet, key, other.Name, UnitOfMeasure.Gram, null);
            else if (asked.Contains(key))
            {
                plan.Blocked ??= $"Made with {name}, which waits for a manager's approval: import the sheet again once it is approved.";
                continue;
            }
            else if (taken.TryGetValue(key, out var material))
            {
                if (material.IsArchived)
                {
                    plan.Notes.Add($"{said}: {material.Name} was deleted, left out.");
                    continue;
                }

                ingredient = new Ingredient(Source.Existing, key, material.Name, material.Unit, material);
            }
            else
            {
                if (!added.TryGetValue(key, out var adding))
                    added[key] = adding = new RawMaterial(name, unit.Unit);
                ingredient = new Ingredient(Source.Added, key, adding.Name, adding.Unit, adding);
            }

            // Weights and volumes go together, a litre as a kilo; pieces only with pieces.
            if ((ingredient.Unit == UnitOfMeasure.Piece) != (unit.Unit == UnitOfMeasure.Piece))
            {
                plan.Notes.Add($"{said}: the till keeps {ingredient.Name} in {Kept(ingredient.Unit)}, left out.");
                continue;
            }

            var amount = quantity * unit.Factor;
            if (plan.Lines.TryGetValue(key, out var before))
            {
                plan.Lines[key] = (ingredient, before.Quantity + amount);
                plan.Notes.Add($"{ingredient.Name} is on it twice: the quantities are added together.");
            }
            else
            {
                plan.Lines[key] = (ingredient, amount);
            }
        }
    }

    // Each prep item after those it is made with. Ones made with each other are skipped.
    private static List<Plan> Ordered(List<Plan> plans, Dictionary<string, Plan> onSheet, List<SkippedSheetRow> skipped)
    {
        var ordered = new List<Plan>();
        var state = new Dictionary<string, bool>();
        bool Visit(Plan plan, Stack<string> path)
        {
            if (state.TryGetValue(plan.Key, out var done))
                return done;
            if (path.Contains(plan.Key))
                return false;
            path.Push(plan.Key);
            var ok = true;
            foreach (var line in plan.Lines.Values.Where(l => l.Ingredient.Source == Source.OnSheet))
                ok &= Visit(onSheet[line.Ingredient.Key], path);
            path.Pop();
            state[plan.Key] = ok;
            if (ok)
                ordered.Add(plan);
            else
                skipped.Add(new SkippedSheetRow(plan.Row, plan.Name, "Made with a prep item that is itself made with it: put one of them right."));
            return ok;
        }

        foreach (var plan in plans)
            Visit(plan, new Stack<string>());
        return ordered;
    }

    // The units a recipe sheet is written in, English or Arabic: what the quantity is in, and how
    // many grams, millilitres or pieces one of it is.
    private static (UnitOfMeasure Unit, decimal Factor)? Unit(string? text) => text is null ? null : SheetText.Key(text).TrimEnd('.') switch
    {
        "كيلو" or "كجم" or "كغ" or "كيلوجرام" or "كيلوغرام" or "KG" or "KGS" or "KILO" or "KILOGRAM" => (UnitOfMeasure.Gram, 1000m),
        "جرام" or "جم" or "غرام" or "جرامات" or "G" or "GM" or "GRAM" or "GRAMS" => (UnitOfMeasure.Gram, 1m),
        "لتر" or "L" or "LT" or "LTR" or "LITER" or "LITRE" or "LITERS" or "LITRES" => (UnitOfMeasure.Millilitre, 1000m),
        "مل" or "ملي" or "ملليلتر" or "ML" => (UnitOfMeasure.Millilitre, 1m),
        "عدد" or "قطعه" or "حبه" or "ربطه" or "بالكو" or "علبه" or "كيس" or "PIECE" or "PIECES" or "PC" or "PCS" or "UNIT" => (UnitOfMeasure.Piece, 1m),
        _ => null,
    };

    private static string Kept(UnitOfMeasure unit) => unit switch
    {
        UnitOfMeasure.Gram => "grams",
        UnitOfMeasure.Millilitre => "millilitres",
        _ => "pieces",
    };
}
