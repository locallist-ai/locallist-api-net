using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Cities;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Import;

/// <summary>Resultado de materializar un plan importado: creado (con su Id) o places inválidos.</summary>
public enum ImportPlanOutcome { Created, InvalidPlaces }

/// <summary>Salida de <see cref="ImportPlanService.MaterializeAsync"/>. <see cref="PlanId"/> solo es válido si <see cref="Outcome"/> = Created.</summary>
public readonly record struct ImportPlanResult(ImportPlanOutcome Outcome, Guid PlanId)
{
    public static ImportPlanResult InvalidPlaces() => new(ImportPlanOutcome.InvalidPlaces, Guid.Empty);
    public static ImportPlanResult Created(Guid planId) => new(ImportPlanOutcome.Created, planId);
}

/// <summary>
/// F2 T4 — materialización del plan importado, extraída de <see cref="ImportPlanController"/> (el
/// controller quedó en gates + mapping). Encapsula el núcleo NO trivial: validación ATÓMICA/OPACA
/// de los places (todos existen + published + de la ciudad), semilla FNV determinista, scheduling
/// sobre el SET FIJO y la reconciliación NO-LOSS de los places que el walk-clock descartaría por
/// viabilidad. Invariante clave (testeable directamente vía <see cref="BuildStops"/>): el plan
/// contiene SIEMPRE los N placeIds confirmados, ni uno menos.
/// </summary>
public sealed class ImportPlanService
{
    private const string SourceImported = "imported";

    private readonly LocalListDbContext _db;
    private readonly ISchedulingService _scheduler;
    private readonly ILogger<ImportPlanService> _logger;

    public ImportPlanService(
        LocalListDbContext db, ISchedulingService scheduler, ILogger<ImportPlanService> logger)
    {
        _db = db;
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <summary>
    /// Valida los places, agenda y persiste el plan+stops en UNA transacción. Los <paramref name="placeIds"/>
    /// deben venir ya deduplicados y en orden canónico (el controller lo garantiza); el orden se
    /// respeta para hacer el scheduling DETERMINISTA e invariante al orden de envío del cliente.
    /// </summary>
    public async Task<ImportPlanResult> MaterializeAsync(
        Guid userId,
        string city,
        int days,
        List<Guid> placeIds,
        string? importedFromPlatform,
        string? creatorHandle,
        string planName,
        string lang,
        CancellationToken ct)
    {
        // Validación ATÓMICA y OPACA: TODOS deben existir, estar published y ser de la ciudad
        // pedida (comparada normalizada). Si alguno falla → InvalidPlaces SIN decir cuál (evita
        // filtrar el catálogo) y SIN crear nada (aún no hemos tocado la DB).
        var places = await _db.Places
            .Where(p => placeIds.Contains(p.Id))
            .ToListAsync(ct);

        var normalizedCity = CityNameNormalizer.Normalize(city);
        var allValid = places.Count == placeIds.Count
            && places.All(p => p.Status == "published"
                               && CityNameNormalizer.Normalize(p.City) == normalizedCity);
        if (!allValid)
        {
            _logger.LogInformation(
                "ImportPlan: invalid places userId={UserId} requested={Req} found={Found} city={City}",
                userId, placeIds.Count, places.Count, city);
            return ImportPlanResult.InvalidPlaces();
        }

        // Scheduling determinista sobre el SET FIJO. Los places se pasan en el orden CANÓNICO de
        // placeIds: la query de DB no garantiza orden y el scheduler es sensible al orden de entrada,
        // así que fijarlo hace el resultado DETERMINISTA e invariante al orden de envío.
        var placeById = places.ToDictionary(p => p.Id);
        var orderedPlaces = placeIds.Select(id => placeById[id]).ToList();

        var schedPrefs = new ExtractedPreferences
        {
            Days = days,
            MaxStopsPerDay = PlanLimits.MaxStopsPerDay,
            GroupType = "couple", // neutro (no-family → sin filtro de nightlife); el import no aporta grupo
        };
        var seed = ComputeSeed(placeIds, normalizedCity, days);
        var schedule = await _scheduler.BuildPlanScheduleAsync(orderedPlaces, schedPrefs, seed, ct);

        var now = DateTimeOffset.UtcNow;
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = planName,
            City = orderedPlaces[0].City, // ortografía canónica del catálogo (todos comparten ciudad)
            Type = "custom",
            Source = SourceImported,   // NUNCA curated/showcase
            Description = null,
            DurationDays = days,
            Visibility = "private",    // default S0
            IsShowcase = false,
            CreatedById = userId,
            CreatedAt = now,
            UpdatedAt = now,
            NameI18n = LanguageAccessor.SetI18nString(null, lang, planName),
            ImportedFromPlatform = importedFromPlatform,
            ImportedCreatorHandle = creatorHandle,
        };
        _db.Plans.Add(plan);

        // Stops: primero los que el scheduler colocó, luego RECONCILIA los que descartó por
        // viabilidad como stops sin horario al final de su día — no perder NUNCA un place confirmado.
        var stops = BuildStops(plan.Id, placeIds, schedule, days);
        _db.PlanStops.AddRange(stops);

        // Plan + stops en UN SaveChanges = una transacción → atómico.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrorPredicates.IsForeignKeyViolation(ex))
        {
            // TOCTOU: hard-delete (admin) de un place entre el SELECT de validación y el INSERT
            // de los stops → 23503 del FK plan_stops→places. Mismo InvalidPlaces opaco que si el
            // place nunca hubiera sido válido — nunca un 500. La transacción se revierte entera.
            _logger.LogInformation(
                "ImportPlan: place hard-deleted mid-request userId={UserId} city={City}", userId, city);
            return ImportPlanResult.InvalidPlaces();
        }

        _logger.LogInformation(
            "ImportPlan: created plan={PlanId} userId={UserId} city={City} days={Days} places={N} platform={Platform}",
            plan.Id, userId, plan.City, days, placeIds.Count, importedFromPlatform ?? "self");

        return ImportPlanResult.Created(plan.Id);
    }

    // ── Stops: schedule + reconcile ──────────────────────────────────────────────

    /// <summary>
    /// Convierte el resultado del scheduler en <see cref="PlanStop"/> y RECONCILIA los places
    /// confirmados que el walk-clock descartó (cerrado/hueco/leg/tope): se añaden sin horario al
    /// final de su día, repartidos round-robin. Determinista dado el orden estable de placeIds.
    /// Internal para el test unitario directo del invariante no-loss (sin HTTP).
    /// </summary>
    internal static List<PlanStop> BuildStops(
        Guid planId, List<Guid> placeIds, ScheduleResult schedule, int days)
    {
        var stops = schedule.Stops
            .Select(sd => new PlanStop
            {
                Id = Guid.NewGuid(),
                PlanId = planId,
                PlaceId = sd.PlaceId,
                DayNumber = sd.DayNumber,
                OrderIndex = sd.OrderIndex,
                TimeBlock = sd.TimeBlock,
                SuggestedArrival = string.IsNullOrEmpty(sd.SuggestedArrival) ? null : TimeSpan.Parse(sd.SuggestedArrival),
                SuggestedDurationMin = sd.SuggestedDurationMin,
                TravelFromPrevious = sd.TravelFromPrevious is null
                    ? null
                    : System.Text.Json.JsonSerializer.SerializeToDocument(sd.TravelFromPrevious),
            })
            .ToList();

        // Reconciliar los descartados: siguiente order_index por día tras los ya colocados.
        var scheduled = stops.Select(s => s.PlaceId).ToHashSet();
        var nextOrder = new Dictionary<int, int>();
        foreach (var s in stops)
            nextOrder[s.DayNumber] = Math.Max(nextOrder.GetValueOrDefault(s.DayNumber, -1), s.OrderIndex);

        var dropped = placeIds.Where(id => !scheduled.Contains(id)).ToList();
        for (int i = 0; i < dropped.Count; i++)
        {
            var day = (i % days) + 1; // reparto round-robin estable
            var order = nextOrder.GetValueOrDefault(day, -1) + 1;
            nextOrder[day] = order;
            stops.Add(new PlanStop
            {
                Id = Guid.NewGuid(),
                PlanId = planId,
                PlaceId = dropped[i],
                DayNumber = day,
                OrderIndex = order,
                // Sin horario/travel: no es viable con seguridad, pero no se pierde (contrato de manual).
            });
        }

        return stops;
    }

    /// <summary>
    /// Semilla estable (FNV-1a 32-bit) del request: mismos placeIds (ordenados) + ciudad + días →
    /// misma semilla → mismo scheduling. Ordena los ids para que el orden de confirmación no altere
    /// el resultado. Estable entre procesos (a diferencia de string.GetHashCode).
    /// </summary>
    private static int ComputeSeed(List<Guid> placeIds, string normalizedCity, int days)
    {
        var canonical = string.Join("|",
            days.ToString(),
            normalizedCity,
            string.Join(",", placeIds.OrderBy(g => g).Select(g => g.ToString("N"))));
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in canonical)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & int.MaxValue);
        }
    }
}
