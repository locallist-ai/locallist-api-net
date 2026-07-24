using Microsoft.AspNetCore.Mvc;

namespace LocalList.API.NET.Shared.Auth;

/// <summary>
/// Gates an endpoint (or controller) to users on the "pro" tier. Apply on top of
/// <c>[Authorize]</c>: authentication establishes identity, this filter enforces entitlement.
///
/// Enforcement re-reads <c>User.Tier</c> from the database on every request. It deliberately
/// does NOT trust the <c>tier</c> claim in the JWT: app access tokens live ~15 min, so a claim
/// goes stale the moment a purchase/expiration flips the tier (the app can't be forced to
/// refresh mid-session, and a client could forge the claim if we trusted it).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireProAttribute : TypeFilterAttribute
{
    public RequireProAttribute() : base(typeof(RequireProAuthorizationFilter)) { }
}
