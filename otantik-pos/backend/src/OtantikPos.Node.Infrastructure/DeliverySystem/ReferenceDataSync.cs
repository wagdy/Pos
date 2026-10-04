using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Otantik.SharedKernel.Customers;
using Otantik.SharedKernel.Catalog;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Identity;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Infrastructure.DeliverySystem;

// Applies the delivery system's menu, tax rate and staff list to this machine's copy. The
// delivery system owns all three; the till only reads them, so a sale goes through whether or
// not the internet is up.
public sealed class ReferenceDataApplier(NodeDbContext db)
{
    public async Task ApplyAsync(ReferenceData data, DateTime now, CancellationToken cancellationToken)
    {
        var settings = await db.Settings.FindAsync([RestaurantSettingsCopy.SingletonId], cancellationToken);
        if (settings is null)
            db.Settings.Add(settings = new RestaurantSettingsCopy());
        settings.TaxPercentage = data.TaxPercentage;
        settings.RedemptionValuePer100Points = data.RedemptionValuePer100Points ?? LoyaltyRedemption.DefaultValuePer100Points;
        settings.SyncedAt = now;

        await ApplyCategoriesAsync(data, cancellationToken);
        await ApplyAddOnsAsync(data, cancellationToken);
        await ApplyMenuItemsAsync(data, cancellationToken);
        await ApplyStaffAsync(data, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    // Categories are soft-deleted when they disappear, as the delivery system does itself.
    // Sub-categories have no such flag and are removed; their menu items keep going, unfiled.
    private async Task ApplyCategoriesAsync(ReferenceData data, CancellationToken cancellationToken)
    {
        var categories = await db.Categories.ToDictionaryAsync(c => c.Id, cancellationToken);
        foreach (var incoming in data.Categories)
        {
            if (!categories.TryGetValue(incoming.Id, out var category))
                db.Categories.Add(category = new Category { Id = incoming.Id });
            category.Name = incoming.Name;
            category.NameAr = incoming.NameAr;
            category.DisplayOrder = incoming.DisplayOrder;
            category.IsDeleted = incoming.IsDeleted;
            category.ImageUrl = incoming.ImageUrl;
        }
        foreach (var gone in categories.Values.Where(c => data.Categories.All(i => i.Id != c.Id)))
            gone.IsDeleted = true;

        var subCategories = await db.SubCategories.ToDictionaryAsync(s => s.Id, cancellationToken);
        foreach (var incoming in data.SubCategories)
        {
            if (!subCategories.TryGetValue(incoming.Id, out var subCategory))
                db.SubCategories.Add(subCategory = new SubCategory { Id = incoming.Id });
            subCategory.Name = incoming.Name;
            subCategory.NameAr = incoming.NameAr;
            subCategory.DisplayOrder = incoming.DisplayOrder;
            subCategory.CategoryId = incoming.CategoryId;
        }
        db.SubCategories.RemoveRange(subCategories.Values.Where(s => data.SubCategories.All(i => i.Id != s.Id)));
    }

    private async Task ApplyAddOnsAsync(ReferenceData data, CancellationToken cancellationToken)
    {
        var addOns = await db.AddOns.ToDictionaryAsync(a => a.Id, cancellationToken);
        foreach (var incoming in data.AddOns)
        {
            if (!addOns.TryGetValue(incoming.Id, out var addOn))
                db.AddOns.Add(addOn = new AddOn { Id = incoming.Id });
            addOn.Name = incoming.Name;
            addOn.NameAr = incoming.NameAr;
            addOn.Price = incoming.Price;
        }
        db.AddOns.RemoveRange(addOns.Values.Where(a => data.AddOns.All(i => i.Id != a.Id)));
    }

    // A menu item that disappears is marked deleted, never removed: past orders, recipes and
    // stock movements still name it.
    private async Task ApplyMenuItemsAsync(ReferenceData data, CancellationToken cancellationToken)
    {
        var items = await db.MenuItems
            .Include(m => m.Variants)
            .Include(m => m.MenuItemAddOns)
            .ToDictionaryAsync(m => m.Id, cancellationToken);
        var knownSubCategories = data.SubCategories.Select(s => s.Id).ToHashSet();
        var knownAddOns = data.AddOns.Select(a => a.Id).ToHashSet();

        foreach (var incoming in data.MenuItems)
        {
            if (!items.TryGetValue(incoming.Id, out var item))
                db.MenuItems.Add(item = new MenuItem { Id = incoming.Id });

            item.Name = incoming.Name;
            item.NameAr = incoming.NameAr;
            item.Description = incoming.Description;
            item.Price = incoming.Price;
            item.Category = incoming.Category;
            item.ImageUrl = incoming.ImageUrl;
            item.IsAvailable = incoming.IsAvailable;
            item.IsDeleted = incoming.IsDeleted;
            item.PriceNote = incoming.PriceNote;
            item.IsPriceBasedOnAddons = incoming.IsPriceBasedOnAddons;
            item.IsOffer = incoming.IsOffer;
            item.OriginalPrice = incoming.OriginalPrice;
            item.DiscountPercentage = incoming.DiscountPercentage;
            item.SubCategoryId = incoming.SubCategoryId is { } sub && knownSubCategories.Contains(sub) ? sub : null;

            ApplyVariants(item, incoming);

            var wanted = incoming.MenuItemAddOns.Select(ma => ma.AddOnId).Where(knownAddOns.Contains).ToHashSet();
            foreach (var link in item.MenuItemAddOns.Where(ma => !wanted.Contains(ma.AddOnId)).ToList())
                item.MenuItemAddOns.Remove(link);
            foreach (var addOnId in wanted.Where(id => item.MenuItemAddOns.All(ma => ma.AddOnId != id)))
                item.MenuItemAddOns.Add(new MenuItemAddOn { MenuItemId = item.Id, AddOnId = addOnId });
        }

        foreach (var gone in items.Values.Where(m => data.MenuItems.All(i => i.Id != m.Id)))
            gone.IsDeleted = true;
    }

    private void ApplyVariants(MenuItem item, MenuItem incoming)
    {
        foreach (var variant in item.Variants.Where(v => incoming.Variants.All(i => i.Id != v.Id)).ToList())
        {
            item.Variants.Remove(variant);
            db.MenuItemVariants.Remove(variant);
        }

        foreach (var incomingVariant in incoming.Variants)
        {
            var variant = item.Variants.FirstOrDefault(v => v.Id == incomingVariant.Id);
            if (variant is null)
                item.Variants.Add(variant = new MenuItemVariant { Id = incomingVariant.Id, MenuItemId = item.Id });
            variant.Name = incomingVariant.Name;
            variant.NameAr = incomingVariant.NameAr;
            variant.Price = incomingVariant.Price;
            variant.DisplayOrder = incomingVariant.DisplayOrder;
            variant.IsAvailable = incomingVariant.IsAvailable;
        }
    }

    // Accounts come from the delivery system; PINs stay here and are never touched by a sync.
    // Someone who disappears from the list is deactivated, never deleted, because audit
    // entries name them.
    private async Task ApplyStaffAsync(ReferenceData data, CancellationToken cancellationToken)
    {
        var staff = await db.Staff.Where(s => !s.IsLocalOnly).ToDictionaryAsync(s => s.Id, cancellationToken);
        foreach (var incoming in data.Staff)
        {
            if (!staff.TryGetValue(incoming.Id, out var member))
                db.Staff.Add(member = new StaffMember { Id = incoming.Id });
            member.FullName = incoming.FullName;
            member.Role = incoming.Role;
            member.IsActive = incoming.IsActive;
        }
        foreach (var gone in staff.Values.Where(s => data.Staff.All(i => i.Id != s.Id)))
            gone.IsActive = false;
    }
}

public sealed class ReferenceDataSync(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliverySystemOptions> options,
    DeliverySystemLink link,
    ILogger<ReferenceDataSync> logger) : BackgroundService
{
    // Woken early when the delivery system comes back, rather than waiting out the interval:
    // after an outage the menu, prices, tax rate and staff list (a deactivated cashier above
    // all) should catch up at once, not minutes later.
    private readonly SyncSignal _syncNow = new();
    private DeliverySystemLinkState _lastLinkState = link.Current.State;
    private volatile bool _lastSyncFailed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReferenceDataRefreshMinutes));
        link.Changed += OnLinkChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await SyncAsync(stoppingToken);
                await _syncNow.WaitAsync(interval, stoppingToken);
            }
        }
        finally
        {
            link.Changed -= OnLinkChanged;
        }
    }

    private async Task SyncAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var data = await scope.ServiceProvider.GetRequiredService<DeliverySystemApi>().GetReferenceDataAsync(stoppingToken);
            await scope.ServiceProvider.GetRequiredService<ReferenceDataApplier>().ApplyAsync(data, DateTime.UtcNow, stoppingToken);

            if (_lastSyncFailed)
                logger.LogInformation("Menu and settings are syncing from the delivery system again");
            _lastSyncFailed = false;
        }
        catch (DeliverySystemUnavailableException) when (!stoppingToken.IsCancellationRequested)
        {
            // Expected while the internet is down; the till keeps selling from its copy.
            if (!_lastSyncFailed)
                logger.LogWarning("Cannot reach the delivery system; using the menu and settings already on this till");
            _lastSyncFailed = true;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Syncing the menu and settings from the delivery system failed");
            _lastSyncFailed = true;
        }
    }

    // Back online after being offline, or online at last after a sync that failed: sync now.
    // Not on the first connection after a sync that worked, which would only repeat it.
    private void OnLinkChanged(DeliverySystemLinkStatus status)
    {
        var previous = _lastLinkState;
        _lastLinkState = status.State;
        if (status.State == DeliverySystemLinkState.Online && (previous == DeliverySystemLinkState.Offline || _lastSyncFailed))
            _syncNow.Notify();
    }

    private sealed class SyncSignal : WakeSignal;
}
