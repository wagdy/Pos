using Otantik.SharedKernel.Orders;
using OtantikPos.Inventory.Application.Recipes;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Ordering.Application.Orders;

namespace OtantikPos.Application.Tests;

// The event-driven path end to end: the till settles an order, the outbox delivers
// OrderSettled, and Inventory deducts through the recipes. The two modules share nothing but
// the event.
public class InventoryTests
{
    private readonly TestPos _pos = new();
    private readonly RawMaterial _beef;
    private readonly RawMaterial _bun;
    private readonly RawMaterial _cheese;
    private readonly RawMaterial _chicken;

    public InventoryTests()
    {
        _beef = _pos.Inventory.Stock("Beef patty", UnitOfMeasure.Piece, 50);
        _bun = _pos.Inventory.Stock("Bun", UnitOfMeasure.Piece, 50);
        _cheese = _pos.Inventory.Stock("Cheese slice", UnitOfMeasure.Piece, 50);
        _chicken = _pos.Inventory.Stock("Chicken", UnitOfMeasure.Gram, 5000);
    }

    private async Task Recipes()
    {
        await _pos.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, TestPos.Burger, null, [new(_beef.Id, 1), new(_bun.Id, 1)]));
        await _pos.Send(new SetRecipeCommand(RecipeTargetKind.AddOn, TestPos.Cheese, null, [new(_cheese.Id, 1)]));
        await _pos.Send(new SetRecipeCommand(RecipeTargetKind.MenuItem, TestPos.Shawarma, TestPos.KiloTray, [new(_chicken.Id, 1000)]));
    }

    private async Task<Order> PaidOrder()
    {
        var order = await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "3"));
        await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Burger, 2, AddOnIds: [TestPos.Cheese]));
        await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Shawarma, VariantId: TestPos.KiloTray));
        await _pos.Send(new SendToKitchenCommand(order.PublicId));
        return await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
    }

    [Fact]
    public async Task A_settled_order_deducts_stock_through_recipes_exactly_once()
    {
        await Recipes();
        await PaidOrder();

        Assert.Equal(50, _beef.QuantityOnHand);
        await _pos.DeliverEventsAsync();

        Assert.Equal(48, _beef.QuantityOnHand);
        Assert.Equal(48, _bun.QuantityOnHand);
        Assert.Equal(48, _cheese.QuantityOnHand);
        Assert.Equal(4000, _chicken.QuantityOnHand);

        await _pos.RedeliverAllAsync();
        Assert.Equal(48, _beef.QuantityOnHand);
        Assert.Equal(4000, _chicken.QuantityOnHand);
    }

    [Fact]
    public async Task A_void_after_the_kitchen_is_waste_and_a_void_after_payment_goes_back_to_stock()
    {
        await Recipes();
        var order = await _pos.Send(new OpenOrderCommand(OrderType.DineIn, TableNumber: "3"));
        order = await _pos.Send(new AddOrderItemCommand(order.PublicId, TestPos.Burger, 2));
        await _pos.Send(new SendToKitchenCommand(order.PublicId));

        order = await _pos.Send(new VoidOrderItemCommand(order.PublicId, order.OrderItems.Single().PublicId, 1, "dropped"));
        await _pos.DeliverEventsAsync();
        Assert.Equal(49, _beef.QuantityOnHand);

        order = await _pos.Send(new CheckoutCommand(order.PublicId, PaymentMethod.Cash));
        await _pos.DeliverEventsAsync();
        Assert.Equal(48, _beef.QuantityOnHand);

        var standing = order.OrderItems.Single(i => !i.IsVoided);
        await _pos.Send(new VoidOrderItemCommand(order.PublicId, standing.PublicId, 1, "complaint"));
        await _pos.DeliverEventsAsync();
        Assert.Equal(49, _beef.QuantityOnHand);

        var reasons = _pos.Inventory.Movements.All.Where(m => m.RawMaterialId == _beef.Id).Select(m => m.Reason).ToList();
        Assert.Equal(
            [Inventory.Domain.StockMovements.StockMovementReason.Waste, Inventory.Domain.StockMovements.StockMovementReason.Sale, Inventory.Domain.StockMovements.StockMovementReason.VoidReturn],
            reasons);
    }

    [Fact]
    public async Task A_menu_item_with_no_recipe_is_sold_without_touching_stock()
    {
        await PaidOrder();
        await _pos.DeliverEventsAsync();

        Assert.Empty(_pos.Inventory.Movements.All);
    }
}
