using Microsoft.EntityFrameworkCore;
using Paparr.API.Data;

namespace Paparr.API.Services;

internal static class LibraryOperationAdmission
{
    private const long LockKey = 0x504150415252;

    public static Task LockAsync(AppDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);
}
