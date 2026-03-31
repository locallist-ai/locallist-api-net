using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LocalList.API.NET.Shared.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LocalListDbContext>
{
    public LocalListDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LocalListDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=locallist_dev;Username=postgres;Password=postgres");
        return new LocalListDbContext(optionsBuilder.Options);
    }
}
