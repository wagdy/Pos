using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Auditing;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Customers;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Inventory.Application;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Suppliers;
using OtantikPos.Inventory.Domain.Treasury;
using OtantikPos.Ordering.Application;
using OtantikPos.Ordering.Application.Catalog;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Application.Tests;

// Both POS modules wired as the API will wire them, with every port faked in memory. Commands
// go through MediatR exactly as in production. Events are delivered only when a test calls
// DeliverEventsAsync, the way the outbox delivers them after a commit.
public sealed class TestPos
{
    public const int Burger = 1, Shawarma = 10, KiloTray = 101, HalfTray = 102, Cheese = 7;

    private readonly ServiceProvider _services;

    public MutableUser User { get; } = new() { UserId = "cashier-1", Role = UserRole.Cashier };
    public InMemoryOrders Orders { get; } = new();
    public FakeDeliverySystem Cloud { get; } = new();
    public RecordingSink Sink { get; } = new();
    public InventoryStore Inventory { get; } = new();
    public HrStore Hr { get; } = new();

    // Names and dates for the inventory's records: the business day can be set a day behind the
    // calendar, as it is after midnight.
    public NamesFromTheTestMenu Dates { get; } = new();
    public FixedPricing Pricing { get; } = new(14m);

    public TestPos()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddOrderingApplication();
        services.AddInventoryApplication();

        services.AddSingleton<ICurrentUser>(User);
        services.AddSingleton<IOrderStore>(Orders);
        services.AddSingleton<ICatalog>(new Menu());
        services.AddSingleton<IPricingSettings>(Pricing);
        services.AddSingleton<OtantikPos.Inventory.Application.Treasury.ITaxRate>(Pricing);
        services.AddSingleton<IBusinessCalendar>(new TestCalendar());
        services.AddSingleton<ICustomerDirectory>(Cloud);
        services.AddSingleton<ILoyaltyGateway>(Cloud);
        services.AddSingleton<IAuditLog>(Sink);
        services.AddSingleton<IEventOutbox>(Sink);
        services.AddSingleton<ITillNotifier>(Sink);
        services.AddSingleton<IKitchenPrintQueue>(Sink);
        services.AddSingleton<IReceiptPrintQueue>(Sink);
        services.AddSingleton<ICashReceived>(Sink);
        services.AddSingleton<IUnitOfWork>(new UnitOfWork(this));
        services.AddSingleton<IRawMaterialRepository>(Inventory);
        services.AddSingleton<IRecipeRepository>(Inventory.Recipes);
        services.AddSingleton<IRecipeCategoryRepository>(Inventory.RecipeCategories);
        services.AddSingleton<IStockMovementRepository>(Inventory.Movements);
        services.AddSingleton<IStockCountRepository>(Inventory.Counts);
        services.AddSingleton<IPurchaseInvoiceRepository>(Inventory.Invoices);
        services.AddSingleton<IPurchaseAttachmentStore>(Inventory.Invoices);
        services.AddSingleton<ISupplierRepository>(Inventory.Suppliers);
        services.AddSingleton<IPurchaseCategoryRepository>(Inventory.PurchaseCategories);
        services.AddSingleton<IExpenseItemRepository>(Inventory.ExpenseItems);
        services.AddSingleton<IApprovalRequestRepository>(Inventory.ApprovalRequests);
        services.AddSingleton<ISafeTransactionRepository>(Inventory.Safe);
        services.AddSingleton<ISupplierLedgerRepository>(Inventory.SupplierLedger);
        services.AddSingleton<OtantikPos.Inventory.Application.Approvals.IRecordDescriptions>(Dates);
        services.AddSingleton<IPrepRecipeRepository>(Inventory.PrepRecipes);
        services.AddSingleton<IProductionOrderRepository>(Inventory.Production);
        services.AddSingleton<OtantikPos.Inventory.Application.RawMaterials.IMaterialUnits>(Inventory.Units);
        services.AddSingleton<OtantikPos.Inventory.Application.Categories.IItemConversions>(new NoItemConversions());
        services.AddSingleton<OtantikPos.Hr.Domain.IEmployeeRepository>(Hr.Employees);
        services.AddSingleton<OtantikPos.Hr.Domain.IEmployeeIdPhotoStore>(Hr.Employees);
        services.AddSingleton<OtantikPos.Hr.Domain.IAttendanceDeviceRepository>(Hr.Devices);
        services.AddSingleton<OtantikPos.Hr.Domain.IAttendanceLogRepository>(Hr.Punches);
        services.AddSingleton<OtantikPos.Hr.Domain.IDayMarkRepository>(Hr.Marks);
        services.AddSingleton<OtantikPos.Hr.Domain.IAdministrativePenaltyRepository>(Hr.Penalties);
        services.AddSingleton<OtantikPos.Hr.Domain.IPublicHolidayRepository>(Hr.Holidays);
        services.AddSingleton<OtantikPos.Hr.Domain.IDeviceCommandRepository>(Hr.Commands);
        services.AddSingleton<OtantikPos.Hr.Domain.IDeviceUserRepository>(Hr.DeviceUsers);
        services.AddSingleton<OtantikPos.Hr.Domain.IDepartmentRepository>(Hr.Departments);
        services.AddSingleton<OtantikPos.Hr.Domain.IJobRoleRepository>(Hr.Roles);
        services.AddSingleton<OtantikPos.Hr.Domain.IDailyRecordRepository>(Hr.Records);
        services.AddSingleton<OtantikPos.Hr.Domain.IPayrollAdjustmentRepository>(Hr.Adjustments);
        services.AddSingleton<OtantikPos.Hr.Domain.IPayrollSlipRepository>(Hr.Slips);
        services.AddSingleton<OtantikPos.Hr.Domain.IAdvanceAllocationRepository>(Hr.Allocations);
        services.AddSingleton<OtantikPos.Hr.Domain.IHrAdvanceRepository>(Hr.HrAdvances);
        services.AddSingleton<OtantikPos.Hr.Domain.IAssetCategoryRepository>(Hr.AssetCategories);
        services.AddSingleton<OtantikPos.Hr.Domain.IStaffHrAccessRepository>(Hr.StaffHrAccess);
        services.AddSingleton<OtantikPos.Hr.Domain.IAssetItemRepository>(Hr.Assets);
        services.AddSingleton<OtantikPos.Hr.Domain.IAssetTransactionRepository>(Hr.AssetVouchers);
        services.AddSingleton<OtantikPos.Inventory.Domain.Waste.IBreakageEntryRepository>(Hr.Breakage);
        services.AddSingleton<OtantikPos.Inventory.Application.Hr.IPinHasher>(Hr.Pins);
        services.AddSingleton<OtantikPos.Inventory.Application.Hr.IAttendanceSettings>(Hr.Settings);
        services.AddSingleton<OtantikPos.Inventory.Application.Hr.IAttendanceNotifier>(Hr.Notifications);
        services.AddSingleton<OtantikPos.Inventory.Application.Hr.IAttendanceDeviceLink>(Hr.Link);

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public TestPos As(string userId, UserRole role)
    {
        User.UserId = userId;
        User.Role = role;
        return this;
    }

    // One request per scope, like one HTTP request. Whatever the request did not save is
    // dropped, the way a failed request's DbContext is.
    public async Task<T> Send<T>(IRequest<T> request)
    {
        using var scope = _services.CreateScope();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        }
        finally
        {
            Discard();
        }
    }

    public async Task Send(IRequest request)
    {
        using var scope = _services.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        }
        finally
        {
            Discard();
        }
    }

    // The outbox: everything committed so far, delivered once each.
    public async Task DeliverEventsAsync()
    {
        while (Sink.TryTakeCommitted(out var orderingEvent))
            await Publish(orderingEvent);
    }

    // Every event ever delivered, delivered again: what a crashed outbox processor does.
    public async Task RedeliverAllAsync()
    {
        foreach (var orderingEvent in Sink.Delivered.ToList())
            await Publish(orderingEvent, record: false);
    }

    private async Task Publish(OrderingEvent orderingEvent, bool record = true)
    {
        using var scope = _services.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(orderingEvent);
            if (record)
                Sink.Delivered.Add(orderingEvent);
        }
        finally
        {
            Discard();
        }
    }

    private void Discard()
    {
        Orders.Discard();
        Sink.Discard();
    }

    private sealed class UnitOfWork(TestPos pos) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            pos.Orders.Commit();
            pos.Sink.Commit();
            return Task.FromResult(1);
        }
    }
}

public sealed class MutableUser : ICurrentUser
{
    public string UserId { get; set; } = string.Empty;
    public UserRole Role { get; set; }
}

// Orders are stored as the shared JSON and read back as fresh copies, so every test also
// round-trips the sync payload, and a command that throws leaves the stored order as it was.
public sealed class InMemoryOrders : IOrderStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, string> _saved = new();
    private readonly List<Order> _tracked = new();
    private int _nextOrderId = 1;
    private int _nextItemId = 1;

    public Order Saved(Guid publicId) => Read(_saved[publicId]);

    public Task<Order?> GetAsync(Guid orderPublicId, CancellationToken cancellationToken)
    {
        if (!_saved.TryGetValue(orderPublicId, out var json))
            return Task.FromResult<Order?>(null);

        var order = Read(json);
        _tracked.Add(order);
        return Task.FromResult<Order?>(order);
    }

    public Task<IReadOnlyList<Order>> GetOpenAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>(_saved.Values.Select(Read).Where(o => !o.IsDeleted && OrderRules.AwaitsPayment(o)).OrderBy(o => o.CreatedAt).ToList());

    public Task<IReadOnlyList<Order>> GetCreatedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>(_saved.Values.Select(Read).Where(o => !o.IsDeleted && o.CreatedAt >= sinceUtc).OrderByDescending(o => o.CreatedAt).ToList());

    public Task<IReadOnlyList<Order>> GetCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>(_saved.Values.Select(Read)
            .Where(o => !o.IsDeleted && o.CreatedAt >= fromUtc && o.CreatedAt < toUtc)
            .OrderByDescending(o => o.CreatedAt)
            .ToList());

    public void Add(Order order) => _tracked.Add(order);

    // What SaveChanges does: assign this database's ids, then store.
    public void Commit()
    {
        foreach (var order in _tracked)
        {
            if (order.Id == 0)
                order.Id = _nextOrderId++;
            foreach (var item in order.OrderItems.Where(i => i.Id == 0))
                item.Id = _nextItemId++;
            _saved[order.PublicId] = JsonSerializer.Serialize(order, Json);
        }
        _tracked.Clear();
    }

    public void Discard() => _tracked.Clear();

    private static Order Read(string json) => JsonSerializer.Deserialize<Order>(json, Json)!;
}

// The delivery system, as the till sees it: a customer directory and a loyalty ledger.
public sealed class FakeDeliverySystem : ICustomerDirectory, ILoyaltyGateway
{
    public bool Offline { get; set; }
    public bool RefuseRedemption { get; set; }
    public Dictionary<string, CustomerProfile> Customers { get; } = new();
    public Dictionary<string, int> Balances { get; } = new();
    public Dictionary<Guid, (string UserId, int Points)> Redemptions { get; } = new();

    // By refund id, as the delivery system keeps them: each applied once.
    public Dictionary<Guid, (string UserId, Guid OrderPublicId, int Points, decimal RefundedAmount)> Refunds { get; } = new();

    public void Register(string userId, string name, string phone, int points)
    {
        Customers[phone] = new CustomerProfile(userId, name, phone, points);
        Balances[userId] = points;
    }

    public Task<CustomerProfile?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        return Task.FromResult(Customers.GetValueOrDefault(phoneNumber));
    }

    public Task<int> GetBalanceAsync(string customerUserId, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        return Task.FromResult(Balances.GetValueOrDefault(customerUserId));
    }

    public Task RedeemAsync(string customerUserId, int points, Guid orderPublicId, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        if (Redemptions.ContainsKey(orderPublicId))
            return Task.CompletedTask;
        if (RefuseRedemption || Balances.GetValueOrDefault(customerUserId) < points)
            throw new ConflictException("The customer's points were spent elsewhere.");

        Balances[customerUserId] -= points;
        Redemptions[orderPublicId] = (customerUserId, points);
        return Task.CompletedTask;
    }

    public Task RefundAsync(string customerUserId, Guid orderPublicId, Guid refundId, int points, decimal refundedAmount, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        if (Refunds.TryAdd(refundId, (customerUserId, orderPublicId, points, refundedAmount)))
            Balances[customerUserId] = Balances.GetValueOrDefault(customerUserId) + points;
        return Task.CompletedTask;
    }

    private void ThrowIfOffline()
    {
        if (Offline)
            throw new DeliverySystemUnavailableException("The delivery system cannot be reached.");
    }
}

// Audit log, outbox, till pushes and both print queues, recorded.
public sealed class RecordingSink : IAuditLog, IEventOutbox, ITillNotifier, IKitchenPrintQueue, IReceiptPrintQueue, ICashReceived
{
    private readonly List<OrderAuditEntry> _pendingAudit = new();
    private readonly List<OrderingEvent> _pendingEvents = new();
    private readonly Queue<OrderingEvent> _committedEvents = new();

    public List<OrderAuditEntry> Audit { get; } = new();
    public List<OrderingEvent> Delivered { get; } = new();
    public List<KitchenTicket> KitchenTickets { get; } = new();
    public List<Guid> Receipts { get; } = new();
    public int TillPushes { get; private set; }

    public void Add(OrderAuditEntry entry) => _pendingAudit.Add(entry);

    public void Add(OrderingEvent orderingEvent) => _pendingEvents.Add(orderingEvent);

    public Task OrderChangedAsync(Order order, CancellationToken cancellationToken)
    {
        TillPushes++;
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(KitchenTicket ticket, CancellationToken cancellationToken)
    {
        if (KitchenTickets.All(t => t.TicketId != ticket.TicketId))
            KitchenTickets.Add(ticket);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(Order order, string printedByUserId, CancellationToken cancellationToken)
    {
        Receipts.Add(order.PublicId);
        return Task.CompletedTask;
    }

    // Recorded at once here; at the till it is saved with the payment.
    public Dictionary<Guid, decimal> CashReceived { get; } = new();

    public void Record(Guid orderPublicId, decimal amount) => CashReceived[orderPublicId] = amount;

    public IEnumerable<T> Committed<T>() where T : OrderingEvent => _committedEvents.OfType<T>().Concat(Delivered.OfType<T>());

    public bool TryTakeCommitted(out OrderingEvent orderingEvent) => _committedEvents.TryDequeue(out orderingEvent!);

    public void Commit()
    {
        Audit.AddRange(_pendingAudit);
        foreach (var e in _pendingEvents)
            _committedEvents.Enqueue(e);
        Discard();
    }

    public void Discard()
    {
        _pendingAudit.Clear();
        _pendingEvents.Clear();
    }
}

// Today is the two hours around the test run, so a test can never straddle midnight. Any other
// day is empty.
public sealed class TestCalendar : IBusinessCalendar
{
    public static readonly DateOnly Today = new(2026, 10, 3);

    private readonly DateTime _start = DateTime.UtcNow.AddHours(-1);

    public DateOnly BusinessDateOf(DateTime utc) => Today;

    public (DateTime FromUtc, DateTime ToUtc) Bounds(DateOnly businessDate) =>
        businessDate == Today ? (_start, _start.AddHours(2)) : (DateTime.MinValue, DateTime.MinValue);
}

// Burger 120 with Cheese +15; Shawarma in a Kilo tray (400) or Half tray (220).
internal sealed class Menu : ICatalog
{
    private static readonly AddOn CheeseAddOn = new() { Id = TestPos.Cheese, Name = "Cheese", Price = 15 };

    public Task<MenuItem?> GetMenuItemAsync(int menuItemId, CancellationToken cancellationToken) => Task.FromResult<MenuItem?>(menuItemId switch
    {
        TestPos.Burger => new MenuItem
        {
            Id = TestPos.Burger, Name = "Burger", Price = 120,
            MenuItemAddOns = [new MenuItemAddOn { MenuItemId = TestPos.Burger, AddOnId = TestPos.Cheese, AddOn = CheeseAddOn }],
        },
        TestPos.Shawarma => new MenuItem
        {
            Id = TestPos.Shawarma, Name = "Shawarma", Price = 0,
            Variants =
            [
                new MenuItemVariant { Id = TestPos.KiloTray, Name = "Kilo tray", Price = 400 },
                new MenuItemVariant { Id = TestPos.HalfTray, Name = "Half tray", Price = 220 },
            ],
        },
        _ => null,
    });

    public async Task<TillMenu> GetMenuAsync(CancellationToken cancellationToken) =>
        new([], [], [(await GetMenuItemAsync(TestPos.Burger, cancellationToken))!, (await GetMenuItemAsync(TestPos.Shawarma, cancellationToken))!]);
}

// The delivery system's synced pricing settings: 14% tax, and points at the default rate
// unless a test changes it. The treasury takes tax off takings at the same rate.
public sealed class FixedPricing(decimal taxPercentage) : IPricingSettings, OtantikPos.Inventory.Application.Treasury.ITaxRate
{
    public decimal TaxPercentage { get; set; } = taxPercentage;

    public decimal RedemptionValuePer100Points { get; set; } = LoyaltyRedemption.DefaultValuePer100Points;

    public Task<decimal> GetTaxPercentageAsync(CancellationToken cancellationToken) => Task.FromResult(TaxPercentage);

    public Task<decimal> PercentAsync(CancellationToken cancellationToken) => Task.FromResult(TaxPercentage);

    public Task<decimal> GetRedemptionValuePer100PointsAsync(CancellationToken cancellationToken) => Task.FromResult(RedemptionValuePer100Points);
}

public sealed class InventoryStore : IRawMaterialRepository
{
    private readonly List<RawMaterial> _materials = new();

    public RecipeStore Recipes { get; } = new();
    public RecipeCategoryStore RecipeCategories { get; } = new();
    public MovementStore Movements { get; } = new();
    public CountStore Counts { get; } = new();
    public InvoiceStore Invoices { get; } = new();
    public SupplierStore Suppliers { get; } = new();
    public ExpenseItemStore ExpenseItems { get; } = new();
    public ApprovalRequestStore ApprovalRequests { get; } = new();
    public SafeStore Safe { get; } = new();
    public SupplierLedgerStore SupplierLedger { get; } = new();
    public PurchaseCategoryStore PurchaseCategories => _purchaseCategories ??= new(this, ExpenseItems);
    private PurchaseCategoryStore? _purchaseCategories;
    public PrepRecipeStore PrepRecipes { get; } = new();
    public ProductionStore Production { get; } = new();
    public MaterialUnitStore Units => _units ??= new(Movements);
    private MaterialUnitStore? _units;

    public RawMaterial Stock(string name, UnitOfMeasure unit, decimal onHand)
    {
        var material = new RawMaterial(name, unit);
        material.ReceivePurchase(onHand, Guid.NewGuid());
        _materials.Add(material);
        return material;
    }

    public Task<RawMaterial?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_materials.SingleOrDefault(m => m.Id == id));
    public void Add(RawMaterial aggregate) => _materials.Add(aggregate);
    public Task<IReadOnlyList<RawMaterial>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RawMaterial>>(_materials.ToList());
    public Task<IReadOnlyList<RawMaterial>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RawMaterial>>(_materials.Where(m => ids.Contains(m.Id)).ToList());
    public Task<IReadOnlyList<RawMaterial>> GetNeedingReorderAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RawMaterial>>(_materials.Where(m => m.NeedsReorder).ToList());
}

// What records a material, as far as these stores keep it: its ledger. Restating it is the
// database's, and is tested against one (NodeTests).
public sealed class MaterialUnitStore(MovementStore movements) : OtantikPos.Inventory.Application.RawMaterials.IMaterialUnits
{
    public Task<OtantikPos.Inventory.Application.RawMaterials.MaterialUsage> UsageAsync(
        RawMaterial material, Guid? exceptRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(new OtantikPos.Inventory.Application.RawMaterials.MaterialUsage(movements.All.Count(m => m.RawMaterialId == material.Id), 0, 0, 0, 0, 0, 0));

    public Task RestateAndSaveAsync(RawMaterial material, decimal oneNewIsOld, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Restated in the database: see NodeTests.");
}

// Converting raw materials to expense items reads and restates the database's records: tested
// against one (NodeTests). Here nothing stops it and nothing is restated.
public sealed class NoItemConversions : OtantikPos.Inventory.Application.Categories.IItemConversions
{
    public Task<IReadOnlyList<string>> ExpenseBlockersAsync(
        IReadOnlyList<RawMaterial> materials, IReadOnlyDictionary<Guid, Guid> namesakes, Guid? exceptRequestId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<OtantikPos.Inventory.Application.Categories.ExpenseRestatement> RestatementAsync(
        IReadOnlyList<Guid> materialIds, CancellationToken cancellationToken) =>
        Task.FromResult(new OtantikPos.Inventory.Application.Categories.ExpenseRestatement(0, 0, 0, 0));

    public Task<int> PendingNamingAsync(IReadOnlyCollection<Guid> ids, Guid? exceptRequestId, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task RestateAndSaveAsync(IReadOnlyList<(Guid MaterialId, Guid ExpenseItemId)> pairs, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Restated in the database: see NodeTests.");
}

public sealed class RecipeStore : IRecipeRepository
{
    private readonly List<Recipe> _recipes = new();

    public Task<Recipe?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_recipes.SingleOrDefault(r => r.Id == id));
    public void Add(Recipe aggregate) => _recipes.Add(aggregate);
    public Task<int> CountUsingAsync(Guid rawMaterialId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_recipes.Count(r => !r.IsDeleted && r.Ingredients.Any(i => i.RawMaterialId == rawMaterialId)));
    // The ones sales go by: not deleted, and linked to the menu.
    private IEnumerable<Recipe> Active => _recipes.Where(r => !r.IsDeleted && r.Target is not null);
    public Task<Recipe?> GetByTargetAsync(RecipeTarget target, CancellationToken cancellationToken = default) => Task.FromResult(Active.SingleOrDefault(r => r.Target == target));
    public Task<IReadOnlyList<Recipe>> GetByTargetsAsync(IReadOnlyCollection<RecipeTarget> targets, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Recipe>>(Active.Where(r => targets.Contains(r.Target!.Value)).ToList());
    public Task<IReadOnlyList<Recipe>> GetInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Recipe>>(_recipes.Where(r => r.RecipeCategoryId == categoryId).ToList());
    public Task<IReadOnlyDictionary<Guid, int>> CountByCategoryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, int>>(_recipes.Where(r => !r.IsDeleted && r.RecipeCategoryId != null).GroupBy(r => r.RecipeCategoryId!.Value).ToDictionary(g => g.Key, g => g.Count()));
}

public sealed class MovementStore : IStockMovementRepository
{
    public List<StockMovement> All { get; } = new();

    public Task<StockMovement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(All.SingleOrDefault(m => m.Id == id));
    public void Add(StockMovement aggregate) => All.Add(aggregate);
    public Task<bool> ExistsAsync(Guid sourceId, StockMovementReason reason, CancellationToken cancellationToken = default) => Task.FromResult(All.Any(m => m.SourceId == sourceId && m.Reason == reason));
    public Task<IReadOnlyList<StockMovement>> GetBySourceAsync(Guid sourceId, StockMovementReason reason, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StockMovement>>(All.Where(m => m.SourceId == sourceId && m.Reason == reason).ToList());
}

public sealed class CountStore : IStockCountRepository
{
    private readonly List<StockCount> _counts = new();

    public Task<StockCount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_counts.SingleOrDefault(c => c.Id == id));
    public void Add(StockCount aggregate) => _counts.Add(aggregate);
    public void Remove(StockCount draft) => _counts.Remove(draft);
    public Task<StockCount?> GetDraftAsync(CancellationToken cancellationToken = default) => Task.FromResult(_counts.FirstOrDefault(c => c.Status == StockCountStatus.Draft));
    public Task<IReadOnlyList<StockCount>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StockCount>>(_counts.ToList());
}

public sealed class InvoiceStore : IPurchaseInvoiceRepository, IPurchaseAttachmentStore
{
    private readonly List<PurchaseInvoice> _invoices = new();
    private readonly Dictionary<Guid, byte[]> _attachments = new();

    public Task<PurchaseInvoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_invoices.SingleOrDefault(i => i.Id == id));
    public void Add(PurchaseInvoice aggregate) => _invoices.Add(aggregate);
    public void Remove(PurchaseInvoice draft) => _invoices.Remove(draft);
    public Task<IReadOnlyList<PurchaseInvoice>> GetByStatusAsync(PurchaseInvoiceStatus status, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PurchaseInvoice>>(_invoices.Where(i => i.Status == status).ToList());
    public Task<IReadOnlyDictionary<Guid, SupplierUse>> GetSupplierUseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, SupplierUse>>(_invoices.Where(i => i.SupplierId != null).GroupBy(i => i.SupplierId!.Value)
            .ToDictionary(g => g.Key, g => new SupplierUse(
                g.Count(), g.Where(i => i.Status == PurchaseInvoiceStatus.Posted).Sum(i => i.Total), g.Max(i => i.PostedAtUtc))));
    public Task<bool> AnyForSupplierAsync(Guid supplierId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_invoices.Any(i => i.SupplierId == supplierId));
    public Task<IReadOnlyDictionary<Guid, ExpenseItemUse>> GetExpenseItemUseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ExpenseItemUse>>(_invoices
            .SelectMany(i => i.Lines.Where(l => l.ExpenseItemId != null).Select(l => (Invoice: i, Line: l)))
            .GroupBy(x => x.Line.ExpenseItemId!.Value)
            .ToDictionary(g => g.Key, g => new ExpenseItemUse(
                g.Count(), g.Where(x => x.Invoice.Status == PurchaseInvoiceStatus.Posted).Sum(x => x.Line.Cost ?? 0), g.Max(x => x.Invoice.PostedAtUtc))));
    public Task<bool> AnyForExpenseItemAsync(Guid expenseItemId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_invoices.Any(i => i.Lines.Any(l => l.ExpenseItemId == expenseItemId)));
    public void Put(Guid invoiceId, byte[] content) => _attachments[invoiceId] = content;
    public Task<byte[]?> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default) => Task.FromResult(_attachments.GetValueOrDefault(invoiceId));
}

public sealed class SupplierStore : ISupplierRepository
{
    private readonly List<Supplier> _suppliers = new();

    public Task<Supplier?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_suppliers.SingleOrDefault(s => s.Id == id));
    public void Add(Supplier aggregate) => _suppliers.Add(aggregate);
    public void Remove(Supplier supplier) => _suppliers.Remove(supplier);
    public Task<IReadOnlyList<Supplier>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Supplier>>(_suppliers.ToList());
    public Task<Supplier?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_suppliers.SingleOrDefault(s => s.NormalizedName == normalizedName));
}

public sealed class PrepRecipeStore : IPrepRecipeRepository
{
    private readonly List<PrepRecipe> _recipes = new();

    public Task<PrepRecipe?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_recipes.SingleOrDefault(r => r.Id == id));
    public void Add(PrepRecipe aggregate) => _recipes.Add(aggregate);
    public Task<IReadOnlyList<PrepRecipe>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PrepRecipe>>(_recipes.ToList());
    public Task<IReadOnlyList<PrepRecipe>> GetInCategoryAsync(Guid recipeCategoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrepRecipe>>(_recipes.Where(r => r.RecipeCategoryId == recipeCategoryId).ToList());
}

public sealed class ProductionStore : IProductionOrderRepository
{
    private readonly List<ProductionOrder> _orders = new();

    public Task<ProductionOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_orders.SingleOrDefault(o => o.Id == id));
    public void Add(ProductionOrder aggregate) => _orders.Add(aggregate);
    public Task<IReadOnlyList<ProductionOrder>> GetRecentAsync(Guid? prepItemId, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProductionOrder>>(_orders.Where(o => prepItemId == null || o.PrepItemId == prepItemId)
            .OrderByDescending(o => o.ProducedAtUtc).Take(take).ToList());
}

public sealed class RecipeCategoryStore : IRecipeCategoryRepository
{
    private readonly List<RecipeCategory> _categories = new();

    public Task<RecipeCategory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_categories.SingleOrDefault(c => c.Id == id));
    public void Add(RecipeCategory aggregate) => _categories.Add(aggregate);
    public void Remove(RecipeCategory category) => _categories.Remove(category);
    public Task<IReadOnlyList<RecipeCategory>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RecipeCategory>>(_categories.ToList());
    public Task<RecipeCategory?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_categories.SingleOrDefault(c => c.NormalizedName == normalizedName));
}

public sealed class PurchaseCategoryStore : IPurchaseCategoryRepository
{
    private readonly List<PurchaseCategory> _categories = new();
    private readonly InventoryStore _materials;
    private readonly ExpenseItemStore _expenses;

    public PurchaseCategoryStore(InventoryStore materials, ExpenseItemStore expenses) => (_materials, _expenses) = (materials, expenses);

    public Task<PurchaseCategory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_categories.SingleOrDefault(c => c.Id == id));
    public void Add(PurchaseCategory aggregate) => _categories.Add(aggregate);
    public void Remove(PurchaseCategory category) => _categories.Remove(category);
    public Task<IReadOnlyList<PurchaseCategory>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PurchaseCategory>>(_categories.ToList());
    public Task<PurchaseCategory?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_categories.SingleOrDefault(c => c.NormalizedName == normalizedName));
    public async Task<IReadOnlyDictionary<Guid, PurchaseCategoryUse>> GetUseAsync(CancellationToken cancellationToken = default)
    {
        var materials = (await _materials.GetAllAsync(cancellationToken)).GroupBy(m => m.CategoryId).ToDictionary(g => g.Key, g => g.ToList());
        var expenses = (await _expenses.GetAllAsync(cancellationToken)).GroupBy(e => e.CategoryId).ToDictionary(g => g.Key, g => g.ToList());
        return materials.Keys.Union(expenses.Keys)
            .ToDictionary(id => id, id => new PurchaseCategoryUse(
                materials.GetValueOrDefault(id)?.Count ?? 0, expenses.GetValueOrDefault(id)?.Count ?? 0,
                materials.GetValueOrDefault(id)?.Count(m => !m.IsArchived) ?? 0, expenses.GetValueOrDefault(id)?.Count(e => !e.IsArchived) ?? 0));
    }
}

public sealed class ExpenseItemStore : IExpenseItemRepository
{
    private readonly List<ExpenseItem> _items = new();

    public Task<ExpenseItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_items.SingleOrDefault(e => e.Id == id));
    public void Add(ExpenseItem aggregate) => _items.Add(aggregate);
    public void Remove(ExpenseItem item) => _items.Remove(item);
    public Task<IReadOnlyList<ExpenseItem>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExpenseItem>>(_items.ToList());
    public Task<IReadOnlyList<ExpenseItem>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExpenseItem>>(_items.Where(e => ids.Contains(e.Id)).ToList());
    public Task<IReadOnlyList<ExpenseItem>> GetInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExpenseItem>>(_items.Where(e => e.CategoryId == categoryId).ToList());
    public Task<ExpenseItem?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.SingleOrDefault(e => e.NormalizedName == normalizedName));
}

public sealed class SafeStore : ISafeTransactionRepository
{
    private readonly List<SafeTransaction> _entries = new();

    public Task<SafeTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_entries.SingleOrDefault(t => t.Id == id));
    public void Add(SafeTransaction aggregate) => _entries.Add(aggregate);
    public Task<decimal> SafeBalanceAsync(DateOnly? through = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.Where(t => t.Fund == CashFund.Safe && (through == null || t.BusinessDate <= through)).Sum(t => t.Amount));
    public Task<SafeTransaction?> GetTakingsAsync(DateOnly revenueDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.FirstOrDefault(t => t.Kind == SafeTransactionKind.RevenueTransfer && t.RevenueDate == revenueDate && !t.IsReversal && !t.IsVoided));
    public Task<decimal> PaidOutOfTakingsAsync(DateOnly revenueDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(-_entries.Where(t => t.Fund == CashFund.DayRevenue && t.RevenueDate == revenueDate).Sum(t => t.Amount));
    public Task<SafeTransaction?> GetPaymentOfInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.SingleOrDefault(t => t.PurchaseInvoiceId == invoiceId && t.Kind == SafeTransactionKind.InvoicePayment && !t.IsReversal && !t.IsVoided));
    public Task<IReadOnlySet<Guid>> PaidInvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(_entries
            .Where(t => t.Kind == SafeTransactionKind.InvoicePayment && !t.IsReversal && !t.IsVoided && t.PurchaseInvoiceId is { } id && invoiceIds.Contains(id))
            .Select(t => t.PurchaseInvoiceId!.Value).ToHashSet());
    public Task<bool> AnyOpeningBalanceAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.Any(t => t.Kind == SafeTransactionKind.OpeningBalance && !t.IsReversal && !t.IsVoided));
    public Task<IReadOnlyList<SafeTransaction>> GetForPayrollMonthAsync(DateOnly month, Guid? employeeId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SafeTransaction>>(_entries
            .Where(t => t.PayrollMonth == month && t.EmployeeId != null && (employeeId == null || t.EmployeeId == employeeId)).ToList());
    public Task<IReadOnlyList<SafeTransaction>> GetPendingAdvancesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SafeTransaction>>(_entries.Where(t => t.IsPendingAdvance && !t.IsReversal && !t.IsVoided).ToList());
}

public sealed class SupplierLedgerStore : ISupplierLedgerRepository
{
    private readonly List<SupplierLedgerEntry> _lines = new();

    public Task<SupplierLedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_lines.SingleOrDefault(l => l.Id == id));
    public void Add(SupplierLedgerEntry aggregate) => _lines.Add(aggregate);
    public Task<decimal> BalanceAsync(Guid supplierId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_lines.Where(l => l.SupplierId == supplierId).Sum(l => l.Amount));
    public Task<IReadOnlyList<SupplierLedgerEntry>> GetBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SupplierLedgerEntry>>(_lines.Where(l => l.SourceId == sourceId).ToList());
}

public sealed class ApprovalRequestStore : IApprovalRequestRepository
{
    private readonly List<ApprovalRequest> _requests = new();

    public Task<ApprovalRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_requests.SingleOrDefault(r => r.Id == id));
    public void Add(ApprovalRequest aggregate) => _requests.Add(aggregate);
    public Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApprovalRequest>>(_requests.Where(r => r.IsPending).OrderBy(r => r.RequestedAtUtc).ToList());
    public Task<IReadOnlyList<ApprovalRequest>> GetDecidedAsync(int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApprovalRequest>>(_requests.Where(r => !r.IsPending).OrderByDescending(r => r.DecidedAtUtc).Take(take).ToList());
    public Task<ApprovalRequest?> GetPendingForAsync(Guid recordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_requests.SingleOrDefault(r => r.IsPending && r.RecordId == recordId));
}

// The test menu's names for a recipe's summary, and dates as they fall in UTC.
public sealed class NamesFromTheTestMenu : OtantikPos.Inventory.Application.Approvals.IRecordDescriptions
{
    public Task<string?> MenuItemNameAsync(RecipeTargetKind kind, int catalogItemId, int? variantId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>($"Menu item {catalogItemId}");

    public DateOnly BusinessDateOf(DateTime utc) => DateOnly.FromDateTime(utc).AddDays(-BusinessDayBehind);

    public DateOnly LocalDateOf(DateTime utc) => DateOnly.FromDateTime(utc);

    public DateTime LocalTimeOf(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

    // 1 for the hours after midnight, before the business day starts: the business date is a day
    // behind the calendar.
    public int BusinessDayBehind { get; set; }
}
