using Microsoft.EntityFrameworkCore;
using Npgsql;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Orders;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Costing;
using OtantikPos.Node.Infrastructure.Identity;
using OtantikPos.Node.Infrastructure.Messaging;
using OtantikPos.Node.Infrastructure.Printing;

namespace OtantikPos.Node.Infrastructure.Persistence;

// The restaurant machine's database. It stores the shared Order exactly as the delivery system
// defines it, a copy of the delivery system's menu, and the POS's own data: inventory, the
// outbox, print jobs and staff PINs.
public sealed class NodeDbContext(DbContextOptions<NodeDbContext> options, OutboxSignal outboxSignal)
    : DbContext(options), IUnitOfWork
{
    // One schema per area. Nothing in pos has a foreign key into inventory or the other way
    // round: Inventory knows orders only through events, and recipes only by catalog id.
    public const string PosSchema = "pos";
    public const string CatalogSchema = "catalog";
    public const string InventorySchema = "inventory";
    public const string MessagingSchema = "messaging";
    public const string IdentitySchema = "identity";

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderAuditEntry> OrderAudit => Set<OrderAuditEntry>();

    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<MenuItemVariant> MenuItemVariants => Set<MenuItemVariant>();
    public DbSet<AddOn> AddOns => Set<AddOn>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<SubCategory> SubCategories => Set<SubCategory>();
    public DbSet<RestaurantSettingsCopy> Settings => Set<RestaurantSettingsCopy>();

    public DbSet<RawMaterial> RawMaterials => Set<RawMaterial>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<CostingSettings> CostingSettings => Set<CostingSettings>();
    public DbSet<SharedCost> SharedCosts => Set<SharedCost>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<CashReceived> CashReceived => Set<CashReceived>();
    public DbSet<SyncState> SyncState => Set<SyncState>();

    public DbSet<StaffMember> Staff => Set<StaffMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NodeDbContext).Assembly);

        // The inventory module's rich aggregates (BuildingBlocks): ids generated in the domain,
        // domain events never stored, and optimistic concurrency on PostgreSQL's xmin.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            var entity = modelBuilder.Entity(entityType.ClrType);
            entity.Property(nameof(Entity.Id)).ValueGeneratedNever();

            if (typeof(AggregateRoot).IsAssignableFrom(entityType.ClrType))
            {
                entity.Ignore(nameof(AggregateRoot.DomainEvents));
                entity.Property<uint>("Version").IsRowVersion();
            }
        }
    }

    // Money, with the delivery system's precision. Stock quantities override this; see
    // PropertyBuilderExtensions.IsQuantity.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<decimal>().HavePrecision(10, 2);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        TouchOrdersWithChangedItems();
        TouchRootsOfChangedChildren();

        var outboxGrew = ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added);

        int written;
        try
        {
            written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (Exception ex) when (ToConflict(ex) is { } conflict)
        {
            throw conflict;
        }

        if (outboxGrew)
            outboxSignal.Notify();

        return written;
    }

    // The xmin check guards a row, but an order spans several. Voiding one item writes only that
    // item's row, so on its own it would not collide with another till taking payment for the
    // same order at the same moment. Marking the order modified whenever one of its items
    // changes puts the order's row, and its check, into every save that touches the order.
    private void TouchOrdersWithChangedItems()
    {
        var changedOrderIds = ChangeTracker.Entries<OrderItem>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Entity.Order ?? Orders.Local.FirstOrDefault(o => o.Id == e.Entity.OrderId))
            .OfType<Order>()
            .ToHashSet();

        foreach (var order in changedOrderIds)
        {
            var entry = Entry(order);
            if (entry.State == EntityState.Unchanged)
                entry.State = EntityState.Modified;
        }
    }

    // The same for the inventory aggregates: a recipe's row is touched when its ingredients
    // change. Only the owning aggregate, recognised by its collection navigation; a
    // RecipeIngredient also points at a RawMaterial, which it does not belong to.
    private void TouchRootsOfChangedChildren()
    {
        var changedChildren = ChangeTracker.Entries()
            .Where(e => e.Entity is Entity and not AggregateRoot
                && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var child in changedChildren)
        {
            foreach (var foreignKey in child.Metadata.GetForeignKeys()
                         .Where(fk => fk.PrincipalToDependent is not null
                             && typeof(AggregateRoot).IsAssignableFrom(fk.PrincipalEntityType.ClrType)))
            {
                var property = child.Property(foreignKey.Properties[0].Name);
                var rootId = (Guid?)(child.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue);
                var root = ChangeTracker.Entries<AggregateRoot>().FirstOrDefault(r => r.Entity.Id == rootId);
                if (root is { State: EntityState.Unchanged })
                    root.State = EntityState.Modified;
            }
        }
    }

    // The two save failures a caller can act on, translated so no layer above this one ever
    // handles an EF Core or Npgsql type.
    private static ConflictException? ToConflict(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException =>
            new ConflictException("Someone else changed this at the same time. Reload it and try again.", ex),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
            new ConflictException("This conflicts with a record that already exists.", ex),
        _ => null,
    };
}
