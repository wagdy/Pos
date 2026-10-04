using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Infrastructure.Tests;
using OtantikPos.Ordering.Domain;

// A stand-in for the delivery system, until it serves the pos-sync contract (shared/README.md):
// the menu, staff and tax rate, a customer with points, and the hub the till listens on. Point the
// till at it with DeliverySystem:BaseUrl=http://127.0.0.1:5099 and DeliverySystem:NodeKey=test-node-key.
//
// Then play the cloud's part from another terminal:
//   curl -X POST "http://127.0.0.1:5100/captain-order?table=9"   a captain submits a table's order
//   curl -X POST http://127.0.0.1:5100/stop                      the internet goes down
//   curl -X POST http://127.0.0.1:5100/start                     and comes back
const string cloudUrl = "http://127.0.0.1:5099";
const string controlUrl = "http://127.0.0.1:5100";

var cloud = await StartCloudAsync();
var gate = new SemaphoreSlim(1, 1);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(controlUrl);
var control = builder.Build();

control.MapGet("/", () =>
    $"Fake delivery system on {cloudUrl} ({(cloud is null ? "stopped" : "running")}). POST /captain-order?table=9, /stop, /start.");

control.MapPost("/captain-order", async (string? table) =>
{
    if (cloud is null)
        return Results.Conflict("The fake delivery system is stopped; POST /start first.");

    var now = DateTime.UtcNow;
    var order = new Order
    {
        Type = OrderType.DineIn,
        TableNumber = table ?? "9",
        CreatedByUserId = "staff-captain",
        PaymentStatus = PaymentStatus.Pending,
        CreatedAt = now,
        UpdatedAt = now,
        // Submitted: the till prints whatever arrives already sent.
        OrderItems = [new OrderItem { MenuItemId = FakeDeliverySystem.Burger, MenuItemName = "Burger", UnitPrice = 120, Quantity = 2, SentToKitchenAt = now }],
    };
    // Priced as the delivery system prices it, with the fake's 14% tax.
    OrderPricing.Reprice(order, FakeDeliverySystem.ReferenceData().TaxPercentage);
    cloud.ChangedOrders.Add(order);
    await cloud.BroadcastAsync(order);
    return Results.Ok(new { order.PublicId, order.TableNumber });
});

control.MapPost("/stop", async () =>
{
    await gate.WaitAsync();
    try
    {
        if (cloud is not null)
        {
            await cloud.DisposeAsync();
            cloud = null;
        }
        return Results.Ok("stopped");
    }
    finally
    {
        gate.Release();
    }
});

control.MapPost("/start", async () =>
{
    await gate.WaitAsync();
    try
    {
        cloud ??= await StartCloudAsync();
        return Results.Ok("running");
    }
    finally
    {
        gate.Release();
    }
});

Console.WriteLine($"Fake delivery system on {cloudUrl}; controls on {controlUrl}.");
await control.RunAsync();
if (cloud is not null)
    await cloud.DisposeAsync();

static async Task<FakeDeliverySystem> StartCloudAsync()
{
    var fake = new FakeDeliverySystem(cloudUrl);
    await fake.StartAsync();
    fake.Customers["01001234567"] = new CustomerProfile("customer-1", "Ali Hassan", "01001234567", 500);
    fake.Balances["customer-1"] = 500;
    return fake;
}
