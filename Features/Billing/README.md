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

### CRÍTICO — no desacoplar "a quién verifico" de "a quién acredito"

El paso clave que cierra el god-token: **se resuelve el `User` PRIMERO** (desde los ids del
payload) y luego **se verifica contra RC exclusivamente los ids PROPIOS de ese user** (su
`User.Id` y su `RcCustomerId` ya enlazado), nunca un `app_user_id` arbitrario del JSON. Sin esto,
un evento con `app_user_id`=(un id que RC reporta Active pero sin User local, p.ej. un
`$RCAnonymousID` sin enlazar) + `original_app_user_id`=(Guid del atacante) verificaría UNA
identidad y acreditaría OTRA → pro gratis. Variante griefing (bajar a una víctima a free con solo
su `User.Id` + el secreto) queda igualmente cerrada: se verifica el id de la víctima, no la basura
del payload. Si ningún id propio del user está Active en RC → no se concede. El enlace real
`rc_customer_id` lo hace la vía normal del purchase de la app, NO este webhook (no escribe
`rc_customer_id` desde el payload — era el vector de secuestro persistente).

## Qué hay aquí

- **`BillingController`** — `POST /webhooks/revenuecat`. Verifica el header `Authorization`
  contra un secreto configurable **antes de leer el body** (fail-closed + límite de DoS de
  parseo), deserializa y delega en el processor. `RcUnavailable` → 503 (RevenueCat reintenta).
  Rate-limit por IP `RevenueCatWebhookLimit` (60/min).
- **`IRevenueCatClient` / `RevenueCatClient`** — consulta el estado real del suscriptor en la
  REST API de RC. Si la key no está o RC no responde → `Unavailable` (nunca concede a ciegas).
- **`BillingEventProcessor`** — único escritor de `User.Tier` guiado por billing. Resuelve el user
  y luego verifica **sus propios ids** contra RC (active→pro, inactive→free). Idempotente (dedup
  por `billing_events.rc_event_id` + índice UNIQUE; el catch de 23505 está **acotado por nombre**
  a `IX_billing_events_rc_event_id`, cualquier otra unique violation propaga). No escribe
  `rc_customer_id` desde el payload. Concurrencia: sin serialización por-usuario, dos eventos del
  mismo user escriben en orden de commit — auto-corrige en el siguiente evento (ventana mínima;
  no se añadió rowversion por volumen).
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

El secreto se valida **antes** de deserializar el body, así que un caller no autorizado no nos
hace parsear un payload arbitrario; el cap de 10 MB de Kestrel (`Program.cs`) acota un body
autorizado. Además, DoS de 2º orden (un atacante con el secreto floodeando `rc_event_id` frescos
→ un GET a la REST API de RC por cada uno → RC nos 429ea → lookups legítimos degradan a 503): se
mitiga con el rate-limit por IP `RevenueCatWebhookLimit` (60/min, en `RateLimitingExtensions`).

## Catálogo Plus vs free (DECIDIDO 2026-07-13 — enforcement server-side activo)

Implementado en `Shared/Usage/` (`PlanGenerationGateService` + `UsageCounterService` sobre la
tabla `usage_counters`, migración `AddUsageCounters`). Los gates corren en los DOS endpoints de
generación (`POST /chat/generate` y `POST /builder/chat`), que desde F4 exigen `[Authorize]`
(sin identidad no hay contador mensual posible; el funnel anónimo sigue vivo en `/chat/turn`).

| Regla | Free | Plus (`users.tier = "pro"`) |
|---|---|---|
| Planes IA | 3/mes (mes natural UTC) | Ilimitado, cap antiabuso 50/día (UTC) |
| Duración del plan | ≤ 3 días | ≤ 14 días (hard cap para todos) |
| Multi-ciudad | no | sí (ver hueco abajo) |
| Favoritos | 50 | ilimitado (ver hueco abajo) |
| Planes guardados | 5 activos (filas en `plans` con `created_by`) | ilimitado |
| Catálogo, edición manual, Follow online, `/chat/turn` | ilimitado | ilimitado |

### Errores estructurados (la app los consume para el upsell)

Orden de evaluación del gate (los rechazos de validación NO consumen contador):

1. `401 {error:"Invalid token claims."}` — token válido pero user inexistente en DB.
2. `400 {error:"duration_invalid", requestedDays, maxDays:14}` — hard cap global (también lo
   corta la validación `[Range(1,14)]` de `TripContextDto.Days` con el 400 del framework).
3. `403 {error:"duration_requires_plus", requestedDays, maxDays:3, plusMaxDays:14}` — free con
   más de 3 días explícitos.
4. `403 {error:"saved_plans_limit_reached", used, limit:5}` — free con ≥5 planes guardados
   (`DELETE /plans/:id` libera hueco).
5. `403 {error:"plan_limit_reached", used:3, limit:3, resetsAt:<inicio del mes siguiente UTC>}`
   — free, contador mensual agotado.
6. `429 {error:"daily_cap_reached", used:50, limit:50, resetsAt:<siguiente medianoche UTC>}` —
   Plus, cap diario antiabuso. **Elegido 429, no 403**: es throttling, no falta de
   entitlement — la app no debe pintar upsell a un usuario que YA es Plus.

### Semántica del contador (elegida, con test en `PlanGateTests`)

El permiso se consume cuando la generación **arranca** (último paso del gate, justo antes del
pipeline LLM+RAG). A partir de ahí NO se devuelve aunque no salga plan (sin places, LLM caído):
el coste ya se pagó y "devolver si falla" sería retry-abuse barato contra el límite mensual.
Los paths que no generan no consumen: idempotencia de `/chat/generate` (releer plan existente),
coverage (`city_unsupported`), validación de input y los rechazos 400/403 del propio gate.

### Atomicidad

`UsageCounterService.TryConsumeAsync` = `INSERT … ON CONFLICT … DO UPDATE SET count = count+1
WHERE count < limit` en un único statement: el row-lock de Postgres serializa los increments y
dos requests concurrentes no pueden gastar el mismo permiso (tests de concurrencia a nivel
servicio y a nivel endpoint). PK compuesta `(user_id, feature, period_start)`; features:
`ai_plans_month` (free) y `ai_plans_day` (Plus) — keys separadas porque el día 1 del mes ambos
periodos comparten fecha. FK a `users` con cascade (GDPR); el reset-por-reregistro que permite
el borrado de cuenta queda acotado por el techo horario por IP y el throttle de `/auth/register`.

### Días derivados del texto libre

El gate valida los días EXPLÍCITOS del request; los que el LLM derive del mensaje se acotan con
el clamp `prefs.Days ≤ maxDays(tier)` dentro de `PlanGenerationService.GenerateAsync` — ningún
camino produce un plan más largo que el techo del tier.

### Huecos documentados (NO inventados)

- **Favoritos (50 free)**: el backend no tiene modelo de favoritos (solo
  `UserProfile.FavoriteCity`, que es otra cosa). El límite se implementará con el modelo.
- **Multi-ciudad (solo Plus)**: el request es mono-ciudad por construcción
  (`TripContextDto.City`/`ChatSlots.City`/`Plan.City` escalares) — hoy no hay nada que validar.
  Código de error reservado para cuando exista: `403 {error:"multicity_requires_plus"}`.
- **Follow offline (solo Plus)**: gate app-side; el backend solo expone el tier (ya existía).

El guard genérico `[RequirePro]` sigue disponible para endpoints binarios (todo-o-nada); los
gates de generación usan el servicio porque necesitan contador + errores estructurados.
