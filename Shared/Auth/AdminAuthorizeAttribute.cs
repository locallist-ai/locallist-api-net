using Microsoft.AspNetCore.Mvc;

namespace LocalList.API.NET.Shared.Auth;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AdminAuthorizeAttribute : TypeFilterAttribute
{
    public AdminAuthorizeAttribute() : base(typeof(AdminAuthorizationFilter)) { }
}
