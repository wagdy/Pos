using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Hr.Domain.Custody;
using OtantikPos.Inventory.Application.Approvals;
using OtantikPos.Inventory.Application.Custody;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Api.Controllers;

// Custody & Assets (عهدة وأصول): the categories, the assets and whose custody each is in, and the
// custody vouchers. Seen by whoever sees HR (HrView). Categories are a manager's. Assets and
// vouchers are whoever manages HR's: a manager's at once (200), anyone else's asked of a manager
// (202) and made only when approved. A consumable checked out as broken is a breakage entry in
// Costing → Waste, said on its voucher.
//
// RequestId and a voucher's TransactionId are the till's: a retry finds what was asked or made.
[ApiController]
[Route("api/custody")]
[Authorize(Policy = Permissions.HrView)]
public sealed class CustodyController(ISender sender, IBusinessCalendar calendar) : ControllerBase
{
    // ---- Categories ----

    [HttpGet("categories")]
    public Task<IReadOnlyList<AssetCategoryDto>> GetCategories(CancellationToken cancellationToken) =>
        sender.Send(new GetAssetCategoriesQuery(), cancellationToken);

    [HttpPut("categories/{categoryId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public Task<AssetCategoryDto> SaveCategory(Guid categoryId, AssetCategoryRequest request, CancellationToken cancellationToken) =>
        sender.Send(new SaveAssetCategoryCommand(categoryId, request.Name, request.IsConsumable), cancellationToken);

    [HttpDelete("categories/{categoryId:guid}")]
    [Authorize(Policy = Permissions.ApprovalDecide)]
    public async Task<IActionResult> DeleteCategory(Guid categoryId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteAssetCategoryCommand(categoryId), cancellationToken);
        return NoContent();
    }

    // ---- Assets ----

    // ?withFinancial=true (Link with Financials) adds those bought on purchase invoices to the
    // ones entered in HR.
    [HttpGet("assets")]
    public Task<IReadOnlyList<AssetItemDto>> GetAssets([FromQuery] bool withFinancial, CancellationToken cancellationToken) =>
        sender.Send(new GetAssetsQuery(withFinancial), cancellationToken);

    // An asset with its vouchers, the latest first.
    [HttpGet("assets/{assetId:guid}")]
    public async Task<ActionResult<AssetDetailDto>> GetAsset(Guid assetId, CancellationToken cancellationToken) =>
        await sender.Send(new GetAssetQuery(assetId), cancellationToken) is { } asset ? asset : NotFound();

    // Added under the id the till made (entered in HR), or changed. How many there are is set
    // when it is added; vouchers change it after.
    [HttpPut("assets/{assetId:guid}")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<IActionResult> SaveAsset(Guid assetId, SaveAssetRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(assetId, request.RequestId, new CustodyRequest(Asset: new AssetInput(request.Name, request.CategoryId, request.UnitCost, request.Quantity, request.Notes)),
            request.RequestReason, cancellationToken);

    // إذن دخول: all of it into an employee's custody, today unless a day before is given.
    [HttpPost("assets/{assetId:guid}/check-in")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<IActionResult> CheckIn(Guid assetId, CheckInRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(assetId, request.RequestId, new CustodyRequest(CheckIn: new CheckInInput(
            request.TransactionId, request.EmployeeId, request.Date ?? Today, request.Notes)), request.RequestReason, cancellationToken);

    // إذن خروج, and why: handed back or to maintenance (all of it); broken, lost or thrown away
    // (how many).
    [HttpPost("assets/{assetId:guid}/check-out")]
    [Authorize(Policy = Permissions.HrManage)]
    public Task<IActionResult> CheckOut(Guid assetId, CheckOutRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(assetId, request.RequestId, new CustodyRequest(CheckOut: new CheckOutInput(
            request.TransactionId, request.Reason, request.Quantity, request.Date ?? Today, request.Notes)), request.RequestReason, cancellationToken);

    // ---- Vouchers ----

    // The vouchers of the business days ?from= to ?to=, the latest first; the last 30 days unless said.
    [HttpGet("vouchers")]
    public Task<IReadOnlyList<AssetTransactionDto>> GetVouchers([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var last = to ?? Today;
        return sender.Send(new GetAssetTransactionsQuery(from ?? last.AddDays(-29), last), cancellationToken);
    }

    private DateOnly Today => calendar.BusinessDateOf(DateTime.UtcNow);

    private async Task<IActionResult> ChangeAsync(Guid assetId, Guid requestId, CustodyRequest change, string? reason, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new ChangeCustodyCommand(assetId, requestId, change, reason, StaffClaims.IdOf(User) ?? string.Empty, StaffName, CanApprove), cancellationToken);
        return result.Outcome == ApprovalOutcome.Requested ? Accepted(new ApprovalResultDto(result.Outcome, result.Request!)) : Ok(result);
    }

    private string StaffName => User.FindFirst(StaffClaims.Name)?.Value ?? string.Empty;

    private bool CanApprove => StaffClaims.RoleOf(User) is { } role && RolePermissions.Has(role, Permissions.ApprovalDecide);
}

public sealed record AssetCategoryRequest(string Name, bool IsConsumable);

// RequestReason, on each: why it is asked, for a manager's approval.
public sealed record SaveAssetRequest(
    Guid RequestId, string Name, Guid? CategoryId, decimal? UnitCost, int Quantity, string? Notes = null, string? RequestReason = null);

public sealed record CheckInRequest(Guid RequestId, Guid TransactionId, Guid EmployeeId, DateOnly? Date = null, string? Notes = null, string? RequestReason = null);

// Reason: why it goes out (Returned, Maintenance, Breakage, Lost, Discarded).
public sealed record CheckOutRequest(
    Guid RequestId, Guid TransactionId, AssetMovementReason Reason, int Quantity, DateOnly? Date = null, string? Notes = null, string? RequestReason = null);
