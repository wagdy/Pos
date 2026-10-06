using Otantik.SharedKernel.Orders;

namespace OtantikPos.Ordering.Domain;

public sealed record MergeResult(
    bool Changed,
    // Items the delivery system has marked sent that this till has not printed yet.
    IReadOnlyList<OrderItem> NewlySent,
    // Non-empty when the delivery system cancelled the order and the till applied it.
    IReadOnlyList<VoidedLine> Cancelled,
    // Set when some or all of the incoming change was not applied, and why.
    string? Ignored)
{
    public static readonly MergeResult Unchanged = new(false, [], [], null);
}

// How an order arriving from the delivery system lands on the till's copy. That covers a
// captain's dine-in order, an online order, and either one changed again in the cloud later.
//
// Two systems can change the same order, so each part of it has one owner:
//   the delivery system owns new items (a captain's next round), and can change who the
//     customer is, the table and the notes, but never blank them: the till sets the
//     customer too, by mobile number;
//   the till owns money and voids: payment, refunds, points, and every void and price;
//   the kitchen's "sent" mark only ever moves forward, so whichever side sets it first wins;
//   the status only ever moves forward too, so a late message cannot undo progress.
//
// Once the till has taken payment or cancelled the order, the till's copy is final and
// incoming changes are ignored. Merging is idempotent: the till's own change, echoed back by
// the cloud, merges as no change at all.
public static class CloudOrderMerge
{
    public const string CloudUserId = "system:delivery-sync";

    // A first sight of an order: it becomes the till's own copy. Ids are this database's own,
    // so the cloud's are cleared and assigned again on save; PublicIds stay, being the order's
    // identity everywhere.
    public static Order Import(Order incoming)
    {
        incoming.Id = 0;
        incoming.User = null;
        foreach (var item in incoming.OrderItems)
        {
            item.Id = 0;
            item.OrderId = 0;
            item.Order = incoming;
            foreach (var addOn in item.AddOns)
            {
                addOn.Id = 0;
                addOn.OrderItemId = 0;
                addOn.OrderItem = item;
            }
        }
        return incoming;
    }

    public static MergeResult Merge(Order local, Order incoming, decimal taxPercentage, DateTime now)
    {
        // Paid or cancelled. The delivery system applies the same rule from its side: a round
        // that arrives here too late is voided there when this copy reaches it.
        if (!OrderRules.AwaitsPayment(local))
            return MergeResult.Unchanged with { Ignored = "The order is already closed at the till; the till's copy stands." };

        var changed = false;
        var repriceNeeded = false;

        // Cloud-owned details, taken only when the incoming value is present. The till can set
        // the customer too (looking them up by mobile number), and a cloud message that left
        // before the till's change reached the cloud would otherwise blank it out again.
        if (Present(incoming.UserId) && incoming.UserId != local.UserId)
        {
            // Points belong to the account they were applied for.
            if (local.PointsRedeemed > 0)
            {
                local.PointsRedeemed = 0;
                local.PointsDiscountAmount = 0;
                repriceNeeded = true;
            }
            local.UserId = incoming.UserId;
            changed = true;
        }
        changed |= AssignIfPresent(local.CustomerName, incoming.CustomerName, v => local.CustomerName = v);
        changed |= AssignIfPresent(local.CustomerPhone, incoming.CustomerPhone, v => local.CustomerPhone = v);
        changed |= AssignIfPresent(local.DeliveryAddress, incoming.DeliveryAddress, v => local.DeliveryAddress = v);
        changed |= AssignIfPresent(local.TableNumber, incoming.TableNumber, v => local.TableNumber = v);
        changed |= AssignIfPresent(local.Notes, incoming.Notes, v => local.Notes = v);

        // Items: new ones are added. Existing ones only ever gain a "sent to kitchen" time;
        // their price, quantity and voids are the till's.
        var newlySent = new List<OrderItem>();
        var itemsAdded = false;
        var mine = local.OrderItems.ToDictionary(i => i.PublicId);
        foreach (var theirs in incoming.OrderItems)
        {
            if (mine.TryGetValue(theirs.PublicId, out var item))
            {
                if (item.SentToKitchenAt is null && theirs.SentToKitchenAt is not null && !item.IsVoided)
                {
                    item.SentToKitchenAt = theirs.SentToKitchenAt;
                    newlySent.Add(item);
                    changed = true;
                }
                continue;
            }

            // Never seen here and already voided there: nothing for the till to show.
            if (theirs.IsVoided)
                continue;

            var copy = Copy(theirs, local);
            local.OrderItems.Add(copy);
            if (copy.IsSentToKitchen)
                newlySent.Add(copy);
            itemsAdded = changed = true;
        }

        if (itemsAdded || repriceNeeded)
            OrderPricing.Reprice(local, taxPercentage);

        // Status, forward only. A cancellation is accepted only while nothing has reached the
        // kitchen: then it is a plain Void Before. After that, the food is being made, and the
        // decision belongs to the till, which can record the waste.
        IReadOnlyList<VoidedLine> cancelled = [];
        string? ignored = null;
        if (incoming.Status == OrderStatus.Cancelled)
        {
            if (OrderRules.KitchenHasIt(local))
            {
                ignored = "The order was cancelled in the delivery system, but the kitchen already has it. Void it at the till if it should go.";
            }
            else
            {
                cancelled = TillOperations.VoidOrder(local, "Cancelled in the delivery system", CloudUserId, taxPercentage, now);
                changed = true;
            }
        }
        else if (OrderStatuses.Progress(incoming.Status) > OrderStatuses.Progress(local.Status))
        {
            local.Status = incoming.Status;
            changed = true;
        }

        if (changed)
            local.UpdatedAt = now;

        return new MergeResult(changed, newlySent, cancelled, ignored);
    }

    private static OrderItem Copy(OrderItem theirs, Order local) => new()
    {
        PublicId = theirs.PublicId,
        Order = local,
        MenuItemId = theirs.MenuItemId,
        MenuItemName = theirs.MenuItemName,
        VariantId = theirs.VariantId,
        VariantName = theirs.VariantName,
        Quantity = theirs.Quantity,
        UnitPrice = theirs.UnitPrice,
        Notes = theirs.Notes,
        SentToKitchenAt = theirs.SentToKitchenAt,
        AddOns = theirs.AddOns.Select(a => new OrderItemAddOn { AddOnId = a.AddOnId, Name = a.Name, Price = a.Price }).ToList(),
    };

    private static bool AssignIfPresent(string? current, string? incoming, Action<string> set)
    {
        if (!Present(incoming) || incoming == current)
            return false;
        set(incoming!);
        return true;
    }

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
}
