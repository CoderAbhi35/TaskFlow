using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Reeve.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Reads REEVE_CONNECTION_STRING, falling back to the
/// docker-compose development database.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReeveDbContext>
{
    private const string LocalDevConnectionString =
        "Host=localhost;Port=5432;Database=reeve;Username=reeve;Password=reeve";

    public ReeveDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("REEVE_CONNECTION_STRING") ?? LocalDevConnectionString;

        var options = new DbContextOptionsBuilder<ReeveDbContext>();
        DependencyInjection.ConfigureDbContext(options, connectionString);
        return new ReeveDbContext(options.Options);
    }
}
