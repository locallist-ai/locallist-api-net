using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace LocalList.API.NET.Shared.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LocalListDbContext>
{
    public LocalListDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LocalListDbContext>();

        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=locallist_dev;Username=postgres;Password=postgres";

        optionsBuilder.UseNpgsql(connectionString, npg => npg.UseVector());
        return new LocalListDbContext(optionsBuilder.Options);
    }
}
