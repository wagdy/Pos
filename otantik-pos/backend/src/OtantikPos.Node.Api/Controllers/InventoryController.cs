using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
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
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.Excel;

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
            rawMaterialId, request.Name, request.ReorderLevel, request.Code, request.CategoryId,
            request.PurchaseUnit, request.PurchaseUnitSize, request.DefaultYieldPercent, request.CostPerPurchaseUnit), cancellationToken);

    // The spreadsheet to bring raw materials in from: the columns, the unit and category lists.
    [HttpGet("raw-materials/template")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> GetRawMaterialsTemplate(CancellationToken cancellationToken)
    {
        var categories = await sender.Send(new GetPurchaseCategoriesQuery(), cancellationToken);
        return File(RawMaterialWorkbook.Build(categories.Select(c => c.Name).ToList()), RawMaterialWorkbook.ContentType, RawMaterialWorkbook.FileName);
    }

    // A filled-in template: every good row made a purchased stock item, the rest said why not.
    [HttpPost("raw-materials/import")]
    [Authorize(Policy = Permissions.InventoryManage)]
    [RequestSizeLimit(MaxImportBytes + 64 * 1024)]
    public async Task<RawMaterialImportResult> ImportRawMaterials(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxImportBytes)
            throw new Otantik.BuildingBlocks.DomainException("The file is larger than 5 MB: a sheet of materials is far smaller. Is it the right file?");
        await using var stream = file.OpenReadStream();
        return await sender.Send(new ImportRawMaterialsCommand(RawMaterialWorkbook.Read(stream)), cancellationToken);
    }

    private const int MaxImportBytes = 5 * 1024 * 1024;

    // Goods received in one request, filed as a posted invoice paid from the safe (waiting for a
    // manager's approval when someone else sends it). PurchaseId is made by the till,
    // so a retry after a lost response books the delivery once, not twice. The screens enter an
    // invoice in two steps instead, below.
    [HttpPost("purchases")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<RawMaterialDto>> ReceivePurchase(ReceivePurchaseCommand command, CancellationToken cancellationToken) =>
        sender.Send(command with { RecordedBy = StaffName, StaffId = StaffId, CanApprove = CanApprove }, cancellationToken);

    // Supplier invoices: posted ones for the history, newest first, or the drafts being entered.
    [HttpGet("purchase-invoices")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<PurchaseInvoiceSummaryDto>> GetPurchaseInvoices(
        [FromQuery] PurchaseInvoiceStatus status = PurchaseInvoiceStatus.Posted, CancellationToken cancellationToken = default) =>
        sender.Send(new GetPurchaseInvoicesQuery(status), cancellationToken);

    [HttpGet("purchase-invoices/{invoiceId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<PurchaseInvoiceDto>> GetPurchaseInvoice(Guid invoiceId, CancellationToken cancellationToken) =>
        await sender.Send(new GetPurchaseInvoiceQuery(invoiceId), cancellationToken) is { } invoice ? invoice : NotFound();

    // Step one: the invoice as entered, saved as a draft (created on its first save).
    [HttpPut("purchase-invoices/{invoiceId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<PurchaseInvoiceDto> SavePurchaseInvoice(Guid invoiceId, SavePurchaseInvoiceRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SavePurchaseInvoiceCommand(
            invoiceId, request.SupplierId, request.SupplierInvoiceNumber, request.InvoiceDate, request.Lines, StaffName,
            request.PaymentMethod, request.RevenueDate), cancellationToken);

    // Step two, after the review: the stock goes in, and it is paid as chosen: on credit, owed to
    // the supplier; in cash, paid for a manager and waiting for one's approval for anyone else
    // (PaymentStatus says which). Posting again returns the posted invoice.
    [HttpPost("purchase-invoices/{invoiceId:guid}/post")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<PurchaseInvoiceDto> PostPurchaseInvoice(Guid invoiceId, CancellationToken cancellationToken) =>
        sender.Send(new PostPurchaseInvoiceCommand(invoiceId, StaffName, StaffId, CanApprove), cancellationToken);

    // A day's purchases entered together: the date and payment chosen once, an invoice for each
    // supplier, and suppliers paid against what they are owed. Each becomes its own posted invoice
    // or payment, paid as chosen (a manager's at once, anyone else's asked of one); none is saved
    // unless all can be (422, each problem by its invoice or payment).
    [HttpPost("purchase-invoices/batch")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<PurchaseBatchResult> PostPurchaseBatch(PurchaseBatchRequest request, CancellationToken cancellationToken) =>
        sender.Send(new PostPurchaseBatchCommand(
            request.InvoiceDate, request.PaymentMethod, request.RevenueDate, request.Invoices, StaffName, StaffId, CanApprove, request.Payments ?? []),
            cancellationToken);

    // Pays a posted cash invoice that is not paid (its payment was rejected or voided), or puts it
    // on credit. RequestId is made by the till.
    [HttpPost("purchase-invoices/{invoiceId:guid}/pay")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<object> PayPurchaseInvoice(Guid invoiceId, PayPurchaseInvoiceRequest request, CancellationToken cancellationToken) =>
        new
        {
            PaymentStatus = await sender.Send(new PayPurchaseInvoiceCommand(
                request.RequestId, invoiceId, request.Method, request.RevenueDate, StaffId, StaffName, CanApprove), cancellationToken),
        };

    [HttpDelete("purchase-invoices/{invoiceId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> DiscardPurchaseInvoice(Guid invoiceId, CancellationToken cancellationToken)
    {
        await sender.Send(new DiscardPurchaseInvoiceCommand(invoiceId), cancellationToken);
        return NoContent();
    }

    // The paper, photographed or scanned: a JPEG, PNG, WebP or PDF of up to 10 MB.
    [HttpPut("purchase-invoices/{invoiceId:guid}/attachment")]
    [Authorize(Policy = Permissions.InventoryManage)]
    [RequestSizeLimit(PurchaseInvoice.MaxAttachmentBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = PurchaseInvoice.MaxAttachmentBytes + 64 * 1024)]
    public async Task<ActionResult<PurchaseInvoiceDto>> AttachPurchaseInvoiceFile(Guid invoiceId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > PurchaseInvoice.MaxAttachmentBytes)
            return UnprocessableEntity(new { errors = new[] { "The file must be under 10 MB." } });
        using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        return await sender.Send(new AttachPurchaseInvoiceFileCommand(invoiceId, file.FileName, file.ContentType, content.ToArray(), StaffName), cancellationToken);
    }

    [HttpGet("purchase-invoices/{invoiceId:guid}/attachment")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> GetPurchaseInvoiceFile(Guid invoiceId, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        if (await sender.Send(new GetPurchaseInvoiceAttachmentQuery(invoiceId), cancellationToken) is not { } attached)
            return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        return download ? File(attached.Content, attached.ContentType, attached.FileName) : File(attached.Content, attached.ContentType);
    }

    // The suppliers, by name; the archived ones too when asked for.
    [HttpGet("suppliers")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<SupplierDto>> GetSuppliers([FromQuery] bool includeArchived, CancellationToken cancellationToken) =>
        sender.Send(new GetSuppliersQuery(includeArchived), cancellationToken);

    // Adds the supplier on the first save, under the id the till made; changes it after.
    [HttpPut("suppliers/{supplierId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<SupplierDto> SaveSupplier(Guid supplierId, SaveSupplierRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveSupplierCommand(supplierId, request.Name, request.ContactPerson, request.Phone, request.Email, request.Notes), cancellationToken);

    // Deleted if it is on no invoice, archived if it is: the answer says which.
    [HttpDelete("suppliers/{supplierId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<SupplierRemovalDto> DeleteSupplier(Guid supplierId, CancellationToken cancellationToken) =>
        sender.Send(new DeleteSupplierCommand(supplierId), cancellationToken);

    [HttpPost("suppliers/{supplierId:guid}/restore")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<SupplierDto> RestoreSupplier(Guid supplierId, CancellationToken cancellationToken) =>
        sender.Send(new RestoreSupplierCommand(supplierId), cancellationToken);

    // The purchase categories materials and expense items are filed under, General first, with
    // how much is in each. Anyone who sees stock sees how it is grouped.
    [HttpGet("purchase-categories")]
    public Task<IReadOnlyList<PurchaseCategoryDto>> GetPurchaseCategories(CancellationToken cancellationToken) =>
        sender.Send(new GetPurchaseCategoriesQuery(), cancellationToken);

    // Adds it on the first save, under the id the till made; changes it after.
    [HttpPut("purchase-categories/{categoryId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<PurchaseCategoryDto> SavePurchaseCategory(Guid categoryId, SavePurchaseCategoryRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SavePurchaseCategoryCommand(categoryId, request.Name, request.NameAr, request.ExpenseLine ?? ExpenseLine.Controllable), cancellationToken);

    // moveTo: where what is in it goes; required while it holds anything.
    [HttpDelete("purchase-categories/{categoryId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> DeletePurchaseCategory(Guid categoryId, [FromQuery] Guid? moveTo, CancellationToken cancellationToken)
    {
        await sender.Send(new DeletePurchaseCategoryCommand(categoryId, moveTo), cancellationToken);
        return NoContent();
    }

    // Expense items: bought but never stock. A manager's, as what they cost is on them.
    [HttpGet("expense-items")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<ExpenseItemDto>> GetExpenseItems([FromQuery] bool includeArchived, CancellationToken cancellationToken) =>
        sender.Send(new GetExpenseItemsQuery(includeArchived), cancellationToken);

    [HttpPut("expense-items/{expenseItemId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<ExpenseItemDto> SaveExpenseItem(Guid expenseItemId, SaveExpenseItemRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveExpenseItemCommand(expenseItemId, request.Name, request.CategoryId, request.NameAr, request.Unit), cancellationToken);

    // Deleted if it is on no invoice, archived if it is: the answer says which.
    [HttpDelete("expense-items/{expenseItemId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<ExpenseItemRemovalDto> DeleteExpenseItem(Guid expenseItemId, CancellationToken cancellationToken) =>
        sender.Send(new DeleteExpenseItemCommand(expenseItemId), cancellationToken);

    [HttpPost("expense-items/{expenseItemId:guid}/restore")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<ExpenseItemDto> RestoreExpenseItem(Guid expenseItemId, CancellationToken cancellationToken) =>
        sender.Send(new RestoreExpenseItemCommand(expenseItemId), cancellationToken);

    // Prep items: made in the kitchen, kept in stock, costed from their batches. Seen by whoever
    // keeps the books; added and changed by a manager at once (200, the prep item), and by anyone
    // else as a request a manager approves (202, the request), nothing changed until then.
    [HttpGet("prep-items")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<PrepItemDto>> GetPrepItems(CancellationToken cancellationToken) =>
        sender.Send(new GetPrepItemsQuery(), cancellationToken);

    [HttpGet("prep-items/{prepItemId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<PrepItemDto>> GetPrepItem(Guid prepItemId, CancellationToken cancellationToken) =>
        await sender.Send(new GetPrepItemQuery(prepItemId), cancellationToken) is { } item ? item : NotFound();

    [HttpPost("prep-items")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IActionResult> CreatePrepItem(SavePrepItemRequest request, CancellationToken cancellationToken) =>
        SavePrepItem(null, request, cancellationToken);

    [HttpPut("prep-items/{prepItemId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IActionResult> UpdatePrepItem(Guid prepItemId, SavePrepItemRequest request, CancellationToken cancellationToken) =>
        SavePrepItem(prepItemId, request, cancellationToken);

    // A sheet of batch recipes (المصنعات): each prep item named on a row of its own, then its
    // ingredients. Made at once for a manager; asked of one, item by item, for anyone else.
    [HttpPost("prep-items/import")]
    [Authorize(Policy = Permissions.InventoryManage)]
    [RequestSizeLimit(MaxImportBytes + 64 * 1024)]
    public async Task<PrepImportResult> ImportPrepItems(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxImportBytes)
            throw new Otantik.BuildingBlocks.DomainException("The file is larger than 5 MB: a sheet of recipes is far smaller. Is it the right file?");
        await using var stream = file.OpenReadStream();
        return await sender.Send(new ImportPrepItemsCommand(PrepItemWorkbook.Read(stream), StaffId, StaffName, CanApprove), cancellationToken);
    }

    private async Task<IActionResult> SavePrepItem(Guid? prepItemId, SavePrepItemRequest request, CancellationToken cancellationToken)
    {
        var command = request.ToCommand(prepItemId);
        if (CanApprove)
            return Ok(await sender.Send(command, cancellationToken));
        return Accepted(await sender.Send(new RequestPrepItemChangeCommand(
            request.RequestId ?? Guid.NewGuid(), prepItemId, command.Change, request.Reason, StaffId, StaffName), cancellationToken));
    }

    // What a batch would take and cost made now: the review before posting.
    [HttpGet("production-orders/preview")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<ProductionPreviewDto> PreviewProduction(
        [FromQuery] Guid prepItemId, [FromQuery] decimal planned, [FromQuery] decimal? produced, CancellationToken cancellationToken) =>
        sender.Send(new PreviewProductionQuery(prepItemId, planned, produced), cancellationToken);

    [HttpGet("production-orders")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<ProductionOrderDto>> GetProductionOrders([FromQuery] Guid? prepItemId, CancellationToken cancellationToken) =>
        sender.Send(new GetProductionOrdersQuery(prepItemId), cancellationToken);

    [HttpGet("production-orders/{orderId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<ProductionOrderDto>> GetProductionOrder(Guid orderId, CancellationToken cancellationToken) =>
        await sender.Send(new GetProductionOrderQuery(orderId), cancellationToken) is { } order ? order : NotFound();

    // Makes the batch under the id the till made: the ingredients out, the prep item in, for a
    // manager (200, the batch). Anyone else's is a request (202), and nothing moves until a
    // manager approves it, at the averages then. A retry returns the batch, or request, already made.
    [HttpPut("production-orders/{orderId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> MakeProductionOrder(Guid orderId, MakeProductionOrderRequest request, CancellationToken cancellationToken)
    {
        if (CanApprove)
            return Ok(await sender.Send(new MakeProductionOrderCommand(
                orderId, request.PrepItemId, request.PlannedQuantity, request.ProducedQuantity, request.Notes, StaffName), cancellationToken));
        return Accepted(await sender.Send(new RequestProductionOrderCommand(
            request.RequestId ?? Guid.NewGuid(), orderId, request.PrepItemId, request.PlannedQuantity, request.ProducedQuantity, request.Notes,
            request.Reason, StaffId, StaffName), cancellationToken));
    }

    // Corrects a batch: reversed and made again with these quantities, at the averages now, as
    // the batch NewOrderId. At once for a manager (200), recorded with the approvals; a request
    // for anyone else (202).
    [HttpPost("production-orders/{orderId:guid}/revision")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> ReviseProductionOrder(Guid orderId, ReviseProductionOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ReviseProductionOrderCommand(
            request.RequestId, orderId, request.NewOrderId, request.PlannedQuantity, request.ProducedQuantity, request.Notes, request.Reason,
            StaffId, StaffName, CanApprove), cancellationToken);
        return result.Outcome == ApprovalOutcome.Requested ? Accepted(result) : Ok(result);
    }

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

    // Replaces the recipe a menu item sells by, whole. No ingredients deletes it (it is kept,
    // marked deleted), answered with 204.
    [HttpPut("recipes")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<RecipeDto>> SetRecipe(SetRecipeCommand command, CancellationToken cancellationToken)
    {
        var recipe = await sender.Send(command with { StaffName = StaffName, CanDelete = CanApprove }, cancellationToken);
        return recipe is null ? NoContent() : recipe;
    }

    // A recipe of its own: linked to a menu item, or off the menu (no target).
    [HttpPost("recipes")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<RecipeDto> CreateRecipe(SaveRecipeRequest request, CancellationToken cancellationToken) =>
        sender.Send(request.ToCommand(null), cancellationToken);

    [HttpPut("recipes/{recipeId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<RecipeDto> UpdateRecipe(Guid recipeId, SaveRecipeRequest request, CancellationToken cancellationToken) =>
        sender.Send(request.ToCommand(recipeId), cancellationToken);

    // The kitchen's recipe categories, with how many recipes each holds.
    [HttpGet("recipe-categories")]
    public Task<IReadOnlyList<RecipeCategoryDto>> GetRecipeCategories(CancellationToken cancellationToken) =>
        sender.Send(new GetRecipeCategoriesQuery(), cancellationToken);

    // Adds it on the first save, under the id the till made; renames it after.
    [HttpPut("recipe-categories/{categoryId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<RecipeCategoryDto> SaveRecipeCategory(Guid categoryId, SaveRecipeCategoryRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveRecipeCategoryCommand(categoryId, request.Name, request.NameAr), cancellationToken);

    // moveTo: where its recipes go; required while it has any.
    [HttpDelete("recipe-categories/{categoryId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> DeleteRecipeCategory(Guid categoryId, [FromQuery] Guid? moveTo, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteRecipeCategoryCommand(categoryId, moveTo), cancellationToken);
        return NoContent();
    }

    // Marks it deleted: kept for the record, gone from the screens and the sales.
    // Deleted (204) by someone who may approve deletions; asked of a manager otherwise (202).
    [HttpDelete("recipes/{recipeId:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> DeleteRecipe(Guid recipeId, CancellationToken cancellationToken)
    {
        var result = await sender.Send(DeletionOf(Guid.NewGuid(), ApprovalModule.Recipe, recipeId, null), cancellationToken);
        return result.Outcome == ApprovalOutcome.Applied ? NoContent() : Accepted(result);
    }

    // Deletes a recipe, raw material, posted invoice, waste entry, posted stock count, prep item
    // or production order: at once (200, Applied) for someone who may approve, otherwise asked of
    // a manager (202, Requested), the record left as it is until then. RequestId is made by the till.
    [HttpPost("deletion-requests")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<ApprovalResultDto>> RequestDeletion(RequestDeletionBody request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(DeletionOf(request.RequestId, request.Module, request.RecordId, request.Reason), cancellationToken);
        return result.Outcome == ApprovalOutcome.Requested ? Accepted(result) : Ok(result);
    }

    // What waits for a manager (deletions, new and changed prep items and batches), for the
    // screens to mark and lock; with ?decided=, the last decisions too, for whoever decides them.
    [HttpGet("approval-requests")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<ApprovalRequestsDto> GetApprovalRequests([FromQuery] int decided, CancellationToken cancellationToken) =>
        sender.Send(new GetApprovalRequestsQuery(CanApprove ? Math.Clamp(decided, 0, 200) : 0), cancellationToken);

    // What approving a batch would move now, at today's averages, and for a correction the batch
    // it replaces: what the manager signs off on.
    [HttpGet("approval-requests/{requestId:guid}/preview")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<ActionResult<ApprovalPreviewDto>> GetApprovalPreview(Guid requestId, CancellationToken cancellationToken) =>
        await sender.Send(new GetApprovalPreviewQuery(requestId), cancellationToken) is { } preview ? preview : NotFound();

    [HttpPost("approval-requests/{requestId:guid}/approve")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<ApprovalRequestDto> ApproveRequest(Guid requestId, CancellationToken cancellationToken) =>
        sender.Send(new ApproveRequestCommand(requestId, StaffName), cancellationToken);

    [HttpPost("approval-requests/{requestId:guid}/reject")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<ApprovalRequestDto> RejectRequest(Guid requestId, RejectRequestBody request, CancellationToken cancellationToken) =>
        sender.Send(new RejectRequestCommand(requestId, request.Note, StaffName), cancellationToken);

    // Name: what to call it if its menu item has another recipe now and it comes back off the menu.
    [HttpPost("recipes/{recipeId:guid}/restore")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<RecipeDto> RestoreRecipe(Guid recipeId, RestoreRecipeRequest? request, CancellationToken cancellationToken) =>
        sender.Send(new RestoreRecipeCommand(recipeId, request?.Name), cancellationToken);

    // Who is signed in, as their name: kept on counts and spoilage, which have no order to audit them.
    private string StaffName => User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty;

    private bool CanApprove => StaffClaims.RoleOf(User) is { } role && RolePermissions.Has(role, Permissions.ApprovalDecide);

    private string StaffId => StaffClaims.IdOf(User) ?? string.Empty;

    private RequestDeletionCommand DeletionOf(Guid requestId, ApprovalModule module, Guid recordId, string? reason) =>
        new(requestId, module, recordId, reason, StaffId, StaffName, CanApprove);
}

public sealed record SaveStockCountRequest(IReadOnlyList<StockCountLine> Lines);

public sealed record SavePurchaseInvoiceRequest(
    Guid? SupplierId,
    string? SupplierInvoiceNumber,
    DateOnly? InvoiceDate,
    IReadOnlyList<PurchaseInvoiceLineEntry> Lines,
    PurchasePaymentMethod? PaymentMethod = null,
    DateOnly? RevenueDate = null);

public sealed record PayPurchaseInvoiceRequest(Guid RequestId, PurchasePaymentMethod Method, DateOnly? RevenueDate = null);

public sealed record PurchaseBatchRequest(
    DateOnly InvoiceDate, PurchasePaymentMethod PaymentMethod, IReadOnlyList<BatchInvoice> Invoices, DateOnly? RevenueDate = null,
    IReadOnlyList<BatchPayment>? Payments = null);

public sealed record SavePrepItemRequest(
    string Name,
    UnitOfMeasure Unit,
    decimal BatchQuantity,
    IReadOnlyList<PrepIngredientEntry> Ingredients,
    string? Code = null,
    Guid? CategoryId = null,
    string? DisplayUnit = null,
    decimal? DisplayUnitSize = null,
    decimal ReorderLevel = 0,
    // For a request: made by the till, so a retry is recorded once; and why, for the manager.
    Guid? RequestId = null,
    string? Reason = null,
    Guid? RecipeCategoryId = null)
{
    public SavePrepItemCommand ToCommand(Guid? id) =>
        new(id, Name, Unit, BatchQuantity, Ingredients ?? [], Code, CategoryId, DisplayUnit, DisplayUnitSize, ReorderLevel, RecipeCategoryId);
}

public sealed record SaveRecipeRequest(
    string? Name,
    RecipeTargetKind? TargetKind,
    int? CatalogItemId,
    int? VariantId,
    int Portions,
    IReadOnlyList<RecipeIngredientDto> Ingredients,
    Guid? RecipeCategoryId = null,
    decimal? PlannedPrice = null)
{
    public SaveRecipeCommand ToCommand(Guid? id) =>
        new(id, Name, TargetKind, CatalogItemId, VariantId, Portions, Ingredients ?? [], RecipeCategoryId, PlannedPrice);
}

public sealed record SaveRecipeCategoryRequest(string Name, string? NameAr = null);

public sealed record RestoreRecipeRequest(string? Name);

public sealed record MakeProductionOrderRequest(
    Guid PrepItemId, decimal PlannedQuantity, decimal ProducedQuantity, string? Notes = null, Guid? RequestId = null, string? Reason = null);

public sealed record ReviseProductionOrderRequest(
    Guid RequestId, Guid NewOrderId, decimal PlannedQuantity, decimal ProducedQuantity, string? Notes = null, string? Reason = null);

public sealed record RequestDeletionBody(Guid RequestId, ApprovalModule Module, Guid RecordId, string? Reason = null);

public sealed record RejectRequestBody(string? Note = null);

public sealed record SaveSupplierRequest(string Name, string? ContactPerson = null, string? Phone = null, string? Email = null, string? Notes = null);

public sealed record SavePurchaseCategoryRequest(string Name, string? NameAr = null, ExpenseLine? ExpenseLine = null);

public sealed record SaveExpenseItemRequest(string Name, Guid? CategoryId = null, string? NameAr = null, string? Unit = null);

public sealed record UpdateRawMaterialRequest(
    string Name,
    decimal ReorderLevel,
    string? Code = null,
    Guid? CategoryId = null,
    string? PurchaseUnit = null,
    decimal? PurchaseUnitSize = null,
    decimal? DefaultYieldPercent = null,
    decimal? CostPerPurchaseUnit = null);
