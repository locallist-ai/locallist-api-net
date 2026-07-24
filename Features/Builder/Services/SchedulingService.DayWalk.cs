using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Routing;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

// The viability core: per-day orchestration (Step 3), parallel segment prefetch, and the
// clock walk that gates every candidate by opening hours (C1/M2/M4), a hard day cap (M3),
// a dead-gap tolerance and a per-leg travel ceiling (m8). Plus travel resolution. Logic is
// identical to the original single-file version; only its location changed.
public partial class SchedulingService
{
    // ── Step 3: schedule one day ──────────────────────────────────────────────

    private async Task ScheduleDayAsync(List<Place> places, ScheduleResult result, int day, DayOfWeek? weekday, Random rng, CancellationToken ct)
    {
        var ordered = OrderByGeography(places, rng);
        AnchorMeals(ordered);       // position food near meal windows first
        AnchorNightlife(ordered);   // then push nightlife to end (always last)

        // Pre-fetch all consecutive travel segments in parallel before the sequential clock walk.
        // Avoids up to N-1 sequential Mapbox calls on cold cache; DB cache writes are idempotent
        // (ON CONFLICT DO NOTHING), so concurrent pre-fetches across requests are safe.
        var prefetched = await PrefetchDaySegmentsAsync(ordered, ct);
        await WalkDayClockAsync(ordered, result, day, weekday, prefetched, ct);
    }

    // Pre-fetches route segments for all consecutive place pairs in parallel (concurrency cap 4,
    // matching the batch resolver in RouteResolver.FetchAndPersistAsync).
    // Returns a dict keyed by (fromId, toId); entries that fail resolver fall back to Haversine
    // inside ResolveTravelAsync and are still stored so WalkDayClockAsync gets a dict hit.
    private async Task<Dictionary<(Guid, Guid), TravelInfoDto>> PrefetchDaySegmentsAsync(
        List<Place> ordered, CancellationToken ct)
    {
        if (_resolver == null || ordered.Count < 2) return [];

        var pairs = new List<(Place from, Place to, double dist, string mode)>(ordered.Count - 1);
        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];
            if (prev.Latitude.HasValue && prev.Longitude.HasValue &&
                curr.Latitude.HasValue && curr.Longitude.HasValue)
            {
                double dist = Haversine(
                    (double)prev.Latitude.Value, (double)prev.Longitude.Value,
                    (double)curr.Latitude.Value, (double)curr.Longitude.Value);
                pairs.Add((prev, curr, dist, dist < 2 ? "walk" : "drive"));
            }
        }

        if (pairs.Count == 0) return [];

        using var semaphore = new SemaphoreSlim(4);
        var tasks = pairs.Select(async pair =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var travel = await ResolveTravelAsync(pair.from, pair.to, pair.dist, pair.mode, ct);
                return (key: (pair.from.Id, pair.to.Id), travel);
            }
            finally { semaphore.Release(); }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.key, r => r.travel);
    }

    // ── Clock walk ────────────────────────────────────────────────────────────

    // Walks the day clock over the geographically-ordered candidates, gating each by
    // opening hours (day-aware when <paramref name="weekday"/> is known), a hard day cap,
    // a dead-gap tolerance and a per-leg travel ceiling. This is the viability core:
    // it must never emit a stop at a place that is closed on this weekday, nor a visit
    // that runs past its closing time.
    private async Task WalkDayClockAsync(
        List<Place> places, ScheduleResult result, int day, DayOfWeek? weekday,
        Dictionary<(Guid, Guid), TravelInfoDto> prefetched,
        CancellationToken ct)
    {
        var clock       = DayStart;
        bool overpacked = false;
        int orderIndex  = 0;

        for (int i = 0; i < places.Count; i++)
        {
            var place    = places[i];
            int duration = VisitDurationFor(place);

            // Travel from the LAST EMITTED stop (never from a skipped candidate). Computed
            // before the gate but NOT committed to the clock until the stop is actually emitted,
            // so a skip leaves the clock in place for the next candidate.
            TravelInfoDto? travelInfo = null;
            if (orderIndex > 0)
            {
                var prevStop  = result.Stops.LastOrDefault(s => s.DayNumber == day);
                var prevPlace = prevStop is not null
                    ? places.FirstOrDefault(p => p.Id == prevStop.PlaceId)
                    : null;

                if (prevPlace?.Latitude.HasValue == true && prevPlace.Longitude.HasValue &&
                    place.Latitude.HasValue && place.Longitude.HasValue)
                {
                    double dist = Haversine(
                        (double)prevPlace.Latitude.Value, (double)prevPlace.Longitude.Value,
                        (double)place.Latitude.Value, (double)place.Longitude.Value);
                    string mode = dist < 2 ? "walk" : "drive";

                    // Use pre-fetched result for consecutive pairs (the common case). Falls back
                    // to a live resolver call only when a prior stop was skipped, producing a
                    // non-consecutive (last-emitted → current) pair not present in the dict.
                    if (!prefetched.TryGetValue((prevPlace.Id, place.Id), out travelInfo))
                        travelInfo = await ResolveTravelAsync(prevPlace, place, dist, mode, ct);
                }
            }

            // m8: per-leg travel ceiling. A single leg longer than MaxLegTravelMin means the
            // candidate is geographically implausible — skip WITHOUT advancing the clock. The
            // next candidate re-resolves travel from the same last-emitted stop.
            if (travelInfo is not null && travelInfo.duration_min > MaxLegTravelMin)
            {
                AddWarningOnce(result.Warnings, "leg_too_far");
                _logger.LogInformation(
                    "Builder: schedule day={Day} place={Place} skipped (leg_too_far {Min}min > {Cap}min)",
                    day, place.Name, travelInfo.duration_min, MaxLegTravelMin);
                continue;
            }

            // Tentative arrival = current clock + travel. Only committed on emit.
            var arrival = clock + TimeSpan.FromMinutes(travelInfo?.duration_min ?? 0);

            // ── Opening-hours gate ────────────────────────────────────────────────
            var openingHours = OpeningHoursData.FromJsonDocument(place.OpeningHours);
            if (openingHours is null)
            {
                // m6: absent/malformed hours are NOT treated as closed (missing data ≠ closed),
                // so no gate is applied — but the plan flags that some stops were scheduled
                // without hour validation.
                AddWarningOnce(result.Warnings, "no_hours_data");
            }
            else if (weekday is DayOfWeek wd)
            {
                // Day-aware gate (C1 + M2 + M4). NextFitAt only sees THIS weekday's own windows and
                // requires the full visit to fit before close (M2); a same-day cross-midnight window
                // (Open.Day == wd, e.g. Sat 22:00 to Sun 02:00) is handled by it. IsOpenAt is
                // additionally tail-aware: it also reports "open right now" for a PREVIOUS day's
                // cross-midnight tail (Fri 22:00 to Sat 02:00 seen at Sat 01:00) that NextFitAt does
                // not see. Such a pure tail therefore fails the openNow && fit == arrival guard (fit
                // is null) and is treated as closed — not a viability violation, just a false
                // negative, and unreachable in practice since the walk clock starts at DayStart
                // (09:30) and only advances, so a pre-dawn arrival never occurs.
                var fit      = openingHours.NextFitAt(wd, arrival, duration);
                bool openNow = openingHours.IsOpenAt(wd, arrival);

                if (openNow && fit == arrival)
                {
                    // Open now and the full visit fits before this window closes → schedule as-is.
                }
                else if (fit is null)
                {
                    // Closed this weekday, no window at all, or the visit runs past close (M2).
                    AddWarningOnce(result.Warnings, "place_closed_skipped");
                    _logger.LogInformation(
                        "Builder: schedule day={Day} weekday={Wd} place={Place} skipped (closed or does-not-fit)",
                        day, wd, place.Name);
                    continue;
                }
                else if (fit.Value - arrival > MaxWaitForOpen)
                {
                    // A fitting window exists but the wait exceeds the dead-gap tolerance (M4).
                    // Skip WITHOUT advancing the clock — no huge empty gaps in the itinerary.
                    AddWarningOnce(result.Warnings, "dead_gap_skipped");
                    _logger.LogInformation(
                        "Builder: schedule day={Day} weekday={Wd} place={Place} skipped (dead gap {Gap}min > {Cap}min)",
                        day, wd, place.Name, (int)(fit.Value - arrival).TotalMinutes, (int)MaxWaitForOpen.TotalMinutes);
                    continue;
                }
                else
                {
                    // Wait until the next fitting window (within tolerance). Real travel still
                    // happened, so travelInfo is kept.
                    arrival = fit.Value;
                }
            }
            else
            {
                // Legacy day-agnostic fallback (StartDate == null / old clients): accept a window
                // on ANY weekday. C1 (closed THIS weekday) is inapplicable without a date and stays
                // that way by design, but M2 (the visit must fit before close) is day-independent —
                // so the fit-before-close check is enforced here too, mirroring the day-aware gate.
                // The invariant "impossible to schedule a visit that does not fit before close" is
                // therefore UNCONDITIONAL, with or without a trip date.
                var fit      = openingHours.NextFitAt(arrival, duration);
                bool openNow = openingHours.IsOpenAt(arrival);

                if (openNow && fit == arrival)
                {
                    // Open now and the full visit fits before this window closes → schedule as-is.
                }
                else if (fit is null)
                {
                    // No window on any weekday admits the full visit before close (M2), or the place
                    // never opens later. Same skip semantics as the day-aware path.
                    AddWarningOnce(result.Warnings, "place_closed_skipped");
                    _logger.LogInformation(
                        "Builder: schedule day={Day} place={Place} skipped (closed or does-not-fit)",
                        day, place.Name);
                    continue;
                }
                else if (fit.Value - arrival > MaxWaitForOpen)
                {
                    // A fitting window exists but the wait exceeds the dead-gap tolerance (M4).
                    // Skip WITHOUT advancing the clock — no huge empty gaps in the itinerary.
                    AddWarningOnce(result.Warnings, "dead_gap_skipped");
                    _logger.LogInformation(
                        "Builder: schedule day={Day} place={Place} skipped (dead gap {Gap}min > {Cap}min)",
                        day, place.Name, (int)(fit.Value - arrival).TotalMinutes, (int)MaxWaitForOpen.TotalMinutes);
                    continue;
                }
                else
                {
                    // Wait until the next fitting window (within tolerance).
                    arrival    = fit.Value;
                    travelInfo = null; // gap absorbed by the opening wait (legacy semantics)
                }
            }

            // ── M3: hard day cap ──────────────────────────────────────────────────
            // Any arrival past the cap is dropped, but we keep evaluating the rest of the day
            // instead of breaking: the clock is NOT advanced on a skip, and arrival =
            // last_emitted_clock + travel(prev -> candidate), so a LATER candidate with a shorter
            // leg can have a smaller arrival that still fits before the cap. A hard break here
            // (wrongly assuming a monotonic clock across candidates) would leave viable stops —
            // even a whole day — dropped. Nightlife gets a later cap since it is anchored last.
            // Replaces the old "clamp any >=23h to 23:59".
            var hardCap = IsNightlife(place) ? NightlifeHardCap : DayHardCap;
            if (arrival > hardCap)
            {
                AddWarningOnce(result.Warnings, "day_truncated");
                _logger.LogInformation(
                    "Builder: schedule day={Day} place={Place} dropped, arrival {Arrival} past hard cap {Cap}",
                    day, place.Name, arrival, hardCap);
                continue;
            }

            if (!overpacked && arrival > DaySoftCap)
            {
                AddWarningOnce(result.Warnings, "day_overpacked");
                overpacked = true;
            }

            // arrival ≤ hardCap ≤ 23:59 < 24h, so "hh" is safe (no day component).
            string arrivalStr = arrival.ToString(@"hh\:mm");

            _logger.LogInformation(
                "Builder: schedule day={Day} stop={I} place={Place} arrival={Arrival} block={Block} dur={Dur}",
                day, orderIndex, place.Name, arrivalStr, DeriveTimeBlock(arrival), duration);

            result.Stops.Add(new ScheduledStopDto
            {
                PlaceId              = place.Id,
                DayNumber            = day,
                OrderIndex           = orderIndex,
                TimeBlock            = DeriveTimeBlock(arrival),
                SuggestedArrival     = arrivalStr,
                SuggestedDurationMin = duration,
                TravelFromPrevious   = travelInfo
            });

            clock = arrival + TimeSpan.FromMinutes(duration);
            orderIndex++;
        }
    }

    private static bool IsNightlife(Place p) =>
        p.Category.Equals("nightlife", StringComparison.OrdinalIgnoreCase);

    private async Task<TravelInfoDto> ResolveTravelAsync(Place from, Place to, double dist, string mode, CancellationToken ct)
    {
        if (_resolver != null)
        {
            try
            {
                var routeMode = mode == "walk" ? RoutingMode.Walking : RoutingMode.Driving;
                var segment = await _resolver.ResolveSegmentAsync(from, to, routeMode, ct);
                if (segment != null)
                    return new TravelInfoDto
                    {
                        distance_km  = Math.Round(segment.DistanceMeters / 1000.0, 1),
                        duration_min = Math.Max(1, (int)Math.Ceiling(segment.DurationSeconds / 60.0)),
                        mode         = mode
                    };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Catches non-OCE errors (HttpRequestException, DB failures…) and OCE from
                // internal timeouts. Does NOT catch OCE when ct is cancelled (user cancelled).
                _logger.LogWarning(ex, "Routing failed for segment {From}→{To} — using Haversine estimate",
                    from.Name, to.Name);
            }
        }

        return new TravelInfoDto
        {
            distance_km  = Math.Round(dist, 1),
            duration_min = EstimateTravelTime(dist, mode),
            mode         = mode
        };
    }
}
