using Microsoft.Extensions.DependencyInjection;

namespace OtantikPos.Ordering.Application;

public static class DependencyInjection
{
    // The ports in Ports/Ports.cs come from Infrastructure; ICurrentUser and ITillNotifier come
    // from the API; TimeProvider from the host.
    public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
    {
        services.AddMediatR(config => config.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddScoped<OrderWorkflow>();
        return services;
    }
}
