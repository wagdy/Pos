using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Custody;
using OtantikPos.Hr.Domain.Employees;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Waste;

namespace Otantik.Domain.Tests;

// Custody & Assets (عهدة وأصول): what is in whose custody, the vouchers that move it, breakage as a
// day's waste, and the employee portal's PIN.
public class CustodyTests
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly Guid Hassan = Guid.NewGuid();
    private static readonly Guid Mona = Guid.NewGuid();

    private static AssetItem Plates(int quantity = 40, decimal? unitCost = 35) =>
        new(Guid.NewGuid(), " Dinner plates ", Guid.NewGuid(), unitCost, quantity, AssetOrigin.HrManual, "Omar");

    [Fact]
    public void A_category_is_named_and_consumable_or_a_fixed_asset()
    {
        var tableware = new AssetCategory(Guid.NewGuid(), " Tableware ", isConsumable: true);
        Assert.Equal(("Tableware", true), (tableware.Name, tableware.IsConsumable));
        tableware.SetConsumable(false);
        Assert.False(tableware.IsConsumable);
        Assert.Throws<DomainException>(() => tableware.Rename(" "));
        Assert.Throws<DomainException>(() => new AssetCategory(Guid.NewGuid(), new string('x', AssetCategory.MaxNameLength + 1), false));
    }

    [Fact]
    public void An_asset_is_a_number_of_one_kind_at_a_cost_a_piece_in_the_store_until_handed_out()
    {
        var plates = Plates(unitCost: 35.555m);
        Assert.Equal(("Dinner plates", 40, 35.56m, AssetStatus.InStore, (Guid?)null), (plates.Name, plates.Quantity, plates.UnitCost, plates.Status, plates.EmployeeId));
        Assert.Equal(1_422.40m, plates.TotalCost);
        Assert.Null(Plates(unitCost: null).TotalCost);

        Assert.Throws<DomainException>(() => Plates(quantity: 0));
        Assert.Throws<DomainException>(() => Plates(unitCost: -1));
        Assert.Throws<DomainException>(() => new AssetItem(Guid.NewGuid(), "Fryer", null, 9_000, 1, AssetOrigin.FinancialPurchase, "Omar"));
        Assert.Throws<DomainException>(() => new AssetItem(Guid.NewGuid(), "Fryer", null, 9_000, 1, AssetOrigin.HrManual, "Omar", Guid.NewGuid(), Guid.NewGuid()));
        var bought = new AssetItem(Guid.NewGuid(), "Fryer", null, 9_000, 1, AssetOrigin.FinancialPurchase, "Omar", Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(AssetOrigin.FinancialPurchase, bought.Origin);
    }

    [Fact]
    public void A_check_in_puts_all_of_it_in_one_employees_custody_and_another_takes_it_only_once_it_is_out()
    {
        var plates = Plates();
        var voucher = plates.CheckIn(Guid.NewGuid(), Hassan, Monday, " Kitchen ", "Omar");
        Assert.Equal((AssetStatus.InCustody, (Guid?)Hassan), (plates.Status, plates.EmployeeId));
        Assert.Equal((AssetTransactionType.CheckIn, 40, (AssetMovementReason?)null, "Kitchen", 35m, 1_400m), (voucher.Type, voucher.Quantity, voucher.Reason, voucher.Notes, voucher.UnitCost, voucher.Cost));

        Assert.Contains("in their custody already", Assert.Throws<DomainException>(() => plates.CheckIn(Guid.NewGuid(), Hassan, Monday, null, "Omar")).Message);
        Assert.Contains("someone else's custody", Assert.Throws<DomainException>(() => plates.CheckIn(Guid.NewGuid(), Mona, Monday, null, "Omar")).Message);
        Assert.Throws<DomainException>(() => Plates().CheckIn(Guid.NewGuid(), Guid.Empty, Monday, null, "Omar"));

        // Handed back, all of it; then Mona's.
        Assert.Contains("All 40 of them", Assert.Throws<DomainException>(() => plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Returned, 10, Monday, null, "Omar")).Message);
        var back = plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Returned, 40, Monday, null, "Omar");
        Assert.Equal((AssetStatus.InStore, (Guid?)null, (Guid?)Hassan), (plates.Status, plates.EmployeeId, back.EmployeeId));
        Assert.Throws<DomainException>(() => plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Returned, 40, Monday, null, "Omar"));
        plates.CheckIn(Guid.NewGuid(), Mona, Monday, null, "Omar");
        Assert.Equal((Guid?)Mona, plates.EmployeeId);
    }

    [Fact]
    public void Some_broken_lost_or_thrown_away_leave_the_rest_in_custody_and_none_left_is_broken_or_discarded()
    {
        var plates = Plates();
        plates.CheckIn(Guid.NewGuid(), Hassan, Monday, null, "Omar");

        var broken = plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Breakage, 3, Monday, "Dropped a tray", "Omar");
        Assert.Equal((37, AssetStatus.InCustody, (Guid?)Hassan), (plates.Quantity, plates.Status, plates.EmployeeId));
        Assert.Equal((AssetTransactionType.CheckOut, 3, (AssetMovementReason?)AssetMovementReason.Breakage, 105m, (Guid?)Hassan),
            (broken.Type, broken.Quantity, broken.Reason, broken.Cost, broken.EmployeeId));
        Assert.Contains("Between 1 and 37", Assert.Throws<DomainException>(() => plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Lost, 38, Monday, null, "Omar")).Message);
        Assert.Throws<DomainException>(() => plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Lost, 0, Monday, null, "Omar"));

        plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Breakage, 37, Monday, null, "Omar");
        Assert.Equal((0, AssetStatus.BrokenWaste, (Guid?)null, true), (plates.Quantity, plates.Status, plates.EmployeeId, plates.IsGone));
        Assert.Contains("None of it is left", Assert.Throws<DomainException>(() => plates.CheckIn(Guid.NewGuid(), Hassan, Monday, null, "Omar")).Message);
        Assert.Throws<DomainException>(() => plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Lost, 1, Monday, null, "Omar"));

        // A knife lost from the store: no one held it.
        var knife = new AssetItem(Guid.NewGuid(), "Chef's knife", null, 400, 1, AssetOrigin.HrManual, "Omar");
        var lost = knife.CheckOut(Guid.NewGuid(), AssetMovementReason.Lost, 1, Monday, null, "Omar");
        Assert.Equal((AssetStatus.Discarded, (Guid?)null), (knife.Status, lost.EmployeeId));
        Assert.Throws<DomainException>(() => Plates().CheckOut(Guid.NewGuid(), (AssetMovementReason)99, 1, Monday, null, "Omar"));
    }

    [Fact]
    public void Maintenance_takes_all_of_it_from_its_custodian_and_it_comes_back_by_a_check_in_or_to_the_store()
    {
        var fryer = new AssetItem(Guid.NewGuid(), "Fryer", null, 9_000, 1, AssetOrigin.HrManual, "Omar");
        fryer.CheckIn(Guid.NewGuid(), Hassan, Monday, null, "Omar");
        fryer.CheckOut(Guid.NewGuid(), AssetMovementReason.Maintenance, 1, Monday, "Thermostat", "Omar");
        Assert.Equal((AssetStatus.Maintenance, (Guid?)null), (fryer.Status, fryer.EmployeeId));
        Assert.Throws<DomainException>(() => fryer.CheckOut(Guid.NewGuid(), AssetMovementReason.Maintenance, 1, Monday, null, "Omar"));

        fryer.CheckIn(Guid.NewGuid(), Mona, Monday.AddDays(3), "Repaired", "Omar");
        Assert.Equal((AssetStatus.InCustody, (Guid?)Mona), (fryer.Status, fryer.EmployeeId));
        fryer.CheckOut(Guid.NewGuid(), AssetMovementReason.Maintenance, 1, Monday.AddDays(5), null, "Omar");
        fryer.CheckOut(Guid.NewGuid(), AssetMovementReason.Returned, 1, Monday.AddDays(8), "Back, kept in the store", "Omar");
        Assert.Equal(AssetStatus.InStore, fryer.Status);
    }

    [Fact]
    public void A_breakage_goes_to_waste_once_and_its_entry_costs_what_broke()
    {
        var plates = Plates();
        var broken = plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Breakage, 3, Monday, null, "Omar");
        var entry = new BreakageEntry(Guid.NewGuid(), Monday, plates.Id, broken.Id, plates.Name, broken.Quantity, broken.UnitCost, broken.EmployeeId, " Dropped ", "Omar");
        Assert.Equal(("Dinner plates", 3, 105m, "Dropped"), (entry.Description, entry.Quantity, entry.Cost, entry.Notes));

        broken.RouteToWaste(entry.Id);
        Assert.Equal((true, (Guid?)entry.Id), (broken.RoutedToWaste, broken.WasteEntryId));
        Assert.Throws<DomainException>(() => broken.RouteToWaste(Guid.NewGuid()));
        var lost = plates.CheckOut(Guid.NewGuid(), AssetMovementReason.Lost, 1, Monday, null, "Omar");
        Assert.Throws<DomainException>(() => lost.RouteToWaste(Guid.NewGuid()));

        // An asset with no cost known breaks at nothing.
        var free = new BreakageEntry(Guid.NewGuid(), Monday, plates.Id, Guid.NewGuid(), "Glass", 2, null, null, null, "Omar");
        Assert.Equal((0m, (decimal?)null), (free.Cost, free.UnitCost));
        Assert.Throws<DomainException>(() => new BreakageEntry(Guid.NewGuid(), Monday, plates.Id, Guid.NewGuid(), " ", 1, 5, null, null, "Omar"));
        Assert.Throws<DomainException>(() => new BreakageEntry(Guid.NewGuid(), Monday, plates.Id, Guid.NewGuid(), "Glass", 0, 5, null, null, "Omar"));

        entry.Void("Omar");
        Assert.True(entry.IsVoided);
        Assert.Throws<DomainException>(() => entry.Void("Omar"));
    }

    [Fact]
    public void An_employees_portal_pin_is_4_to_6_digits_kept_hashed_and_locked_for_5_minutes_after_5_misses()
    {
        Assert.Equal("1234", Employee.Pin(" ١٢٣٤ "));
        Assert.Equal("123456", Employee.Pin("123456"));
        Assert.Throws<DomainException>(() => Employee.Pin("123"));
        Assert.Throws<DomainException>(() => Employee.Pin("1234567"));
        Assert.Throws<DomainException>(() => Employee.Pin("12a4"));
        Assert.Throws<DomainException>(() => Employee.Pin(null));

        var chef = new Employee(Guid.NewGuid(), "Hassan", "Chef", 5_400, new TimeOnly(10, 0), WeekDays.Friday, new DateOnly(2026, 1, 1));
        Assert.False(chef.HasPin);
        // They sign in with their mobile: a PIN waits for one.
        Assert.Contains("mobile number first", Assert.Throws<DomainException>(() => chef.SetPinHash("hash")).Message);
        chef.SetMobileNumber("01012345678");
        chef.SetPinHash("hash");
        Assert.True(chef.HasPin);
        Assert.NotNull(chef.PinSetAtUtc);

        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < Employee.MaxFailedPinAttempts - 1; i++)
            chef.PinRefused(now);
        Assert.Equal((4, false), (chef.FailedPinAttempts, chef.IsPinLockedAt(now)));
        chef.PinRefused(now);
        Assert.Equal((0, true), (chef.FailedPinAttempts, chef.IsPinLockedAt(now)));
        Assert.False(chef.IsPinLockedAt(now + Employee.PinLockout));

        // A manager setting a new one lets them straight back in.
        chef.SetPinHash("new hash");
        Assert.Equal((false, "new hash"), (chef.IsPinLockedAt(now), chef.PinHash));
        chef.PinRefused(now);
        chef.PinAccepted("rehashed");
        Assert.Equal((0, "rehashed"), (chef.FailedPinAttempts, chef.PinHash));
        chef.ClearPin();
        Assert.Equal((false, (string?)null), (chef.HasPin, chef.PinHash));
    }

    [Fact]
    public void An_expense_marked_an_asset_names_its_custody_category()
    {
        var plates = new ExpenseItem(Guid.NewGuid(), "Dinner plates", Guid.NewGuid());
        Assert.False(plates.IsAsset);
        var tableware = Guid.NewGuid();
        plates.MarkAsAsset(tableware);
        Assert.Equal((true, (Guid?)tableware), (plates.IsAsset, plates.AssetCategoryId));
        Assert.Throws<DomainException>(() => plates.MarkAsAsset(Guid.Empty));
        plates.MarkAsAsset(null);
        Assert.False(plates.IsAsset);
    }
}
