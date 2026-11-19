using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Metrics.Client.Extensions;

internal static class ManagementPortEndpoints
{
    private const int PublicPortNotFoundOrder = int.MaxValue;

    internal static void RestrictToManagementPort(
        IEndpointRouteBuilder endpoints,
        IEndpointConventionBuilder endpoint,
        string path,
        int managementPort)
    {
        endpoint.RequireHost($"*:{managementPort}");
        endpoints.Map(path, () => Results.NotFound())
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithOrder(PublicPortNotFoundOrder);
    }
}
