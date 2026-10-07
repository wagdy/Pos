using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Application.Recipes;
using OtantikPos.Inventory.Application.StockCounts;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Node.Api.Auth;

namespace OtantikPos.Node.Api.Controllers;

// Stock and recipes. Unlike orders, who may do what here never depends on the record, so the
// permissions are checked on the endpoints: the Inventory use cases also run from the outbox
// with no user at all, deducting stock for a sale.
//
// Seeing stock is InventoryView (cashiers too); changing it is InventoryManage (managers). The
// sales side moves stock by itself, through OrderSettled and OrderItemsVoided.
[ApiController]
[Route("api/inventory")]
[Authorize(Policy = Permissions.InventoryView)]
public sealed class InventoryController(ISender sender) : ControllerBase
{
    [HttpGet("raw-materials")]
    public Task<IReadOnlyList<RawMaterialDto>> GetRawMaterials([FromQuery] bool needsReorderOnly, CancellationToken cancellationToken) =>
        sender.Send(new GetRawMaterialsQuery(needsReorderOnly), cancellationToken);

    [HttpPost("raw-materials")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<RawMaterialDto>> CreateRawMaterial(CreateRawMaterialCommand command, CancellationToken cancellationToken)
    {
        var created = await sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetRawMaterials), null, created);
    }

    [HttpPut("raw-materials/{rawMaterialId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<RawMaterialDto> UpdateRawMaterial(Guid rawMaterialId, UpdateRawMaterialRequest request, CancellationToken cancellationToken) =>
        sender.Send(new UpdateRawMaterialCommand(
            rawMaterialId, request.Name, request.ReorderLevel, request.Code, request.Category,
            request.PurchaseUnit, request.PurchaseUnitSize, request.DefaultYieldPercent, request.CostPerPurchaseUnit), cancellationToken);

    // Goods received. PurchaseId is made by the till, so a retry after a lost response books
    // the delivery once, not twice.
    [HttpPost("purchases")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<RawMaterialDto>> ReceivePurchase(ReceivePurchaseCommand command, CancellationToken cancellationToken) =>
        sender.Send(command, cancellationToken);

    // Thrown-away raw stock: expired, spoiled, dropped. SpoilageId makes a retry harmless.
    [HttpPost("spoilage")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<RawMaterialDto>> RecordSpoilage(RecordSpoilageCommand command, CancellationToken cancellationToken) =>
        sender.Send(command with { RecordedBy = StaffName }, cancellationToken);

    // A whole physical count in one request, recorded and posted at once: sets stock to what is
    // on the shelf and records the difference. StockCountId makes a retry harmless, as PurchaseId
    // does above. The screens fill in a draft instead, below.
    [HttpPost("stock-counts")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<RawMaterialDto>> RecordStockCount(RecordStockCountCommand command, CancellationToken cancellationToken) =>
        sender.Send(command with { StaffName = StaffName }, cancellationToken);

    // Counts are a manager's: a count sheet shows what is on the shelves, and a posted one what the
    // records expected.
    [HttpGet("stock-counts")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<StockCountSummaryDto>> GetStockCounts(CancellationToken cancellationToken) =>
        sender.Send(new GetStockCountsQuery(), cancellationToken);

    [HttpGet("stock-counts/{stockCountId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<StockCountDto>> GetStockCount(Guid stockCountId, CancellationToken cancellationToken) =>
        await sender.Send(new GetStockCountQuery(stockCountId), cancellationToken) is { } count ? count : NotFound();

    // Saves the draft as it stands, creating it on the first save. The whole list each time: a
    // material left out is not counted.
    [HttpPut("stock-counts/{stockCountId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<StockCountDto> SaveStockCount(Guid stockCountId, SaveStockCountRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveStockCountCommand(stockCountId, request.Lines, StaffName), cancellationToken);

    // Sets stock to what was counted. Posting again returns the posted count.
    [HttpPost("stock-counts/{stockCountId:guid}/post")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<StockCountDto> PostStockCount(Guid stockCountId, CancellationToken cancellationToken) =>
        sender.Send(new PostStockCountCommand(stockCountId, StaffName), cancellationToken);

    // Discards a draft. A posted count cannot be removed.
    [HttpDelete("stock-counts/{stockCountId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> DiscardStockCount(Guid stockCountId, CancellationToken cancellationToken)
    {
        await sender.Send(new DiscardStockCountCommand(stockCountId), cancellationToken);
        return NoContent();
    }

    // A menu item's, variant's or add-on's recipe. 404 means it has none yet, and only 404:
    // the till must not show an empty recipe for a request that merely failed.
    [HttpGet("recipes")]
    public async Task<ActionResult<RecipeDto>> GetRecipe(
        [FromQuery] RecipeTargetKind targetKind, [FromQuery] int catalogItemId, [FromQuery] int? variantId, CancellationToken cancellationToken)
    {
        var recipe = await sender.Send(new GetRecipeQuery(targetKind, catalogItemId, variantId), cancellationToken);
        return recipe is null ? NotFound() : recipe;
    }

    // Replaces the recipe whole. No ingredients removes it, answered with 204.
    [HttpPut("recipes")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<RecipeDto>> SetRecipe(SetRecipeCommand command, CancellationToken cancellationToken)
    {
        var recipe = await sender.Send(command, cancellationToken);
        return recipe is null ? NoContent() : recipe;
    }

    // Who is signed in, as their name: kept on counts and spoilage, which have no order to audit them.
    private string StaffName => User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty;
}

public sealed record SaveStockCountRequest(IReadOnlyList<StockCountLine> Lines);

public sealed record UpdateRawMaterialRequest(
    string Name,
    decimal ReorderLevel,
    string? Code = null,
    string? Category = null,
    string? PurchaseUnit = null,
    decimal? PurchaseUnitSize = null,
    decimal? DefaultYieldPercent = null,
    decimal? CostPerPurchaseUnit = null);
