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
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockMovements;
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
        services.AddSingleton<IStockMovementRepository>(Inventory.Movements);

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
// unless a test changes it.
public sealed class FixedPricing(decimal taxPercentage) : IPricingSettings
{
    public decimal RedemptionValuePer100Points { get; set; } = LoyaltyRedemption.DefaultValuePer100Points;

    public Task<decimal> GetTaxPercentageAsync(CancellationToken cancellationToken) => Task.FromResult(taxPercentage);

    public Task<decimal> GetRedemptionValuePer100PointsAsync(CancellationToken cancellationToken) => Task.FromResult(RedemptionValuePer100Points);
}

public sealed class InventoryStore : IRawMaterialRepository
{
    private readonly List<RawMaterial> _materials = new();

    public RecipeStore Recipes { get; } = new();
    public MovementStore Movements { get; } = new();

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

public sealed class RecipeStore : IRecipeRepository
{
    private readonly List<Recipe> _recipes = new();

    public Task<Recipe?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_recipes.SingleOrDefault(r => r.Id == id));
    public void Add(Recipe aggregate) => _recipes.Add(aggregate);
    public void Remove(Recipe recipe) => _recipes.Remove(recipe);
    public Task<Recipe?> GetByTargetAsync(RecipeTarget target, CancellationToken cancellationToken = default) => Task.FromResult(_recipes.SingleOrDefault(r => r.Target == target));
    public Task<IReadOnlyList<Recipe>> GetByTargetsAsync(IReadOnlyCollection<RecipeTarget> targets, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Recipe>>(_recipes.Where(r => targets.Contains(r.Target)).ToList());
}

public sealed class MovementStore : IStockMovementRepository
{
    public List<StockMovement> All { get; } = new();

    public Task<StockMovement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(All.SingleOrDefault(m => m.Id == id));
    public void Add(StockMovement aggregate) => All.Add(aggregate);
    public Task<bool> ExistsAsync(Guid sourceId, StockMovementReason reason, CancellationToken cancellationToken = default) => Task.FromResult(All.Any(m => m.SourceId == sourceId && m.Reason == reason));
}
