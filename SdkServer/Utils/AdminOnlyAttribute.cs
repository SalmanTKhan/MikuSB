using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MikuSB.SdkServer.Utils;

/// <summary>
/// Restricts a controller to the admin port with a valid bearer token. Controllers are mapped on every
/// listener, so this check lives on the controller itself and fails closed on the public SDK ports.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AdminOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (!AdminApi.IsAdminRequest(http))
        {
            context.Result = new NotFoundResult();
            return;
        }

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        if (!AdminToken.IsValid(token))
            context.Result = new UnauthorizedResult();
    }
}
