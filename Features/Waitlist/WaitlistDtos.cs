namespace LocalList.API.NET.Features.Waitlist;

public record JoinWaitlistRequest(
    string Email,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmContent = null);

public record JoinWaitlistResponse(string Message, int Position);

public record WaitlistCountResponse(int Count);
