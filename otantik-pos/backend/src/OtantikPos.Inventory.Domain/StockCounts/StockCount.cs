using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Domain.StockCounts;

public enum StockCountStatus
{
    // Being filled in: can be saved, finished on another device, or discarded.
    Draft = 1,

    // Stock was set to what was counted. Fixed from then on.
    Posted = 2,
}

// A physical count of the stores, material by material. Filled in as a draft, then posted: each
// counted material's stock is set to what was on the shelf (a CountAdjustment in the ledger),
// and what the ledger said at that moment is kept beside it, with the average cost then.
// Posted counts are the fixed points the variance report measures between.
//
// A material with no line was not counted, which is not the same as counted at nothing: its
// stock is left alone.
public sealed class StockCount : AggregateRoot
{
    private readonly List<StockCountLine> _lines = [];

    private StockCount() { }

    // The id is made by the till, so a retry after a lost answer reaches the same count.
    public StockCount(Guid id, string startedBy)
    {
        Id = id;
        StartedBy = startedBy;
        StartedAtUtc = DateTime.UtcNow;
        Status = StockCountStatus.Draft;
    }

    public StockCountStatus Status { get; private set; }
    public DateTime StartedAtUtc { get; private set; }
    public string StartedBy { get; private set; } = string.Empty;
    public DateTime? PostedAtUtc { get; private set; }
    public string? PostedBy { get; private set; }

    public IReadOnlyList<StockCountLine> Lines => _lines.AsReadOnly();

    // Replaces the draft's figures with these, in each material's unit (grams, millilitres, pieces).
    public void Record(IReadOnlyCollection<(Guid RawMaterialId, decimal Counted)> counted)
    {
        if (Status == StockCountStatus.Posted)
            throw new DomainException("This count has been posted. Start a new count to count again.");
        if (counted.Any(c => c.Counted < 0))
            throw new DomainException("A counted quantity cannot be negative.");
        if (counted.Select(c => c.RawMaterialId).Distinct().Count() != counted.Count)
            throw new DomainException("Each material is counted once: add its quantities together.");

        var wanted = counted.ToDictionary(c => c.RawMaterialId, c => c.Counted);
        _lines.RemoveAll(l => !wanted.ContainsKey(l.RawMaterialId));
        foreach (var (rawMaterialId, quantity) in wanted)
        {
            if (_lines.Find(l => l.RawMaterialId == rawMaterialId) is { } line)
                line.Change(quantity);
            else
                _lines.Add(new StockCountLine(Id, rawMaterialId, quantity));
        }
    }

    // Sets each counted material's stock to what was counted, and returns the ledger entries for
    // the differences, for the caller to add in the same unit of work. Counting should happen
    // with the kitchen closed: a sale while the count is open is taken from stock the count
    // then overwrites.
    public IReadOnlyList<StockMovement> Post(IReadOnlyDictionary<Guid, RawMaterial> materials, string postedBy)
    {
        if (Status == StockCountStatus.Posted)
            throw new DomainException("This count has already been posted.");
        if (_lines.Count == 0)
            throw new DomainException("Count at least one material before posting.");

        var movements = new List<StockMovement>(_lines.Count);
        foreach (var line in _lines)
        {
            var material = materials.GetValueOrDefault(line.RawMaterialId)
                ?? throw new NotFoundException(nameof(RawMaterial), line.RawMaterialId);
            line.Close(material.QuantityOnHand, material.AverageCost);
            movements.Add(material.AdjustToCount(line.CountedQuantity, Id));
        }

        Status = StockCountStatus.Posted;
        PostedAtUtc = DateTime.UtcNow;
        PostedBy = postedBy;
        return movements;
    }
}

public sealed class StockCountLine : Entity
{
    private StockCountLine() { }

    internal StockCountLine(Guid stockCountId, Guid rawMaterialId, decimal countedQuantity)
    {
        StockCountId = stockCountId;
        RawMaterialId = rawMaterialId;
        CountedQuantity = countedQuantity;
    }

    public Guid StockCountId { get; private set; }
    public Guid RawMaterialId { get; private set; }

    // In the material's unit: grams, millilitres, pieces.
    public decimal CountedQuantity { get; private set; }

    // What the ledger said when the count was posted; null on a draft, which a counter should
    // not see anyway: they count what is on the shelf, not confirm a number.
    public decimal? BookQuantity { get; private set; }

    // The material's average cost per unit when the count was posted: what its variance is valued at.
    public decimal? UnitCost { get; private set; }

    internal void Change(decimal countedQuantity) => CountedQuantity = countedQuantity;

    internal void Close(decimal bookQuantity, decimal? unitCost)
    {
        BookQuantity = bookQuantity;
        UnitCost = unitCost;
    }
}
