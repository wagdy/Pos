using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;

namespace Otantik.Domain.Tests;

// Physical counts and spoilage: the fixed points and the recorded losses the variance report
// measures from.
public class StockCountTests
{
    private static RawMaterial Priced(string name, decimal onHand, decimal costPerKg)
    {
        var material = new RawMaterial(name, UnitOfMeasure.Gram);
        material.ReceivePurchase(onHand, Guid.NewGuid(), lineCost: onHand / 1000 * costPerKg);
        return material;
    }

    [Fact]
    public void Posting_sets_stock_to_what_was_counted_and_keeps_what_the_records_said()
    {
        var fries = Priced("Farm frites", 20_000, 60);
        var cheese = Priced("Mozzarella", 5_000, 300);
        var count = new StockCount(Guid.NewGuid(), "Omar");

        count.Record([(fries.Id, 17_500), (cheese.Id, 5_200)]);
        Assert.All(count.Lines, l => Assert.Null(l.BookQuantity));

        var movements = count.Post(new Dictionary<Guid, RawMaterial> { [fries.Id] = fries, [cheese.Id] = cheese }, "Omar");

        Assert.Equal(StockCountStatus.Posted, count.Status);
        Assert.Equal((17_500m, 5_200m), (fries.QuantityOnHand, cheese.QuantityOnHand));
        var friesLine = Assert.Single(count.Lines, l => l.RawMaterialId == fries.Id);
        Assert.Equal((20_000m, 0.06m), (friesLine.BookQuantity, friesLine.UnitCost));
        Assert.Equal([-2_500m, 200m], movements.OrderBy(m => m.Quantity).Select(m => m.Quantity));
        Assert.All(movements, m => Assert.Equal((StockMovementReason.CountAdjustment, count.Id), (m.Reason, m.SourceId)));
    }

    // Left out is not counted, not counted at nothing: its stock stays.
    [Fact]
    public void A_material_left_out_of_the_count_keeps_its_stock()
    {
        var fries = Priced("Farm frites", 20_000, 60);
        var cheese = Priced("Mozzarella", 5_000, 300);
        var count = new StockCount(Guid.NewGuid(), "Omar");
        count.Record([(fries.Id, 1_000), (cheese.Id, 0)]);

        // The draft is saved again with the cheese taken off.
        count.Record([(fries.Id, 18_000)]);
        count.Post(new Dictionary<Guid, RawMaterial> { [fries.Id] = fries, [cheese.Id] = cheese }, "Omar");

        Assert.Equal((18_000m, 5_000m), (fries.QuantityOnHand, cheese.QuantityOnHand));
        Assert.Equal(18_000m, Assert.Single(count.Lines).CountedQuantity);
    }

    [Fact]
    public void A_count_refuses_what_cannot_be_counted_and_cannot_change_once_posted()
    {
        var fries = Priced("Farm frites", 1_000, 60);
        var count = new StockCount(Guid.NewGuid(), "Omar");

        Assert.Throws<DomainException>(() => count.Record([(fries.Id, -1)]));
        Assert.Throws<DomainException>(() => count.Record([(fries.Id, 1), (fries.Id, 2)]));
        Assert.Throws<DomainException>(() => count.Post(new Dictionary<Guid, RawMaterial>(), "Omar"));

        count.Record([(fries.Id, 900)]);
        count.Post(new Dictionary<Guid, RawMaterial> { [fries.Id] = fries }, "Omar");

        Assert.Throws<DomainException>(() => count.Record([(fries.Id, 800)]));
        Assert.Throws<DomainException>(() => count.Post(new Dictionary<Guid, RawMaterial> { [fries.Id] = fries }, "Omar"));
        Assert.Equal(900m, fries.QuantityOnHand);
    }

    [Fact]
    public void Spoilage_takes_stock_at_the_average_cost_and_says_why_and_who()
    {
        var cheese = Priced("Mozzarella", 5_000, 300);

        var spoiled = cheese.RecordSpoilage(250, Guid.NewGuid(), " Expired ", "Omar");

        Assert.Equal(4_750m, cheese.QuantityOnHand);
        Assert.Equal((StockMovementReason.Spoilage, -250m, 0.3m, "Expired", "Omar"),
            (spoiled.Reason, spoiled.Quantity, spoiled.UnitCost, spoiled.Note, spoiled.RecordedBy));
        Assert.Throws<DomainException>(() => cheese.RecordSpoilage(10, Guid.NewGuid(), " ", "Omar"));
        Assert.Throws<DomainException>(() => cheese.RecordSpoilage(0, Guid.NewGuid(), "Dropped", "Omar"));
    }
}
