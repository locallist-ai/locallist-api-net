using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LocalList.API.NET.Shared.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LocalListDbContext>
{
    public LocalListDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LocalListDbContext>();

        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=locallist_dev;Username=postgres;Password=postgres";

        optionsBuilder.UseNpgsql(connectionString);

        return new LocalListDbContext(optionsBuilder.Options);
    }
}
