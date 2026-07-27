namespace LocalList.API.NET.Shared.Constants;

/// <summary>
/// Nombres canónicos de tier de usuario. Fuente ÚNICA de verdad: antes el literal <c>"pro"</c>
/// estaba copiado en ~6 ficheros (gates de Import/Favorites/generación, escritor de Billing).
/// </summary>
public static class Tiers
{
    /// <summary>Tier Plus (de pago). Mapea al entitlement de RevenueCat.</summary>
    public const string Pro = "pro";

    /// <summary>Tier gratuito por defecto.</summary>
    public const string Free = "free";
}
