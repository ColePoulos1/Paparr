namespace Paparr.API.Models;

public sealed record StagingStatusDto(int ActiveAttempts, int PendingMaintenance, int Retained, int Settled);
