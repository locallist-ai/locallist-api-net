namespace LocalList.API.NET.Features.Waitlist;

public record JoinWaitlistRequest(string Email);

public record JoinWaitlistResponse(string Message, int Position);

public record WaitlistCountResponse(int Count);
