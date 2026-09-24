using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace iOneDataGrove.Persistence.Data;

public sealed class IOneDataGroveDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<IOneDataGroveDbContext>
{
    public IOneDataGroveDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__iOneDataGrove")
            ?? "Host=localhost;Port=5432;Database=ionedatagrove;" +
               "Username=ionedatagrove_app;Password=design-time-only";

        var options = new DbContextOptionsBuilder<IOneDataGroveDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new IOneDataGroveDbContext(options);
    }
}
