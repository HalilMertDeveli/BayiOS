using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Imports.Infrastructure.Files;

/// <summary>
/// Raw upload storage. The MVP has no hosted object storage yet, so <see cref="DatabaseImportFileStore"/> keeps the
/// (≤ 20 MB) bytes in <c>imports.import_files</c> (bytea, RLS) on the request/job transaction. A Supabase Storage
/// implementation (private bucket, path <c>{tenant}/{import}</c>) can replace it without touching callers.
/// </summary>
internal interface IImportFileStore
{
    Task SaveAsync(Guid tenantId, Guid importJobId, byte[] content, CancellationToken cancellationToken);

    /// <returns>The bytes, or null when the file is gone (deleted after retention).</returns>
    Task<byte[]?> ReadAsync(Guid importJobId, CancellationToken cancellationToken);
}

internal sealed class DatabaseImportFileStore(ImportsDbContext db, TimeProvider clock) : IImportFileStore
{
    public async Task SaveAsync(Guid tenantId, Guid importJobId, byte[] content, CancellationToken cancellationToken)
    {
        db.Files.Add(ImportFile.Create(tenantId, importJobId, content, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<byte[]?> ReadAsync(Guid importJobId, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        return await db.Files.AsNoTracking()
            .Where(f => f.ImportJobId == importJobId)
            .Select(f => f.Content)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
