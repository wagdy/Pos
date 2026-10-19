using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;

namespace Otantik.Domain.Tests;

// What a change approved by a manager does to the books: a posted record or a batch is voided by
// reversing entries rather than erased, a material is archived only with nothing on the shelf,
// and a request is decided once.
public class ApprovalTests
{
    [Fact]
    public void A_voided_delivery_goes_back_out_and_takes_its_price_out_of_the_average()
    {
        var tomatoes = new RawMaterial("Tomatoes", UnitOfMeasure.Gram);
        tomatoes.ReceivePurchase(5_000, Guid.NewGuid(), 100);             // 0.02 a gram
        var second = tomatoes.ReceivePurchase(5_000, Guid.NewGuid(), 150); // 0.03 a gram
        Assert.Equal((10_000m, 0.025m), (tomatoes.QuantityOnHand, tomatoes.AverageCost));

        var reversal = tomatoes.Reverse(second, "Omar");

        Assert.Equal((5_000m, 0.02m), (tomatoes.QuantityOnHand, tomatoes.AverageCost));
        Assert.Equal((-5_000m, StockMovementReason.Purchase, second.SourceId, 0.03m, (Guid?)second.Id, "Omar"),
            (reversal.Quantity, reversal.Reason, reversal.SourceId, reversal.UnitCost, reversal.ReversalOf, reversal.RecordedBy));
        Assert.Throws<DomainException>(() => tomatoes.Reverse(reversal, "Omar"));
    }

    [Fact]
    public void A_delivery_used_up_before_its_void_leaves_the_average_where_it_is()
    {
        var tomatoes = new RawMaterial("Tomatoes", UnitOfMeasure.Gram);
        tomatoes.ReceivePurchase(5_000, Guid.NewGuid(), 100);
        var second = tomatoes.ReceivePurchase(5_000, Guid.NewGuid(), 150);
        tomatoes.RecordSale(8_000, Guid.NewGuid());

        tomatoes.Reverse(second, "Omar");

        // 2 kg were left: taking the 5 kg back out goes below zero, and there is no stock to
        // work an average out from.
        Assert.Equal((-3_000m, 0.025m), (tomatoes.QuantityOnHand, tomatoes.AverageCost));
    }

    [Fact]
    public void Voided_waste_and_counts_put_the_stock_back_as_it_was()
    {
        var milk = new RawMaterial("Milk", UnitOfMeasure.Millilitre);
        milk.ReceivePurchase(10_000, Guid.NewGuid(), 300);
        var waste = milk.RecordSpoilage(2_000, Guid.NewGuid(), "Expired", "Sara");
        milk.Reverse(waste, "Omar");
        Assert.Equal((10_000m, 0.03m), (milk.QuantityOnHand, milk.AverageCost));

        var count = new StockCount(Guid.NewGuid(), "Sara");
        count.Record([(milk.Id, 9_400m)]);
        var adjustment = Assert.Single(count.Post(new Dictionary<Guid, RawMaterial> { [milk.Id] = milk }, "Sara"));
        Assert.Equal(9_400m, milk.QuantityOnHand);

        milk.Reverse(adjustment, "Omar");
        count.Void("Omar");
        Assert.Equal((10_000m, StockCountStatus.Voided, "Omar"), (milk.QuantityOnHand, count.Status, count.VoidedBy));
        Assert.Throws<DomainException>(() => count.Record([(milk.Id, 1m)]));
    }

    [Fact]
    public void Only_a_posted_invoice_is_voided_and_a_voided_one_is_never_posted_again()
    {
        var tomatoes = new RawMaterial("Tomatoes", UnitOfMeasure.Gram);
        var invoice = new PurchaseInvoice(Guid.NewGuid(), "Sara");
        invoice.Record(null, null, null, [new PurchaseInvoiceLineEntry(tomatoes.Id, 5, "kg", 1000, 100)], PurchasePaymentMethod.Safe);
        Assert.Throws<DomainException>(() => invoice.Void("Omar"));

        invoice.Post(new Dictionary<Guid, RawMaterial> { [tomatoes.Id] = tomatoes }, "Sara");
        invoice.Void("Omar");
        invoice.Void("Omar");

        Assert.Equal((PurchaseInvoiceStatus.Voided, "Omar"), (invoice.Status, invoice.VoidedBy));
        Assert.Throws<DomainException>(() => invoice.Post(new Dictionary<Guid, RawMaterial> { [tomatoes.Id] = tomatoes }, "Omar"));
        Assert.Throws<DomainException>(() => invoice.Record(null, null, null, [], PurchasePaymentMethod.Safe));
    }

    [Fact]
    public void A_material_is_archived_only_with_nothing_on_the_shelf()
    {
        var salt = new RawMaterial("Salt", UnitOfMeasure.Gram);
        salt.ReceivePurchase(1_000, Guid.NewGuid(), 10);
        Assert.Throws<DomainException>(() => salt.Archive("Omar"));

        salt.AdjustToCount(0, Guid.NewGuid());
        salt.Archive("Omar");

        Assert.Equal((true, "Omar"), (salt.IsArchived, salt.ArchivedBy));
    }

    [Fact]
    public void A_voided_batch_puts_its_ingredients_back_and_takes_its_cost_out_of_the_prep_items_average()
    {
        var flour = new RawMaterial("Flour", UnitOfMeasure.Gram);
        flour.ReceivePurchase(20_000, Guid.NewGuid(), 400); // 0.02 a gram
        var dough = new RawMaterial("Pizza dough", UnitOfMeasure.Gram, kind: MaterialKind.Prep);
        var recipe = new PrepRecipe(dough, 10_000);
        recipe.Replace([(flour, 6_000m, null)]);
        var materials = new Dictionary<Guid, RawMaterial> { [flour.Id] = flour };
        Make(5_000);                                  // 60 for 5 kg: 0.012 a gram
        var (second, entries) = Make(10_000);         // 120 for 10 kg, the same price
        flour.ReceivePurchase(10_000, Guid.NewGuid(), 600); // dearer flour, after the batch

        foreach (var entry in entries)
            (entry.RawMaterialId == flour.Id ? flour : dough).Reverse(entry, "Omar");
        second.Void("Omar");

        Assert.Equal((5_000m, 0.012m), (dough.QuantityOnHand, dough.AverageCost));
        // The 6 kg come back at the 0.02 they were taken at; the average moved only by the delivery.
        Assert.Equal(20_000m - 3_000m + 10_000m, flour.QuantityOnHand);
        Assert.Equal((ProductionOrderStatus.Voided, "Omar", (Guid?)null), (second.Status, second.VoidedBy, second.ReplacedBy));

        (ProductionOrder, IReadOnlyList<StockMovement>) Make(decimal grams) =>
            ProductionOrder.Make(Guid.NewGuid(), dough, recipe, materials, grams, grams, "Mona", approvedBy: "Omar");
    }

    [Fact]
    public void A_deleted_prep_item_is_no_longer_made()
    {
        var flour = new RawMaterial("Flour", UnitOfMeasure.Gram);
        flour.ReceivePurchase(1_000, Guid.NewGuid(), 20);
        var dough = new RawMaterial("Pizza dough", UnitOfMeasure.Gram, kind: MaterialKind.Prep);
        var recipe = new PrepRecipe(dough, 1_000);
        recipe.Replace([(flour, 600m, null)]);
        dough.Archive("Omar");

        Assert.Throws<DomainException>(() => ProductionOrder.Make(
            Guid.NewGuid(), dough, recipe, new Dictionary<Guid, RawMaterial> { [flour.Id] = flour }, 1_000, 1_000, "Mona"));
    }

    [Fact]
    public void Only_prep_items_and_batches_are_added_or_changed_through_an_approval()
    {
        Assert.Throws<DomainException>(() => ApprovalRequest.AskToChange(
            Guid.NewGuid(), ApprovalModule.PurchaseInvoice, ApprovalAction.Create, Guid.NewGuid(), "Invoice", [], "{}", null, "staff-1", "Mona"));
        Assert.Throws<DomainException>(() => ApprovalRequest.AskToChange(
            Guid.NewGuid(), ApprovalModule.ProductionOrder, ApprovalAction.Update, Guid.NewGuid(), "Batch", [], " ", null, "staff-1", "Mona"));

        var asked = ApprovalRequest.AskToChange(
            Guid.NewGuid(), ApprovalModule.ProductionOrder, ApprovalAction.Update, Guid.NewGuid(), "Batch of dough",
            [new ApprovalChange("Quantity to produce", "10 kg", "12 kg")], "{\"plannedQuantity\":12000}", "Weighed again", "staff-1", "Mona");
        Assert.Equal((ApprovalAction.Update, ApprovalStatus.Pending, "12 kg"), (asked.Action, asked.Status, asked.Changes.Single().After));
        // A deletion holds no change.
        Assert.Null(ApprovalRequest.Ask(Guid.NewGuid(), ApprovalModule.PrepItem, Guid.NewGuid(), "Prep item Dough", null, "staff-1", "Mona").Payload);
        // A posted invoice is corrected through one: a new invoice is not asked for.
        Assert.Equal(ApprovalStatus.Pending, ApprovalRequest.AskToChange(
            Guid.NewGuid(), ApprovalModule.PurchaseInvoice, ApprovalAction.Update, Guid.NewGuid(), "Invoice F-1 corrected", [], "{}", null, "staff-1", "Mona").Status);
        // And a raw material's unit changed: a new material is not asked for either.
        Assert.Equal(ApprovalStatus.Pending, ApprovalRequest.AskToChange(
            Guid.NewGuid(), ApprovalModule.RawMaterial, ApprovalAction.Update, Guid.NewGuid(), "Tomatoes counted in pieces", [], "{}", null, "staff-1", "Mona").Status);
        Assert.Throws<DomainException>(() => ApprovalRequest.AskToChange(
            Guid.NewGuid(), ApprovalModule.RawMaterial, ApprovalAction.Create, Guid.NewGuid(), "Tomatoes", [], "{}", null, "staff-1", "Mona"));
        // A category's deletion holds where its contents go and what they become.
        var category = ApprovalRequest.AskToChange(
            Guid.NewGuid(), ApprovalModule.PurchaseCategory, ApprovalAction.Delete, Guid.NewGuid(), "Category Cleaning deleted", [], "{\"convert\":\"ToExpenses\"}",
            null, "staff-1", "Mona");
        Assert.Equal((ApprovalAction.Delete, "{\"convert\":\"ToExpenses\"}"), (category.Action, category.Payload));
    }

    [Fact]
    public void A_request_is_decided_once()
    {
        var asked = ApprovalRequest.Ask(Guid.NewGuid(), ApprovalModule.Recipe, Guid.NewGuid(), "Recipe Pasta", "Duplicate", "staff-1", "Sara");
        Assert.Equal((ApprovalStatus.Pending, "Sara", "Duplicate"), (asked.Status, asked.RequestedBy, asked.Reason));

        asked.Reject("Omar", "  Still sold on Fridays ");
        asked.Reject("Omar", null);
        Assert.Equal((ApprovalStatus.Rejected, "Omar", "Still sold on Fridays"), (asked.Status, asked.DecidedBy, asked.DecisionNote));
        Assert.Throws<DomainException>(() => asked.Approve("Omar"));

        var direct = ApprovalRequest.Decided(Guid.NewGuid(), ApprovalModule.Waste, Guid.NewGuid(), "Waste of 8 Oct", null, "staff-2", "Omar");
        Assert.Equal((ApprovalStatus.Approved, "Omar", "Omar"), (direct.Status, direct.RequestedBy, direct.DecidedBy));
        Assert.Throws<DomainException>(() => direct.Reject("Omar", null));
    }
}
