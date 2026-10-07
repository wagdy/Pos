using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.Costing;

namespace OtantikPos.Node.Api.Controllers;

// The manager's costing: what dishes and ingredients cost, and the food cost reports. Costs are
// a manager's, not a cashier's, so all of it is CostingView. Materials, purchases and recipes are
// changed through api/inventory, as before.
[ApiController]
[Route("api/costing")]
[Authorize(Policy = Permissions.CostingView)]
public sealed class CostingController(CostingService costing, VarianceService variance, KpiService kpis, BreakEvenService breakEven) : ControllerBase
{
    // Raw materials with their costs: what each costs per purchase unit, the last price paid, and
    // the stock's value.
    [HttpGet("materials")]
    public Task<IReadOnlyList<MaterialCostDto>> GetMaterials(CancellationToken cancellationToken) =>
        costing.GetMaterialsAsync(cancellationToken);

    // Every dish, size and add-on with its cost per portion and food cost %.
    [HttpGet("menu")]
    public Task<IReadOnlyList<RecipeCostCard>> GetMenuCosts(CancellationToken cancellationToken) =>
        costing.GetMenuCostsAsync(cancellationToken);

    // The Recipe Costing Template for one dish, size or add-on.
    [HttpGet("recipe-card")]
    public Task<RecipeCostCard> GetRecipeCard(
        [FromQuery] RecipeTargetKind targetKind, [FromQuery] int catalogItemId, [FromQuery] int? variantId, CancellationToken cancellationToken) =>
        costing.GetRecipeCardAsync(targetKind, catalogItemId, variantId, cancellationToken);

    // The Monthly Theoretical Cost report for a business month.
    [HttpGet("theoretical")]
    public Task<TheoreticalCostReport> GetTheoreticalCost([FromQuery] int year, [FromQuery] int month, CancellationToken cancellationToken) =>
        costing.GetTheoreticalCostAsync(year, month, cancellationToken);

    // Actual usage against standard between two posted stock counts; the last two by default.
    [HttpGet("variance")]
    public Task<VarianceReport> GetVariance([FromQuery] Guid? fromCountId, [FromQuery] Guid? toCountId, CancellationToken cancellationToken) =>
        variance.GetVarianceAsync(fromCountId, toCountId, cancellationToken);

    // Spoilage recorded in the last so many days, with what it cost.
    [HttpGet("spoilage")]
    public Task<IReadOnlyList<SpoilageEntry>> GetSpoilage([FromQuery] int days = 30, CancellationToken cancellationToken = default)
    {
        var to = DateTime.UtcNow;
        return variance.GetSpoilageAsync(to.AddDays(-Math.Clamp(days, 1, 366)), to.AddMinutes(1), cancellationToken);
    }

    // The template's financial KPIs for a business month, with the month as an income statement
    // and what each dish earned.
    [HttpGet("kpis")]
    public Task<KpiReport> GetKpis([FromQuery] int year, [FromQuery] int month, CancellationToken cancellationToken) =>
        kpis.GetAsync(year, month, cancellationToken);

    // A month's wages, labour hours and overheads, as the manager enters them. The whole month
    // each time: a figure left out is not known yet.
    [HttpPut("expenses/{year:int}/{month:int}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<MonthlyExpensesDto> SaveExpenses(int year, int month, MonthlyExpensesDto expenses, CancellationToken cancellationToken) =>
        kpis.SaveExpensesAsync(year, month, expenses, User.FindFirst(StaffClaims.Name)?.Value, cancellationToken);

    // The break-even worksheet over a run of months (one month: from = to), at a year's pace.
    [HttpGet("break-even")]
    public Task<BreakEvenWorksheet> GetBreakEven(
        [FromQuery] int fromYear, [FromQuery] int fromMonth, [FromQuery] int toYear, [FromQuery] int toMonth, CancellationToken cancellationToken) =>
        breakEven.GetAsync(fromYear, fromMonth, toYear, toMonth, cancellationToken);

    // The worksheet's four options, weekly sales to try, kept for next time.
    [HttpPut("break-even/scenarios")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<IReadOnlyList<decimal>> SaveBreakEvenScenarios(IReadOnlyList<decimal> scenarios, CancellationToken cancellationToken) =>
        breakEven.SaveScenariosAsync(scenarios, cancellationToken);

    [HttpGet("settings")]
    public Task<CostingSettingsDto> GetSettings(CancellationToken cancellationToken) =>
        costing.GetSettingsAsync(cancellationToken);

    [HttpPut("settings")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<CostingSettingsDto> SaveSettings(CostingSettingsDto settings, CancellationToken cancellationToken) =>
        costing.SaveSettingsAsync(settings, cancellationToken);

    [HttpGet("shared-costs")]
    public Task<IReadOnlyList<SharedCostDto>> GetSharedCosts(CancellationToken cancellationToken) =>
        costing.GetSharedCostsAsync(cancellationToken);

    [HttpPost("shared-costs")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<SharedCostDto> AddSharedCost(SaveSharedCostRequest request, CancellationToken cancellationToken) =>
        costing.SaveSharedCostAsync(null, request, cancellationToken);

    [HttpPut("shared-costs/{id:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public Task<SharedCostDto> SaveSharedCost(Guid id, SaveSharedCostRequest request, CancellationToken cancellationToken) =>
        costing.SaveSharedCostAsync(id, request, cancellationToken);

    [HttpDelete("shared-costs/{id:guid}")]
    [Authorize(Policy = Permissions.InventoryManage)]
    public async Task<IActionResult> DeleteSharedCost(Guid id, CancellationToken cancellationToken)
    {
        await costing.DeleteSharedCostAsync(id, cancellationToken);
        return NoContent();
    }
}
