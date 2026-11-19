using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Metrics.Client.Extensions;

internal static class ManagementPortEndpoints
{
    internal static void RestrictToManagementPort(IEndpointConventionBuilder endpoint, int managementPort)
    {
        endpoint.Add(builder =>
        {
            var next = builder.RequestDelegate
                ?? throw new InvalidOperationException("A management endpoint has no request delegate.");
            builder.RequestDelegate = context => context.Connection.LocalPort == managementPort
                ? next(context)
                : RespondNotFound(context);
        });
    }

    private static Task RespondNotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
