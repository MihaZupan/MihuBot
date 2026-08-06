using Microsoft.EntityFrameworkCore;
using MihuBot.Molly;

namespace MihuBot.DB;

public sealed class MollyDbContext : DbContext
{
    public MollyDbContext(DbContextOptions<MollyDbContext> options) : base(options)
    { }

    public DbSet<MollyDbEntry> Entries { get; set; }

    public DbSet<MollyAlertDbEntry> Alerts { get; set; }
}
