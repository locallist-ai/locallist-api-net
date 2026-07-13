# Billing (F4 — monetización)

Pata backend del tier de suscripción. Escribe `User.Tier` desde los eventos de RevenueCat
y expone el enforcement server-side.

## Modelo de seguridad — el webhook es un TRIGGER, no la fuente de verdad

Pasado el secreto, el payload sigue siendo attacker-shaped: si el secreto se filtra, cualquiera
puede POSTear un `app_user_id`, `entitlement` o `event_timestamp_ms` arbitrarios. Por eso **el
tier NO se deriva del payload**, sino del estado autoritativo consultado a la REST API de
RevenueCat (`GET /subscribers/{app_user_id}` con una secret API key). Así:

- Un grant forjado nombrando a una víctima que no compró → RC la reporta inactiva → no hay pro.
- Un `event_timestamp_ms = long.MaxValue` forjado ya no congela nada: no hay guard de timestamp;
  el tier se re-deriva del estado real de RC en cada evento (una expiración genuina revoca).

## Qué hay aquí

- **`BillingController`** — `POST /webhooks/revenuecat`. Verifica el header `Authorization`
  contra un secreto configurable **antes de leer el body** (fail-closed + límite de DoS de
  parseo), deserializa y delega en el processor. `RcUnavailable` → 503 (RevenueCat reintenta).
- **`IRevenueCatClient` / `RevenueCatClient`** — consulta el estado real del suscriptor en la
  REST API de RC. Si la key no está o RC no responde → `Unavailable` (nunca concede a ciegas).
- **`BillingEventProcessor`** — único escritor de `User.Tier` guiado por billing. Deriva el tier
  de RC (active→pro, inactive→free), idempotente (dedup por `billing_events.rc_event_id` +
  índice UNIQUE; el catch de 23505 está **acotado por nombre** a `IX_billing_events_rc_event_id`,
  cualquier otra unique violation propaga). Mapea `app_user_id` → `User` por Guid o
  `rc_customer_id`; al enlazar `rc_customer_id` evita colisiones (no traga un grant legítimo).
- **`Shared/Auth/RequireProAttribute`** + `RequireProAuthorizationFilter` — guard reutilizable
  `[RequirePro]`. Re-consulta el tier en DB (NO el claim `tier` del JWT, que caduca a los 15 min).

## Configuración (Railway / secrets)

- `REVENUECAT_WEBHOOK_AUTH` — **TODO(pablo)**: valor exacto del header `Authorization` que
  configures en el dashboard de RevenueCat (Project settings → Integrations → Webhooks).
  Hasta que esté seteado, el webhook rechaza todo (503, fail-closed).
- `REVENUECAT_REST_API_KEY` — **TODO(pablo)**: secret API key (sk_...) de RC para verificar el
  estado del suscriptor. Distinta del secreto del webhook. Sin ella no se concede ningún upgrade
  (el webhook responde 503 y RC reintenta). También legible como `RevenueCat__RestApiKey`.
- `RevenueCat__PlusEntitlementId` — id del entitlement que mapea a "pro" (default `plus`).

## DoS

El endpoint es anónimo pero el secreto se valida **antes** de deserializar el body, así que un
caller no autorizado no nos hace parsear un payload arbitrario; el cap de 10 MB de Kestrel
(`Program.cs`) acota un body autorizado. El rate-limit por endpoint se deja a la rama
`fix/v1-anon-endpoints-ratelimit` para no colisionar en la infra de rate-limit.

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
