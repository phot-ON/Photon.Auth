namespace Photon.Auth.Records;

public record LoginResponse(User User, string Token);