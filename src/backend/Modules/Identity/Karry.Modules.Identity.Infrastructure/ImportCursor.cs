using Karry.Modules.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

internal sealed class ImportCursor(IdentityDbContext db) : IImportCursor
{
    public async Task<DateTimeOffset?> GetAsync(string source, CancellationToken cancellationToken) =>
        (await db.ImportCursors.AsNoTracking().SingleOrDefaultAsync(c => c.Source == source, cancellationToken).ConfigureAwait(false))?.Position;

    public async Task SetAsync(string source, DateTimeOffset position, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO identity.import_cursors (source, position) VALUES ({source}, {position})
            ON CONFLICT (source) DO UPDATE SET position = GREATEST(identity.import_cursors.position, EXCLUDED.position)
            """, cancellationToken).ConfigureAwait(false);
}
