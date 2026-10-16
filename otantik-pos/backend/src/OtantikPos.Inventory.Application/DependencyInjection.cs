using Microsoft.Extensions.DependencyInjection;
using OtantikPos.Inventory.Application.EventHandlers;

namespace OtantikPos.Inventory.Application;

public static class DependencyInjection
{
    // Infrastructure supplies the repositories and the unit of work.
    public static IServiceCollection AddInventoryApplication(this IServiceCollection services)
    {
        services.AddMediatR(config => config.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddScoped<OrderStockPoster>();
        services.AddScoped<Approvals.DeletionTargets>();
        services.AddScoped<Production.PrepItemWriter>();
        services.AddScoped<Production.ProductionWriter>();
        services.AddScoped<Treasury.TreasuryWriter>();
        services.AddScoped<Purchases.InvoiceRecorder>();
        services.AddScoped<Purchases.InvoicePoster>();
        services.AddScoped<Purchases.InvoiceCorrector>();
        services.AddScoped<RawMaterials.MaterialUnitWriter>();
        return services;
    }
}
