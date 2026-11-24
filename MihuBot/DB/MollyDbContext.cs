using Microsoft.EntityFrameworkCore;
using MihuBot.Molly;
using MihuBot.Molly.Sharing;

namespace MihuBot.DB;

public sealed class MollyDbContext : DbContext
{
    public MollyDbContext(DbContextOptions<MollyDbContext> options) : base(options)
    { }

    public DbSet<MollyDbEntry> Entries { get; set; }

    public DbSet<MollyAlertDbEntry> Alerts { get; set; }

    public DbSet<MollyShareDbEntry> ShareEntries { get; set; }

    public DbSet<MollyShareTrusteeDbEntry> ShareTrustees { get; set; }

    public DbSet<MollyShareUpdateDbEntry> ShareUpdates { get; set; }
}
