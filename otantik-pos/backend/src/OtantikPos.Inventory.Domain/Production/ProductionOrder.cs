using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockMovements;

namespace OtantikPos.Inventory.Domain.Production;

public enum ProductionOrderStatus
{
    // Its ingredients out and the prep item in.
    Made = 1,

    // Reversed, each of its entries undone: deleted, or corrected by a batch made again.
    Voided = 2,
}

// A batch of a prep item made in the kitchen: its ingredients taken from stock by the recipe,
// scaled to what was planned, and what came out put into stock at what they cost. Recorded
// once it is made, and fixed from then on: the ledger of what the kitchen produced. A wrong one
// is voided (its entries reversed) and, when corrected, made again as a new batch that names it.
//
// Its id is made by the till and is the stock movements' SourceId, so a retry after a lost
// answer makes the batch once.
public sealed class ProductionOrder : AggregateRoot
{
    private readonly List<ProductionOrderLine> _lines = [];

    private ProductionOrder() { }

    private ProductionOrder(
        Guid id, Guid prepItemId, decimal planned, decimal produced, string producedBy, string? notes, string? approvedBy, Guid? revisionOf)
    {
        Id = id;
        PrepItemId = prepItemId;
        PlannedQuantity = planned;
        ProducedQuantity = produced;
        ProducedBy = producedBy;
        Notes = notes;
        ApprovedBy = approvedBy;
        RevisionOf = revisionOf;
        ProducedAtUtc = DateTime.UtcNow;
        Status = ProductionOrderStatus.Made;
    }

    public Guid PrepItemId { get; private set; }

    // In the prep item's unit. Planned sets the ingredients; produced is what came out, and what
    // the batch's cost is shared over: 9.6 kg from a 10 kg batch carries the lost 0.4 kg's cost.
    public decimal PlannedQuantity { get; private set; }
    public decimal ProducedQuantity { get; private set; }

    // What the ingredients cost at their averages when it was made, and that per unit produced.
    public decimal TotalCost { get; private set; }
    public decimal UnitCost { get; private set; }

    public DateTime ProducedAtUtc { get; private set; }
    public string ProducedBy { get; private set; } = string.Empty;
    public string? Notes { get; private set; }

    // The manager who approved it, when it was asked of one: made then, at the averages then.
    public string? ApprovedBy { get; private set; }

    public ProductionOrderStatus Status { get; private set; } = ProductionOrderStatus.Made;
    public DateTime? VoidedAtUtc { get; private set; }
    public string? VoidedBy { get; private set; }
    public bool IsVoided => Status == ProductionOrderStatus.Voided;

    // A correction: this batch is the one it replaced, made again with other quantities. The
    // replaced one names it back.
    public Guid? RevisionOf { get; private set; }
    public Guid? ReplacedBy { get; private set; }

    public IReadOnlyList<ProductionOrderLine> Lines => _lines.AsReadOnly();

    // Makes the batch: takes each ingredient off stock at its average cost now, and puts the
    // produced quantity of the prep item into stock at their total over what came out. Returns
    // the ledger entries for the caller to add in the same unit of work. The records may show
    // too little of an ingredient (a delivery not entered yet): it goes below zero, as a sale
    // takes it, and a count puts it right. One with no cost at all stops it, as the batch's cost
    // could not be known.
    public static (ProductionOrder Order, IReadOnlyList<StockMovement> Movements) Make(
        Guid id,
        RawMaterial prepItem,
        PrepRecipe recipe,
        IReadOnlyDictionary<Guid, RawMaterial> materials,
        decimal planned,
        decimal produced,
        string producedBy,
        string? notes = null,
        string? approvedBy = null,
        Guid? revisionOf = null)
    {
        if (recipe.PrepItemId != prepItem.Id)
            throw new DomainException("That recipe is for another prep item.");
        if (prepItem.IsArchived)
            throw new DomainException($"{prepItem.Name} has been deleted: it is no longer made.");
        if (recipe.Ingredients.Count == 0)
            throw new DomainException($"{prepItem.Name} has no ingredients yet: write its recipe first.");
        if (planned <= 0 || produced <= 0)
            throw new DomainException("Enter how much was planned and how much was made, both more than nothing.");
        if (notes?.Trim().Length > 200)
            throw new DomainException("Keep the notes under 200 characters.");

        var requirements = recipe.RequirementsFor(planned)
            .Select(r => (Material: materials.GetValueOrDefault(r.RawMaterialId) ?? throw new NotFoundException(nameof(RawMaterial), r.RawMaterialId), r.Quantity))
            .ToList();
        var unpriced = requirements.Where(r => r.Material.AverageCost is null).Select(r => r.Material.Name).ToList();
        if (unpriced.Count > 0)
            throw new DomainException($"{string.Join(", ", unpriced)} {(unpriced.Count == 1 ? "has" : "have")} no cost yet, so the batch's cost cannot be worked out. "
                + "Enter it on Raw materials, or post the invoice it came on.");

        var order = new ProductionOrder(
            id, prepItem.Id, planned, produced, producedBy, string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), approvedBy, revisionOf);
        var movements = new List<StockMovement>();
        foreach (var (material, quantity) in requirements.Where(r => r.Quantity > 0))
        {
            var unitCost = material.AverageCost!.Value;
            order._lines.Add(new ProductionOrderLine(id, material.Id, quantity, unitCost));
            movements.Add(material.UseInProduction(quantity, id));
        }

        order.TotalCost = order._lines.Sum(l => l.Cost);
        order.UnitCost = order.TotalCost / produced;
        movements.Add(prepItem.ReceiveProduction(produced, id, order.TotalCost));
        return (order, movements);
    }

    // Marks it voided once its entries are reversed (the caller reverses them): deleted, or
    // replacedBy the batch that corrects it. Voiding again changes nothing.
    public void Void(string voidedBy, Guid? replacedBy = null)
    {
        if (IsVoided)
            return;
        Status = ProductionOrderStatus.Voided;
        VoidedAtUtc = DateTime.UtcNow;
        VoidedBy = voidedBy;
        ReplacedBy = replacedBy;
    }
}

public sealed class ProductionOrderLine : Entity
{
    private ProductionOrderLine() { }

    internal ProductionOrderLine(Guid productionOrderId, Guid rawMaterialId, decimal quantity, decimal unitCost)
    {
        ProductionOrderId = productionOrderId;
        RawMaterialId = rawMaterialId;
        Quantity = quantity;
        UnitCost = unitCost;
        Cost = Math.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero);
    }

    public Guid ProductionOrderId { get; private set; }
    public Guid RawMaterialId { get; private set; }

    // As taken off stock, in the ingredient's own unit.
    public decimal Quantity { get; private set; }

    // Its average cost per unit at that moment, and what this line came to.
    public decimal UnitCost { get; private set; }
    public decimal Cost { get; private set; }
}

public interface IProductionOrderRepository : IRepository<ProductionOrder>
{
    // Newest first; one prep item's when it is given.
    Task<IReadOnlyList<ProductionOrder>> GetRecentAsync(Guid? prepItemId, int take, CancellationToken cancellationToken = default);
}
