using Microsoft.EntityFrameworkCore;
using MihuBot.Storage;

namespace MihuBot.DB;

public sealed class StorageDbContext : DbContext
{
    public StorageDbContext(DbContextOptions<StorageDbContext> options) : base(options)
    { }

    public DbSet<ContainerDbEntry> Containers { get; set; }

    public DbSet<FileDbEntry> Files { get; set; }
}
