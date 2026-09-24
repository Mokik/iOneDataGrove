using iOneDataGrove.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed class ImportExecutionLock : IAsyncDisposable
{
    private readonly IOneDataGroveDbContext dbContext;
    private bool acquired;

    private ImportExecutionLock(IOneDataGroveDbContext dbContext)
    {
        this.dbContext = dbContext;
        acquired = true;
    }

    public static async Task<ImportExecutionLock?> TryAcquireAsync(
        IOneDataGroveDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@lock_key);";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "lock_key";
            parameter.Value = ImportAdvisoryLock.ApplicationLockKey;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is true)
            {
                return new ImportExecutionLock(dbContext);
            }

            await dbContext.Database.CloseConnectionAsync();
            return null;
        }
        catch
        {
            await dbContext.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!acquired)
        {
            return;
        }

        acquired = false;
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "lock_key";
            parameter.Value = ImportAdvisoryLock.ApplicationLockKey;
            command.Parameters.Add(parameter);
            await command.ExecuteScalarAsync();
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }
}
