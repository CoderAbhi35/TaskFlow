using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Persistence;

namespace Reeve.UnitTests.Infrastructure;

public class PersistenceConfigurationTests
{
    [Theory]
    [InlineData(null, "Host=db;Database=reeve", 100)]
    [InlineData(40, "Host=db;Database=reeve", 40)]
    [InlineData(40, "Host=db;Database=reeve;Maximum Pool Size=7", 7)]
    public void The_pool_limit_comes_from_configuration_unless_the_connection_string_sets_one(
        int? configured, string connectionString, int expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Reeve"] = connectionString,
            [DependencyInjection.MaxPoolSizeKey] = configured?.ToString(),
        }).Build();
        using var services = new ServiceCollection().AddReevePersistence(configuration).BuildServiceProvider();
        using var scope = services.CreateScope();

        var used = scope.ServiceProvider.GetRequiredService<ReeveDbContext>().Database.GetConnectionString();

        new NpgsqlConnectionStringBuilder(used).MaxPoolSize.Should().Be(expected);
    }
}
