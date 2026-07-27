using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Routing;
using LocalList.API.NET.Shared.Access;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.PostHog;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.NET.Features.Plans;

[ApiController]
[Route("plans")]
public class PlansController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<PlansController> _logger;
    private readonly LanguageAccessor _lang;
    private readonly ISegmentResolver _routeResolver;
    private readonly PostHogService _posthog;
    private readonly IConfiguration _config;
    private readonly IPlanAccessService _access;

    public PlansController(LocalListDbContext db, ILogger<PlansController> logger, LanguageAccessor lang, ISegmentResolver routeResolver, PostHogService posthog, IConfiguration config, IPlanAccessService access)
    {
        _db = db;
        _logger = logger;
        _lang = lang;
        _routeResolver = routeResolver;
        _posthog = posthog;
        _config = config;
        _access = access;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreatePlan([FromBody] CreateUserPlanRequest request, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

        // Cupo de planes guardados (catálogo Plus, decisión Pablo 2026-07-22): límite de
        // ALMACENAMIENTO aplicado AQUÍ, independiente del contador mensual de generación IA.
        // Un free con FreeSavedPlansLimit planes activos no puede crear/guardar más (DELETE
        // /plans/:id libera hueco); Plus ilimitado. Tier SIEMPRE fresco de DB (el claim del
        // JWT es rancio/falsificable). Carrera residual aceptada: count-then-insert sin
        // serialización por usuario puede dejar 5+N-1 bajo N POSTs simultáneos; es un hueco de
        // almacenamiento, no un bypass de los gates de dinero.
        var isPro = await TierGate.IsProAsync(_db, userId.Value, ct);
        if (!isPro)
        {
            var saved = await _db.Plans.CountAsync(p => p.CreatedById == userId.Value, ct);
            if (saved >= PlanGenerationGateService.FreeSavedPlansLimit)
            {
                _logger.LogInformation(
                    "Plans: saved-plans limit denied userId={UserId} saved={Saved}", userId, saved);
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "saved_plans_limit_reached",
                    used = saved,
                    limit = PlanGenerationGateService.FreeSavedPlansLimit
                });
            }
        }

        // Misma validacion de ventana que /builder/chat y /chat/generate (paridad total):
        // null => OK (plan manual sin fecha). Fuera de [today-1, today+MaxTripHorizonDays] => 400.
        // El builder manual no corre scheduler, asi que la fecha solo se persiste para display.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!TripContextDto.IsStartDateWithinWindow(request.StartDate, today))
        {
            _logger.LogInformation(
                "Plans: create rejected invalid_start_date startDate={StartDate}",
                request.StartDate?.ToString("yyyy-MM-dd") ?? "(null)");
            return BadRequest(new
            {
                error = "invalid_start_date",
                message = $"Trip start date must be between today and {TripContextDto.MaxTripHorizonDays} days from now.",
                startDate = request.StartDate?.ToString("yyyy-MM-dd"),
            });
        }

        var now = DateTimeOffset.UtcNow;

        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            City = request.City.Trim(),
            Type = request.Type?.Trim() ?? "custom",
            DurationDays = request.DurationDays,
            StartDate = request.StartDate,
            IsPublic = false,
            IsShowcase = false,
            CreatedById = userId.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Plans.Add(plan);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} created plan {PlanId} ({Name})", userId, plan.Id, plan.Name);

        return Created($"/plans/{plan.Id}", PlanDetailDto.FromEntityWithAllDays(plan, _lang.Language, _config["Api:PublicBaseUrl"]));
    }

    // Clonar un plan curado/showcase o público a la cuenta del caller ("guardar este plan como mío").
    // Gancho de conversión del onboarding: el showcase se muestra sin cuenta, pero GUARDARLO exige
    // registro (este endpoint es [Authorize] AppScheme). Crea una copia PROFUNDA propiedad del caller:
    // plan privado nuevo (source="cloned") con TODOS los stops copiados fielmente. cloned_from apunta
    // al origen para idempotencia y trazabilidad.
    [HttpPost("{id}/clone")]
    [Authorize(AuthenticationSchemes = AuthSchemes.App)]
    public async Task<IActionResult> ClonePlan(Guid id, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

        // 1. El origen debe ser CLONABLE: un showcase curado (admin) O un plan público de usuario.
        //    Un plan privado/unlisted ajeno o inexistente => 404 OPACO (no filtra existencia). El
        //    showcase es contenido curado admin: clonable incondicionalmente. El público de usuario
        //    honra bloqueos vía IPlanAccessService (un usuario bloqueado no puede ni verlo ni copiarlo).
        var source = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        var clonable = source != null && (source.IsShowcase || source.Visibility == "public");
        if (!clonable)
        {
            if (source != null)
                _logger.LogWarning("User {UserId} attempted to clone non-clonable plan {PlanId}", userId, id);
            return NotFound(new { error = "Plan not found" });
        }

        if (!source!.IsShowcase)
        {
            var access = await _access.GetAccessAsync(id, userId, ct);
            if (!access.CanView)
            {
                _logger.LogWarning("User {UserId} blocked from cloning public plan {PlanId}", userId, id);
                return NotFound(new { error = "Plan not found" });
            }
        }

        // 2. Idempotencia — fast-path: si el caller YA tiene un clon activo de ESTE origen, se
        //    devuelve ese (200) en vez de crear otro. DELETE es hard, así que "activo" == "existe en
        //    DB". Se comprueba ANTES del cap para que un re-clone en el tope no reciba un 403 espurio.
        //    OJO: este SELECT es solo la vía rápida del doble-tap NO concurrente; la garantía dura
        //    contra N clones concurrentes la da el índice único parcial (created_by, cloned_from) +
        //    el catch de 23505 más abajo (el SELECT-then-INSERT no es atómico por sí solo).
        var existing = await FindExistingCloneAsync(userId.Value, id, ct);
        if (existing != Guid.Empty)
        {
            _logger.LogInformation(
                "User {UserId} re-cloned plan {PlanId}; returning existing clone {CloneId}", userId, id, existing);
            return Ok(await LoadPlanDetailAsync(existing, ct));
        }

        // 3. Cap de planes guardados: clonar cuenta como un plan guardado más. MISMO gate que
        //    POST /plans (free con FreeSavedPlansLimit planes activos => 403; Plus ilimitado). Tier
        //    fresco de DB. Un usuario nuevo clonando su 1er plan (0 guardados) no lo topa.
        var isPro = await TierGate.IsProAsync(_db, userId.Value, ct);
        if (!isPro)
        {
            var saved = await _db.Plans.CountAsync(p => p.CreatedById == userId.Value, ct);
            if (saved >= PlanGenerationGateService.FreeSavedPlansLimit)
            {
                _logger.LogInformation(
                    "Plans: clone denied by saved-plans limit userId={UserId} saved={Saved}", userId, saved);
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "saved_plans_limit_reached",
                    used = saved,
                    limit = PlanGenerationGateService.FreeSavedPlansLimit
                });
            }
        }

        // 4. Copia profunda. Nuevo Guid; owner = caller; visibility private; source="cloned" (plan de
        //    usuario NORMAL: nunca showcase ni curated — isCurated mira Source=="curated"). i18n se
        //    copia tal cual: como source!="curated", el DTO sirve el idioma existente sin exigir
        //    translation_status approved, así que el ES del showcase se conserva.
        //    Decisiones del hub 2026-07-27:
        //     · StartDate NO se hereda: un showcase con fecha fija/pasada daría un clon fechado en el
        //       pasado (y chocaría con la viabilidad por fecha); el usuario pone su fecha al seguir/editar.
        //     · TripContext solo se copia de un SHOWCASE curado (contexto de diseño del itinerario, no
        //       personal). De un plan PÚBLICO de usuario NO se copia: son datos personales de un extraño
        //       (dieta/presupuesto/grupo/exclusiones) que no deben persistir bajo el cloner.
        var now = DateTimeOffset.UtcNow;
        var newPlanId = Guid.NewGuid();
        var clone = new Plan
        {
            Id = newPlanId,
            Name = source.Name,
            Description = source.Description,
            City = source.City,
            Type = source.Type,
            DurationDays = source.DurationDays,
            ImageUrl = source.ImageUrl,
            StartDate = null,
            TripContext = source.IsShowcase ? CloneJson(source.TripContext) : null,
            NameI18n = CloneJson(source.NameI18n),
            DescriptionI18n = CloneJson(source.DescriptionI18n),
            Visibility = "private",
            Source = "cloned",
            IsShowcase = false,
            ClonedFrom = source.Id,
            CreatedById = userId.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Plans.Add(clone);

        foreach (var s in source.Stops)
        {
            _db.PlanStops.Add(new PlanStop
            {
                Id = Guid.NewGuid(),
                PlanId = newPlanId,
                PlaceId = s.PlaceId,
                DayNumber = s.DayNumber,
                OrderIndex = s.OrderIndex,
                TimeBlock = s.TimeBlock,
                SuggestedArrival = s.SuggestedArrival,
                SuggestedDurationMin = s.SuggestedDurationMin,
                TravelFromPrevious = CloneJson(s.TravelFromPrevious),
                CreatedAt = now,
            });
        }

        // El SELECT-then-INSERT del paso 2 no es atómico: N clones concurrentes del MISMO origen
        // pasarían todos el pre-check y crearían duplicados (+ ráfaga sobre el cap). El índice único
        // parcial (created_by, cloned_from) los reduce a UNO: el que pierde la carrera recibe 23505,
        // re-lee el ganador y devuelve ESE (200) — mismo patrón que el índice único de Favorites.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrorPredicates.IsUniqueViolation(ex))
        {
            var winner = await FindExistingCloneAsync(userId.Value, id, ct);
            if (winner == Guid.Empty) throw; // no debería ocurrir: el 23505 es del índice de clone.
            _logger.LogInformation(
                "User {UserId} lost clone race on plan {PlanId}; returning winner {CloneId}", userId, id, winner);
            return Ok(await LoadPlanDetailAsync(winner, ct));
        }

        _logger.LogInformation(
            "User {UserId} cloned plan {SourceId} into {CloneId} ({StopCount} stops)",
            userId, id, newPlanId, source.Stops.Count);

        _ = _posthog.CaptureAsync(userId.Value.ToString(), "plan_cloned", new()
        {
            ["source_plan_id"] = id.ToString(),
            ["plan_id"] = newPlanId.ToString(),
            ["city"] = clone.City,
        });

        return Created($"/plans/{newPlanId}", await LoadPlanDetailAsync(newPlanId, ct));
    }

    // Re-lee el plan con stops + places y resuelve segmentos de ruta, igual que GET /plans/{id},
    // para devolver un PlanDetailDto completo al que la app pueda navegar directamente.
    private async Task<PlanDetailDto> LoadPlanDetailAsync(Guid planId, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstAsync(p => p.Id == planId, ct);
        var routeSegments = await _routeResolver.ResolveAsync(plan.Stops, RoutingMode.Walking, ct);
        return PlanDetailDto.FromEntity(plan, _lang.Language, routeSegments, _config["Api:PublicBaseUrl"]);
    }

    // Id del clon ACTIVO del caller para un origen dado (Guid.Empty si no hay). "activo" == existe
    // (DELETE es hard). Fuente única para el pre-check idempotente y para re-leer el ganador tras 23505.
    private Task<Guid> FindExistingCloneAsync(Guid userId, Guid sourceId, CancellationToken ct) =>
        _db.Plans.AsNoTracking()
            .Where(p => p.CreatedById == userId && p.ClonedFrom == sourceId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);

    // Deep-clone de un jsonb: re-parsea el texto crudo para NO compartir el JsonDocument del origen
    // (evita disposal compartido y acopla la copia al ciclo de vida de la nueva entidad).
    private static JsonDocument? CloneJson(JsonDocument? doc) =>
        doc is null ? null : JsonDocument.Parse(doc.RootElement.GetRawText());

    [HttpGet("mine")]
    [Authorize]
    public async Task<IActionResult> GetMyPlans(CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

        var plans = await _db.Plans.AsNoTracking()
            .Where(p => p.CreatedById == userId.Value)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(50)
            .ToListAsync(ct);

        var lang = _lang.Language;
        return Ok(new PlansListResponse(
            plans.Select(p => PlanDto.FromEntity(p, lang)).ToList(),
            plans.Count
        ));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlans(
        [FromQuery] string? city,
        [FromQuery] string? type,
        [FromQuery] bool showcase = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var isAuthenticated = User.Identity?.IsAuthenticated ?? false;

        // Social S1 (MINOR a): el listado público filtra por la FUENTE DE VERDAD visibility=='public',
        // no por el espejo is_public. Un plan 'unlisted' (compartido por enlace) JAMÁS aparece en
        // listados públicos: solo se resuelve por su token en GET /plans/shared/{token}.
        var query = _db.Plans.AsNoTracking().Where(p => p.Visibility == "public");

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(type))
            query = query.Where(p => p.Type == type);

        // Unauthenticated users only see showcase plans
        if (!isAuthenticated || showcase)
            query = query.Where(p => p.IsShowcase);

        var total = await query.CountAsync(ct);

        var plans = await query
            .OrderBy(p => p.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var lang = _lang.Language;
        return Ok(new PlansListResponse(
            plans.Select(p => PlanDto.FromEntity(p, lang)).ToList(),
            total
        ));
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlan(Guid id, CancellationToken ct)
    {
        Guid? userId = await User.GetUserIdAsync(_db, ct);

        // Autorizacion centralizada (S0). Un GET anonimo por GUID solo resuelve visibility='public'
        // (owner/colaborador tambien pueden ver); 'unlisted' NO se resuelve por este camino.
        var access = await _access.GetAccessAsync(id, userId, ct);
        if (!access.CanView)
        {
            if (access.PlanExists)
                _logger.LogWarning("User {UserId} attempted to access non-viewable plan {PlanId}", userId, id);
            return NotFound(new { error = "Plan not found" });
        }

        var plan = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        if (userId.HasValue)
        {
            _ = _posthog.CaptureAsync(userId.Value.ToString(), "plan_opened", new()
            {
                ["plan_id"] = id.ToString(),
                ["city"] = plan.City,
                ["plan_type"] = plan.Type,
            });
        }

        await _db.PlanMetrics
            .Where(m => m.PlanId == id && !m.WasOpened)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.WasOpened, true)
                .SetProperty(m => m.OpenedAt, DateTimeOffset.UtcNow), ct);

        var routeSegments = await _routeResolver.ResolveAsync(plan.Stops, RoutingMode.Walking, ct);
        return Ok(PlanDetailDto.FromEntity(plan, _lang.Language, routeSegments, _config["Api:PublicBaseUrl"]));
    }
}
