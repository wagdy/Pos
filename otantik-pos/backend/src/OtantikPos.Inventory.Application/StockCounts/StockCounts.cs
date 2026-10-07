using MediatR;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Application.Common;
using OtantikPos.Inventory.Application.RawMaterials;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Application.StockCounts;

// Physical counts of the stores. A count is filled in as a draft, saved as it goes (a count can
// take an hour, and a tablet can sleep), then posted, which sets stock to what was counted.
// StockCountId is made by the till, as PurchaseId is. The names of who started and posted it
// are set by the API from who is signed in.

public sealed record StockCountLineDto(Guid RawMaterialId, decimal CountedQuantity, decimal? BookQuantity, decimal? UnitCost);

public sealed record StockCountDto(
    Guid Id,
    StockCountStatus Status,
    DateTime StartedAtUtc,
    string StartedBy,
    DateTime? PostedAtUtc,
    string? PostedBy,
    IReadOnlyList<StockCountLineDto> Lines)
{
    public static StockCountDto From(StockCount count) => new(
        count.Id, count.Status, count.StartedAtUtc, count.StartedBy, count.PostedAtUtc, count.PostedBy,
        count.Lines.Select(l => new StockCountLineDto(l.RawMaterialId, l.CountedQuantity, l.BookQuantity, l.UnitCost)).ToList());
}

// In the material's unit: grams, millilitres, pieces.
public sealed record StockCountLine(Guid RawMaterialId, decimal CountedQuantity);

public sealed record StockCountSummaryDto(
    Guid Id, StockCountStatus Status, DateTime StartedAtUtc, string StartedBy, DateTime? PostedAtUtc, string? PostedBy, int Materials);

// The draft first, if there is one, then the posted counts, newest first.
public sealed record GetStockCountsQuery : IRequest<IReadOnlyList<StockCountSummaryDto>>;

internal sealed class GetStockCountsQueryHandler(IStockCountRepository counts)
    : IRequestHandler<GetStockCountsQuery, IReadOnlyList<StockCountSummaryDto>>
{
    public async Task<IReadOnlyList<StockCountSummaryDto>> Handle(GetStockCountsQuery request, CancellationToken cancellationToken) =>
        (await counts.GetAllAsync(cancellationToken))
            .OrderBy(c => c.Status)
            .ThenByDescending(c => c.PostedAtUtc ?? c.StartedAtUtc)
            .Select(c => new StockCountSummaryDto(c.Id, c.Status, c.StartedAtUtc, c.StartedBy, c.PostedAtUtc, c.PostedBy, c.Lines.Count))
            .ToList();
}

public sealed record GetStockCountQuery(Guid StockCountId) : IRequest<StockCountDto?>;

internal sealed class GetStockCountQueryHandler(IStockCountRepository counts) : IRequestHandler<GetStockCountQuery, StockCountDto?>
{
    public async Task<StockCountDto?> Handle(GetStockCountQuery request, CancellationToken cancellationToken) =>
        await counts.GetByIdAsync(request.StockCountId, cancellationToken) is { } count ? StockCountDto.From(count) : null;
}

// Creates the draft on its first save. Only one draft at a time: a second is refused, naming the
// one still open.
public sealed record SaveStockCountCommand(Guid StockCountId, IReadOnlyList<StockCountLine> Lines, string StaffName = "")
    : IRequest<StockCountDto>;

internal sealed class SaveStockCountCommandHandler(
    IStockCountRepository counts,
    IRawMaterialRepository materials,
    IUnitOfWork unitOfWork) : IRequestHandler<SaveStockCountCommand, StockCountDto>
{
    public async Task<StockCountDto> Handle(SaveStockCountCommand request, CancellationToken cancellationToken)
    {
        // Every listed material must exist; an empty draft is allowed while it is being started.
        if (request.Lines.Count > 0)
            await materials.GetRequiredByIdsAsync(request.Lines.Select(l => l.RawMaterialId).ToList(), cancellationToken);

        var count = await counts.GetByIdAsync(request.StockCountId, cancellationToken);
        if (count is null)
        {
            if (await counts.GetDraftAsync(cancellationToken) is { } open)
                throw new DomainException(
                    $"{open.StartedBy} started a count at {open.StartedAtUtc:HH:mm} UTC that is still open: finish or discard it first.");
            count = new StockCount(request.StockCountId, request.StaffName);
            counts.Add(count);
        }

        count.Record(request.Lines.Select(l => (l.RawMaterialId, l.CountedQuantity)).ToList());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return StockCountDto.From(count);
    }
}

// Posting again, after an answer was lost, returns the posted count and changes nothing.
public sealed record PostStockCountCommand(Guid StockCountId, string StaffName = "") : IRequest<StockCountDto>, IRetryOnConflict;

internal sealed class PostStockCountCommandHandler(
    IStockCountRepository counts,
    IRawMaterialRepository materials,
    IStockMovementRepository movements,
    IUnitOfWork unitOfWork) : IRequestHandler<PostStockCountCommand, StockCountDto>
{
    public async Task<StockCountDto> Handle(PostStockCountCommand request, CancellationToken cancellationToken)
    {
        var count = await counts.GetRequiredAsync(request.StockCountId, cancellationToken);
        if (count.Status == StockCountStatus.Posted)
            return StockCountDto.From(count);

        IReadOnlyDictionary<Guid, RawMaterial> byId = count.Lines.Count == 0
            ? new Dictionary<Guid, RawMaterial>()
            : await materials.GetRequiredByIdsAsync(count.Lines.Select(l => l.RawMaterialId).ToList(), cancellationToken);
        foreach (var movement in count.Post(byId, request.StaffName))
            movements.Add(movement);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return StockCountDto.From(count);
    }
}

public sealed record DiscardStockCountCommand(Guid StockCountId) : IRequest;

internal sealed class DiscardStockCountCommandHandler(IStockCountRepository counts, IUnitOfWork unitOfWork)
    : IRequestHandler<DiscardStockCountCommand>
{
    public async Task Handle(DiscardStockCountCommand request, CancellationToken cancellationToken)
    {
        // Already gone: a retry of a discard that worked.
        if (await counts.GetByIdAsync(request.StockCountId, cancellationToken) is not { } count)
            return;
        if (count.Status == StockCountStatus.Posted)
            throw new DomainException("A posted count stays: it is part of the stock's history.");

        counts.Remove(count);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// A whole count in one request: recorded and posted at once. For a quick count of a few
// materials, and for scripts; the screens use a draft.
public sealed record RecordStockCountCommand(Guid StockCountId, IReadOnlyList<StockCountLine> Lines, string StaffName = "")
    : IRequest<IReadOnlyList<RawMaterialDto>>, IRetryOnConflict;

internal sealed class RecordStockCountCommandHandler(
    IStockCountRepository counts,
    IRawMaterialRepository materials,
    IStockMovementRepository movements,
    IUnitOfWork unitOfWork) : IRequestHandler<RecordStockCountCommand, IReadOnlyList<RawMaterialDto>>
{
    public async Task<IReadOnlyList<RawMaterialDto>> Handle(RecordStockCountCommand request, CancellationToken cancellationToken)
    {
        var byId = await materials.GetRequiredByIdsAsync(
            request.Lines.Select(l => l.RawMaterialId).ToList(), cancellationToken);

        if (await counts.GetByIdAsync(request.StockCountId, cancellationToken) is null)
        {
            var count = new StockCount(request.StockCountId, request.StaffName);
            count.Record(request.Lines.Select(l => (l.RawMaterialId, l.CountedQuantity)).ToList());
            counts.Add(count);
            foreach (var movement in count.Post(byId, request.StaffName))
                movements.Add(movement);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return byId.Values.Select(RawMaterialDto.From).ToList();
    }
}
