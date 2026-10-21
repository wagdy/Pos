using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OtantikPos.Node.Api.Hosting;

// The attendance device's own port (Attendance:DevicePort), when the till's screens stay on its
// machine and only the device reaches it over the network: the till listens there too (as
// --urls says), and answers nothing there but the device's /iclock paths. Unset, as in the
// installed till, every port answers everything.
internal static class DevicePort
{
    public const string Setting = "Attendance:DevicePort";

    public static int? Of(IConfiguration configuration) => configuration.GetValue<int?>(Setting);

    public static IApplicationBuilder UseDevicePort(this IApplicationBuilder app, int? port)
    {
        if (port is not { } devicePort)
            return app;
        return app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort == devicePort && !context.Request.Path.StartsWithSegments("/iclock"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
    }

    // This machine's addresses on its networks, for the device to be given one: IPv4, up, not
    // its own loopback.
    public static IReadOnlyList<string> Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();
}
