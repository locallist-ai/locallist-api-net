namespace LocalList.API.NET.Features.Auth;

public record SyncUserDto(Guid Id, string Email, string? Name, string? Image, string Tier, string Role);

public record SyncResponse(SyncUserDto User);
