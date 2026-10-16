using System.Globalization;
using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.RawMaterials;

namespace OtantikPos.Inventory.Application.RawMaterials;

// A bought material counted in another unit (عدّ بـ), as when it was created with the wrong one.
// While nothing records it, the unit simply changes, for anyone, with the unit it is bought in, its
// price and reorder level as given for the new one. Once something does (the ledger, an invoice, a
// recipe or prep recipe, a count, a batch it went into), OneNewIsOld says how many of the old unit one of the
// new is (60 g a piece; 1 when only the label was wrong), and everything recorded is restated in the
// new unit, quantities divided by it and costs per unit multiplied, so every amount of money is as
// it was. That is a manager's to do, recorded as decided by them, and asked of one by anyone else:
// the material locked until then. Without the conversion it is refused: archive it and create a
// new one instead. A prep item keeps its unit, as it does on its own screen.
public sealed record MaterialUnitChange(
    UnitOfMeasure Unit,
    decimal? OneNewIsOld,
    string PurchaseUnit,
    decimal PurchaseUnitSize,
    decimal? CostPerPurchaseUnit = null,
    decimal? ReorderLevel = null);

// What records a material: a unit change restates them all. PendingRequests: requests waiting for a
// manager that name it, whose quantities would be in the old unit.
public sealed record MaterialUsage(int StockMovements, int InvoiceLines, int Recipes, int PrepRecipes, int Counts, int Batches, int PendingRequests)
{
    public bool InUse => StockMovements + InvoiceLines + Recipes + PrepRecipes + Counts + Batches > 0;
}

// The records naming a material, read and restated where they are kept.
public interface IMaterialUnits
{
    Task<MaterialUsage> UsageAsync(RawMaterial material, Guid? exceptRequestId, CancellationToken cancellationToken);

    // Restates every record naming it in its new unit (oneNewIsOld of the old in one of the new),
    // and saves the unit of work with it, in one transaction.
    Task RestateAndSaveAsync(RawMaterial material, decimal oneNewIsOld, CancellationToken cancellationToken);
}

public sealed record ChangeMaterialUnitCommand(
    Guid RequestId, Guid RawMaterialId, MaterialUnitChange Change, string? Reason, string StaffId, string StaffName, bool CanApprove)
    : IRequest<MaterialUnitChangeResult>, IRetryOnConflict;

// Applied (the material as it is now) or Requested (as it was, until a manager decides).
public sealed record MaterialUnitChangeResult(ApprovalOutcome Outcome, RawMaterialDto Material, ApprovalRequestDto? Request);

public sealed record GetMaterialUsageQuery(Guid RawMaterialId) : IRequest<MaterialUsage>;

internal sealed class GetMaterialUsageHandler(IRawMaterialRepository materials, IMaterialUnits units) : IRequestHandler<GetMaterialUsageQuery, MaterialUsage>
{
    public async Task<MaterialUsage> Handle(GetMaterialUsageQuery request, CancellationToken cancellationToken) =>
        await units.UsageAsync(await materials.GetRequiredAsync(request.RawMaterialId, cancellationToken), null, cancellationToken);
}

internal sealed class ChangeMaterialUnitHandler(
    IRawMaterialRepository materials,
    IPurchaseCategoryRepository categories,
    IApprovalRequestRepository requests,
    MaterialUnitWriter writer,
    IUnitOfWork unitOfWork) : IRequestHandler<ChangeMaterialUnitCommand, MaterialUnitChangeResult>
{
    public async Task<MaterialUnitChangeResult> Handle(ChangeMaterialUnitCommand request, CancellationToken cancellationToken)
    {
        var material = await materials.GetRequiredAsync(request.RawMaterialId, cancellationToken);
        async Task<RawMaterialDto> Dto() => RawMaterialDto.From(material, await categories.GetRequiredAsync(material.CategoryId, cancellationToken));
        if (await requests.GetByIdAsync(request.RequestId, cancellationToken) is { } already)
            return new MaterialUnitChangeResult(ApprovalResultDto.Of(already).Outcome, await Dto(), ApprovalRequestDto.From(already));

        if (material.IsArchived)
            throw new DomainException($"{material.Name} has been deleted.");
        await requests.EnsureNotPendingAsync(material.Id, cancellationToken);
        var usage = await writer.CheckAsync(material, request.Change, null, cancellationToken);

        // Nothing records it yet: changed at once, for anyone.
        if (!usage.InUse)
        {
            await writer.ApplyAsync(material, request.Change, null, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new MaterialUnitChangeResult(ApprovalOutcome.Applied, await Dto(), null);
        }

        var (summary, changes) = writer.Describe(material, request.Change, usage);
        var payload = ApprovalPayload.Write(request.Change);
        if (!request.CanApprove)
        {
            var asked = ApprovalRequest.AskToChange(
                request.RequestId, ApprovalModule.RawMaterial, ApprovalAction.Update, material.Id, summary, changes, payload, request.Reason,
                request.StaffId, request.StaffName);
            requests.Add(asked);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new MaterialUnitChangeResult(ApprovalOutcome.Requested, await Dto(), ApprovalRequestDto.From(asked));
        }

        var decided = ApprovalRequest.DecidedChange(
            request.RequestId, ApprovalModule.RawMaterial, ApprovalAction.Update, material.Id, summary, changes, payload, request.Reason,
            request.StaffId, request.StaffName);
        requests.Add(decided);
        await writer.ApplyAsync(material, request.Change, decided.Id, cancellationToken);
        return new MaterialUnitChangeResult(ApprovalOutcome.Applied, await Dto(), ApprovalRequestDto.From(decided));
    }
}

// Checks, describes and applies a unit change: for a manager's own and for an approval alike.
internal sealed class MaterialUnitWriter(IMaterialUnits units)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // What would stop it; checked again on approval, when that request is the one let through.
    public async Task<MaterialUsage> CheckAsync(RawMaterial material, MaterialUnitChange change, Guid? requestId, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(change.Unit))
            throw new DomainException("Choose the unit it is counted in.");
        if (material.IsPrepItem)
            throw new DomainException($"{material.Name} is a prep item, kept in its unit: its stock and batches are in it. Add a new prep item for another unit.");
        if (change.Unit == material.Unit)
            throw new DomainException($"{material.Name} is counted in {Name(change.Unit)} already.");
        var usage = await units.UsageAsync(material, requestId, cancellationToken);
        if (usage.PendingRequests > 0)
            throw new DomainException($"A request waiting for a manager names {material.Name}: its unit can be changed once that is decided.");
        if (usage.InUse && change.OneNewIsOld is not > 0)
            throw new DomainException(
                $"{material.Name} is already recorded in {Name(material.Unit)}: say how many {Name(material.Unit)} one {One(change.Unit)} is, " +
                "or, if that cannot be said, archive it and create a new material.");
        return usage;
    }

    // Done: in use, everything restated and saved together; otherwise for the caller to save.
    public async Task ApplyAsync(RawMaterial material, MaterialUnitChange change, Guid? requestId, CancellationToken cancellationToken)
    {
        var usage = await CheckAsync(material, change, requestId, cancellationToken);
        if (!usage.InUse)
        {
            material.ChangeUnit(change.Unit, null);
            material.SetPurchaseUnit(change.PurchaseUnit, change.PurchaseUnitSize);
            if (change.CostPerPurchaseUnit is { } cost)
                material.SetCost(cost);
            if (change.ReorderLevel is { } level)
                material.SetReorderLevel(level);
            return;
        }

        var ratio = change.OneNewIsOld!.Value;
        material.ChangeUnit(change.Unit, ratio);
        material.SetPurchaseUnit(change.PurchaseUnit, change.PurchaseUnitSize);
        await units.RestateAndSaveAsync(material, ratio, cancellationToken);
    }

    // "Tomatoes counted in pieces, not g: 1 piece is 60 g", and what it changes.
    public (string Summary, IReadOnlyList<ApprovalChange> Changes) Describe(RawMaterial material, MaterialUnitChange change, MaterialUsage usage)
    {
        var ratio = change.OneNewIsOld ?? 1;
        var (from, to) = (material.Unit, change.Unit);
        // Said of the larger unit: "1 piece is 60 g" whichever way it goes.
        var conversion = ratio >= 1 ? $"1 {One(to)} is {Quantity(ratio)} {Name(from)}" : $"1 {One(from)} is {Quantity(1 / ratio)} {Name(to)}";
        var changes = new List<ApprovalChange>
        {
            new("Counted in", Name(from), Name(to)),
            new("Conversion", null, conversion),
            new("On hand", $"{Quantity(material.QuantityOnHand)} {Name(from)}", $"{Quantity(material.QuantityOnHand / ratio)} {Name(to)}"),
        };
        if (material.AverageCost is { } cost)
            changes.Add(new ApprovalChange("Average cost", $"{Money(cost)} a {One(from)}", $"{Money(cost * ratio)} a {One(to)}"));
        if (material.ReorderLevel != 0)
            changes.Add(new ApprovalChange("Reorder at", $"{Quantity(material.ReorderLevel)} {Name(from)}", $"{Quantity(material.ReorderLevel / ratio)} {Name(to)}"));
        changes.Add(new ApprovalChange(
            "Bought by", $"{material.PurchaseUnit} of {Quantity(material.PurchaseUnitSize)} {Name(from)}",
            $"{change.PurchaseUnit} of {Quantity(change.PurchaseUnitSize)} {Name(to)}"));
        var restated = new[]
        {
            Count(usage.StockMovements, "stock movement"), Count(usage.InvoiceLines, "invoice line"), Count(usage.Recipes, "recipe"),
            Count(usage.PrepRecipes, "prep recipe"), Count(usage.Counts, "stock count"), Count(usage.Batches, "batch"),
        }.Where(s => s is not null);
        changes.Add(new ApprovalChange("Restated in the new unit", null, string.Join(", ", restated)));
        return ($"{material.Name} counted in {Name(to)}, not {Name(from)}: {conversion}", changes);
    }

    private static string? Count(int n, string what) => n switch
    {
        0 => null,
        1 => $"1 {what}",
        _ => $"{n} {(what == "batch" ? "batches" : what + "s")}",
    };

    private static string Name(UnitOfMeasure unit) => unit switch
    {
        UnitOfMeasure.Gram => "g",
        UnitOfMeasure.Millilitre => "ml",
        _ => "pieces",
    };

    private static string One(UnitOfMeasure unit) => unit switch
    {
        UnitOfMeasure.Gram => "g",
        UnitOfMeasure.Millilitre => "ml",
        _ => "piece",
    };

    private static string Quantity(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("#,0.###", Invariant);

    // Costs per unit are small: to the hundredth of a piastre when they need it.
    private static string Money(decimal value) => $"L.E {Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("#,0.00##", Invariant)}";
}
