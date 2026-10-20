using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Otantik.BuildingBlocks;
using OtantikPos.Inventory.Domain.Production;
using OtantikPos.Inventory.Domain.Purchases;
using OtantikPos.Inventory.Domain.RawMaterials;
using OtantikPos.Inventory.Domain.Recipes;
using OtantikPos.Inventory.Domain.StockCounts;
using OtantikPos.Inventory.Domain.StockMovements;
using OtantikPos.Inventory.Domain.Approvals;
using OtantikPos.Inventory.Domain.Categories;
using OtantikPos.Inventory.Domain.Expenses;
using OtantikPos.Inventory.Domain.Suppliers;
using OtantikPos.Inventory.Domain.Treasury;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.Costing;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Node.Infrastructure.Identity;
using OtantikPos.Node.Infrastructure.Messaging;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Node.Infrastructure.Printing;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Contracts;

namespace OtantikPos.Node.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "OtantikPos";

    // Every port of both POS modules except ICurrentUser and ITillNotifier, which come from the
    // signed-in user and the SignalR hub and so belong to the API.
    public static IServiceCollection AddNodeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.Configure<RestaurantOptions>(configuration.GetSection(RestaurantOptions.Section));
        services.Configure<PrintingOptions>(configuration.GetSection(PrintingOptions.Section));
        services.Configure<DeliverySystemOptions>(configuration.GetSection(DeliverySystemOptions.Section));
        services.Configure<PricingOptions>(configuration.GetSection(PricingOptions.Section));
        services.AddSingleton<RestaurantClock>();
        services.AddSingleton<IBusinessCalendar>(sp => sp.GetRequiredService<RestaurantClock>());
        services.TryAddSingleton(TimeProvider.System);

        // One query per load, said out loud (EF warns while it is only the default): an order
        // with its items and their add-ons is read as one snapshot, which the xmin concurrency
        // check on Order relies on. Split queries could pair an order with items from after it.
        services.AddDbContext<NodeDbContext>(o =>
            o.UseNpgsql(connectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<NodeDbContext>());

        // Ordering.
        services.AddScoped<IOrderStore, OrderStore>();
        services.AddScoped<ICatalog, CatalogStore>();
        services.AddScoped<IPricingSettings, PricingSettings>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IEventOutbox, EventOutbox>();
        services.AddScoped<IKitchenPrintQueue, KitchenPrintQueue>();
        services.AddScoped<IReceiptPrintQueue, ReceiptPrintQueue>();
        services.AddScoped<ICashReceived, CashReceivedRecord>();
        services.AddScoped<SalesLedger>();
        services.AddScoped<CostingService>();
        services.AddScoped<VarianceService>();
        services.AddScoped<KpiService>();
        services.AddScoped<BreakEvenService>();
        services.AddScoped<PurchaseSpendingService>();
        services.AddScoped<TreasuryService>();

        // Inventory.
        services.AddScoped<IRawMaterialRepository, RawMaterialRepository>();
        services.AddScoped<OtantikPos.Inventory.Application.RawMaterials.IMaterialUnits, MaterialUnits>();
        services.AddScoped<OtantikPos.Inventory.Application.Categories.IItemConversions, ItemConversions>();
        services.AddScoped<IRecipeRepository, RecipeRepository>();
        services.AddScoped<IRecipeCategoryRepository, RecipeCategoryRepository>();
        services.AddScoped<IStockMovementRepository, StockMovementRepository>();
        services.AddScoped<IStockCountRepository, StockCountRepository>();
        services.AddScoped<IPurchaseInvoiceRepository, PurchaseInvoiceRepository>();
        services.AddScoped<IPurchaseAttachmentStore, PurchaseAttachmentStore>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IPurchaseCategoryRepository, PurchaseCategoryRepository>();
        services.AddScoped<IApprovalRequestRepository, ApprovalRequestRepository>();
        services.AddScoped<ISafeTransactionRepository, SafeTransactionRepository>();
        services.AddScoped<ISupplierLedgerRepository, SupplierLedgerRepository>();
        services.AddScoped<OtantikPos.Inventory.Application.Approvals.IRecordDescriptions, RecordDescriptions>();
        services.AddScoped<OtantikPos.Inventory.Application.Treasury.ITaxRate, TillTaxRate>();
        services.AddScoped<IExpenseItemRepository, ExpenseItemRepository>();
        services.AddScoped<IPrepRecipeRepository, PrepRecipeRepository>();
        services.AddScoped<IProductionOrderRepository, ProductionOrderRepository>();
        services.AddScoped<OtantikPos.Hr.Domain.IEmployeeRepository, Hr.EmployeeRepository>();
        services.AddScoped<OtantikPos.Hr.Domain.IAttendanceDeviceRepository, Hr.AttendanceDeviceRepository>();
        services.AddScoped<OtantikPos.Hr.Domain.IAttendanceLogRepository, Hr.AttendanceLogRepository>();
        services.AddScoped<OtantikPos.Hr.Domain.IDayMarkRepository, Hr.DayMarkRepository>();
        services.AddScoped<OtantikPos.Hr.Domain.IDailyRecordRepository, Hr.DailyRecordRepository>();
        services.AddScoped<OtantikPos.Hr.Domain.IPayrollAdjustmentRepository, Hr.PayrollAdjustmentRepository>();
        services.AddScoped<OtantikPos.Hr.Domain.IPayrollSlipRepository, Hr.PayrollSlipRepository>();
        services.AddScoped<OtantikPos.Inventory.Application.Hr.IAttendanceSettings, Hr.AttendanceSettingsStore>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RetryOnConflictBehavior<,>));

        // The delivery system. Short timeouts: a cashier is waiting on most of these calls, and
        // "the internet is down" has to come back in seconds, not after the default 30.
        services.AddHttpClient<DeliverySystemApi>((sp, http) =>
            {
                var settings = sp.GetRequiredService<IOptions<DeliverySystemOptions>>().Value;
                if (settings.IsConfigured)
                    http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
                http.DefaultRequestHeaders.Add(DeliverySystemApi.NodeKeyHeader, settings.NodeKey);
            })
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(8);
                resilience.Retry.MaxRetryAttempts = 2;
            });
        services.AddScoped<ICustomerDirectory>(sp => sp.GetRequiredService<DeliverySystemApi>());
        services.AddScoped<ILoyaltyGateway>(sp => sp.GetRequiredService<DeliverySystemApi>());
        services.AddTransient<INotificationHandler<OrderChanged>, PushOrderToDeliverySystem>();
        services.AddScoped<ReferenceDataApplier>();
        services.AddSingleton<DeliverySystemLink>();
        services.AddHostedService<CloudOrderListener>();
        services.AddHostedService<ReferenceDataSync>();
        services.AddScoped<OnlineSalesImporter>();
        services.AddSingleton<OnlineSalesSync>();
        services.AddHostedService(sp => sp.GetRequiredService<OnlineSalesSync>());

        // Outbox.
        services.AddSingleton<EventSerializer>();
        services.AddSingleton<OutboxSignal>();
        services.AddHostedService<OutboxProcessor>();

        // Printing.
        services.AddSingleton<PrintSignal>();
        services.AddSingleton<TcpPrinterClient>();
        services.AddSingleton<PrinterStatus>();
        services.AddHostedService<PrintWorker>();
        services.AddHostedService<Housekeeping>();

        // Staff PINs.
        services.AddScoped<StaffDirectory>();

        return services;
    }
}
