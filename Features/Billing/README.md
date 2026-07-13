# Billing (F4 — monetización)

Pata backend del tier de suscripción. Escribe `User.Tier` desde los eventos de RevenueCat
y expone el enforcement server-side.

## Qué hay aquí

- **`BillingController`** — `POST /webhooks/revenuecat`. Verifica el header `Authorization`
  contra un secreto configurable (fail-closed), parsea el evento y delega en el processor.
- **`BillingEventProcessor`** — único escritor de `User.Tier` guiado por billing. Idempotente
  (dedup por `billing_events.rc_event_id` + índice UNIQUE) y reorder-safe (guard por
  `event_timestamp_ms`). Mapea `app_user_id` → `User` por Guid o por `rc_customer_id`.
- **`Shared/Auth/RequireProAttribute`** + `RequireProAuthorizationFilter` — guard reutilizable
  `[RequirePro]`. Re-consulta el tier en DB (NO el claim `tier` del JWT, que caduca a los 15 min).

## Configuración (Railway / secrets)

- `REVENUECAT_WEBHOOK_AUTH` — **TODO(pablo)**: valor exacto del header `Authorization` que
  configures en el dashboard de RevenueCat (Project settings → Integrations → Webhooks).
  Hasta que esté seteado, el webhook rechaza todo (503, fail-closed).
- `RevenueCat__PlusEntitlementId` — id del entitlement que mapea a "pro" (default `plus`).

## PENDIENTE DE PRODUCTO — catálogo de features Plus

El guard `[RequirePro]` está **registrado y listo**, pero **no se aplica a ningún endpoint
todavía**. Motivo: la definición de "qué es Plus vs free" es una decisión de producto que aún
no está tomada. No se ha inventado aquí.

Se auditó el código en busca de una señal premium existente (`grep` de `Tier`/`pro`/`entitlement`
sobre `Features/` y `Shared/`): el único uso de `Tier` es echo en `/account`, `/auth/*` y su firma
en el JWT — **no hay ningún endpoint marcado como premium**. Por eso no se gateó ninguno.

Cuando producto defina el catálogo, aplicar sobre los endpoints elegidos, encima de `[Authorize]`:

```csharp
[Authorize]
[RequirePro]
public async Task<IActionResult> AlgunEndpointPlus(...) { ... }
```
