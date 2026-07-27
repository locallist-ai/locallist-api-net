using LocalList.API.NET.Features.Import;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Tests unitarios DIRECTOS (sin HTTP) del núcleo de materialización extraído a
/// <see cref="ImportPlanService"/> (F2 T4). Cubren el INVARIANTE NO-LOSS de <c>BuildStops</c>: un
/// place confirmado que el scheduler descartaría por viabilidad NUNCA se pierde — se reconcilia
/// como stop sin horario al final de su día. La red de integración (ImportPlanTests, DB real) sigue
/// cubriendo el flujo end-to-end; estos aíslan la reconciliación de la orquestación.
/// </summary>
public class ImportPlanServiceTests
{
    private static readonly Guid PlanId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static ScheduledStopDto Scheduled(Guid placeId, int day, int order) => new()
    {
        PlaceId = placeId,
        DayNumber = day,
        OrderIndex = order,
        TimeBlock = "afternoon",
        SuggestedArrival = "14:00",
        SuggestedDurationMin = 60,
    };

    [Fact]
    public void BuildStops_DroppedPlace_IsReconciled_WithoutSchedule_AllPlacesPresent()
    {
        var kept = Guid.Parse("11111111-0000-0000-0000-000000000001");
        var dropped1 = Guid.Parse("22222222-0000-0000-0000-000000000001");
        var dropped2 = Guid.Parse("33333333-0000-0000-0000-000000000001");
        var placeIds = new List<Guid> { kept, dropped1, dropped2 };

        // El scheduler solo colocó 'kept'; 'dropped1'/'dropped2' los descartó por viabilidad.
        var schedule = new ScheduleResult();
        schedule.Stops.Add(Scheduled(kept, day: 1, order: 0));

        var stops = ImportPlanService.BuildStops(PlanId, placeIds, schedule, days: 2);

        // Invariante NO-LOSS: los 3 placeIds confirmados están, exactamente una vez.
        Assert.Equal(3, stops.Count);
        Assert.Equal(placeIds.OrderBy(x => x), stops.Select(s => s.PlaceId).OrderBy(x => x));

        // El colocado conserva su horario; los reconciliados van SIN horario (no viables con seguridad).
        Assert.Equal(TimeSpan.Parse("14:00"), stops.Single(s => s.PlaceId == kept).SuggestedArrival);
        Assert.Null(stops.Single(s => s.PlaceId == dropped1).SuggestedArrival);
        Assert.Null(stops.Single(s => s.PlaceId == dropped2).SuggestedArrival);

        // Todos apuntan al plan y reparto round-robin de los descartados por día (2 días).
        Assert.All(stops, s => Assert.Equal(PlanId, s.PlanId));
        var droppedDays = stops.Where(s => s.PlaceId == dropped1 || s.PlaceId == dropped2)
            .Select(s => s.DayNumber).OrderBy(d => d).ToList();
        Assert.Equal(new[] { 1, 2 }, droppedDays);
    }

    [Fact]
    public void BuildStops_EmptySchedule_ReconcilesEveryPlace_NoneLost()
    {
        var placeIds = Enumerable.Range(0, 5)
            .Select(i => Guid.Parse($"44444444-0000-0000-0000-00000000000{i}"))
            .ToList();

        // El walk-clock no colocó ninguno (todos inviables): el plan sigue conteniendo los 5.
        var stops = ImportPlanService.BuildStops(PlanId, placeIds, new ScheduleResult(), days: 2);

        Assert.Equal(5, stops.Count);
        Assert.Equal(placeIds.OrderBy(x => x), stops.Select(s => s.PlaceId).OrderBy(x => x));
        Assert.All(stops, s => Assert.Null(s.SuggestedArrival));
        // order_index arranca en 0 por día (no hay stops previos que continuar).
        Assert.All(stops, s => Assert.True(s.OrderIndex >= 0));
    }
}
