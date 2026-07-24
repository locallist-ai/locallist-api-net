using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

// Small shared pure helpers used across the other partials: time-block derivation, per-place
// visit duration, once-only warning append, Haversine distance and travel-time estimation.
// Logic is identical to the original single-file version; only its location changed.
public partial class SchedulingService
{
    private static string DeriveTimeBlock(TimeSpan clock)
    {
        double h = clock.TotalHours;
        if (h < 11)   return "morning";
        if (h < 14.5) return "lunch";
        if (h < 17.5) return "afternoon";
        if (h < 21)   return "dinner";
        return "evening";
    }

    private static int VisitDurationFor(Place p) =>
        p.VisitDurationMin ?? CategoryDurationMin.GetValueOrDefault(p.Category, DefaultDurationMin);

    private static void AddWarningOnce(List<string> warnings, string w)
    {
        if (!warnings.Contains(w)) warnings.Add(w);
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R  = 6371;
        var dLat        = ToRad(lat2 - lat1);
        var dLon        = ToRad(lon2 - lon1);
        var a           = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                          Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                          Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c           = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRad(double degrees) => degrees * (Math.PI / 180);

    private static int EstimateTravelTime(double distanceKm, string mode)
    {
        var speedKmH = mode == "walk" ? 5.0 : 30.0;
        var timeHours = distanceKm / speedKmH;
        return (int)Math.Max(5, Math.Round(timeHours * 60));
    }
}
