namespace LocalList.API.NET.Features.Waitlist;

public interface IEmailMarketingService
{
    Task AddToWaitlistAsync(string email, Dictionary<string, string>? utmData = null, CancellationToken ct = default);
}
