using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ValheimServerManager.Services;

public sealed class SteamAdminCookieEvents(ManagerRoleService roles) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var steamId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = steamId is null ? null : await roles.GetRole(steamId);
        if (role is not null)
        {
            var identity = new ClaimsIdentity(context.Principal!.Identity);
            foreach (var claim in identity.FindAll(ClaimTypes.Role).ToArray()) identity.RemoveClaim(claim);
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
            context.ReplacePrincipal(new ClaimsPrincipal(identity));
            return;
        }
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
