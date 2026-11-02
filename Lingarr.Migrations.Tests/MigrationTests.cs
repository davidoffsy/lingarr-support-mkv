using FluentMigrator.Runner;
using Lingarr.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Lingarr.Migrations.Tests;

[Trait("Category", "Integration")]
public class MigrationTests
{
    private static void RunMigrations(string connectionString, string dbType)
    {
        var services = new ServiceCollection();
        services.AddFluentMigrator(connectionString, dbType);

        var serviceProvider = services.BuildServiceProvider();
        MigrationConfiguration.RunMigrations(serviceProvider);
    }

    [Fact]
    public async Task Sqlite_MigrationsRunSuccessfully()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            RunMigrations(connectionString, "sqlite");

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = 'ai_thinking'";
            Assert.Equal("auto", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "SELECT value FROM settings WHERE key = 'ai_max_tokens'";
            Assert.Equal("0", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "SELECT value FROM settings WHERE key = 'export_bilingual_ass'";
            Assert.Equal("false", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "SELECT value FROM settings WHERE key = 'strip_trailing_ideographic_full_stop'";
            Assert.Equal("false", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "SELECT value FROM settings WHERE key = 'replace_inline_ideographic_full_stop'";
            Assert.Equal("false", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_CustomProviderSettingsAreSeeded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            RunMigrations(connectionString, "sqlite");

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var command = connection.CreateCommand();
            foreach (var key in new[]
                     {
                         "custom_endpoint", "custom_model", "custom_api_key", "custom_chat_request_template",
                         "custom_responses_request_template", "custom_messages_request_template"
                     })
            {
                command.CommandText = $"SELECT value FROM settings WHERE key = '{key}'";
                Assert.Equal(string.Empty, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            // The protocol keeps an explicit default so existing installations stay on Chat Completions.
            command.CommandText = "SELECT value FROM settings WHERE key = 'custom_protocol'";
            Assert.Equal("openai_chat", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "SELECT value FROM settings WHERE key = 'custom_anthropic_version'";
            Assert.Equal("2023-06-01", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));

            command.CommandText =
                "SELECT type FROM pragma_table_info('translation_request_lines') WHERE name = 'model'";
            Assert.Equal(
                "TEXT",
                Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_MigrationsRollBackSuccessfully()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddFluentMigrator(connectionString, "sqlite");

            var serviceProvider = services.BuildServiceProvider();
            MigrationConfiguration.RunMigrations(serviceProvider);

            using var scope = serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(7);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);

            // M0032 columns and M0031/M0033 keys must be gone, not just the version records.
            Assert.False(await ColumnExistsAsync(connection, "translation_request_lines", "model"));
            Assert.False(await ColumnExistsAsync(connection, "statistics", "lines_by_source_json"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_endpoint"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_protocol"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_responses_request_template"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_messages_request_template"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_anthropic_version"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_CustomProviderSettingsSeedSkipsExistingKeys()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddFluentMigrator(connectionString, "sqlite");

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

            // Apply everything before M0031, then simulate a database that already carries one of the
            // seeded keys with a value of its own.
            runner.MigrateUp(30);
            await using (var seedConnection = new SqliteConnection(connectionString))
            {
                await seedConnection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = seedConnection.CreateCommand();
                command.CommandText =
                    "INSERT INTO settings (key, value) VALUES ('custom_endpoint', 'http://hand-edited'), ('custom_protocol', 'openai_responses')";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // Must not throw: settings.key is a primary key, a guarded seed skips the existing row.
            runner.MigrateUp();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var valueCommand = connection.CreateCommand();
            valueCommand.CommandText = "SELECT value FROM settings WHERE key = 'custom_endpoint'";
            Assert.Equal("http://hand-edited",
                await valueCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1L, await CountSettingsAsync(connection, "custom_endpoint"));

            // A hand-edited protocol survives the new migration too.
            valueCommand.CommandText = "SELECT value FROM settings WHERE key = 'custom_protocol'";
            Assert.Equal("openai_responses",
                await valueCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1L, await CountSettingsAsync(connection, "custom_protocol"));
            valueCommand.CommandText = "SELECT value FROM settings WHERE key = 'custom_anthropic_version'";
            Assert.Equal("2023-06-01",
                await valueCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));

            foreach (var key in new[]
                     {
                         "custom_model", "custom_api_key", "custom_chat_request_template",
                         "custom_responses_request_template", "custom_messages_request_template"
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT value FROM settings WHERE key = '{key}'";
                Assert.Equal(string.Empty, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_CustomProviderSettingsDownKeepsPreexistingKeys()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddFluentMigrator(connectionString, "sqlite");

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

            runner.MigrateUp(30);
            await using (var seedConnection = new SqliteConnection(connectionString))
            {
                await seedConnection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = seedConnection.CreateCommand();
                command.CommandText =
                    "INSERT INTO settings (key, value) VALUES ('custom_endpoint', 'http://hand-edited'), ('custom_protocol', 'openai_responses')";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            runner.MigrateUp();
            runner.MigrateDown(30);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var valueCommand = connection.CreateCommand();
            valueCommand.CommandText = "SELECT value FROM settings WHERE key = 'custom_endpoint'";
            Assert.Equal("http://hand-edited",
                await valueCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            valueCommand.CommandText = "SELECT value FROM settings WHERE key = 'custom_protocol'";
            Assert.Equal("openai_responses",
                await valueCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));

            // Keys this migration actually inserted (empty defaults) are removed.
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_model"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_api_key"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_chat_request_template"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_responses_request_template"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_messages_request_template"));
            Assert.Equal(0L, await CountSettingsAsync(connection, "custom_anthropic_version"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0;
    }

    private static async Task<long> CountSettingsAsync(SqliteConnection connection, string key)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM settings WHERE key = '{key}'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sqlite_MigrationsReapplyAfterRollback()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddFluentMigrator(connectionString, "sqlite");

            var serviceProvider = services.BuildServiceProvider();
            MigrationConfiguration.RunMigrations(serviceProvider);

            using var scope = serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateDown(2);
            runner.MigrateUp();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM settings WHERE key = 'navigate_to_details_on_request'";
            Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task MySql_MigrationsRunSuccessfully()
    {
        await using var container = new MySqlBuilder("mysql:latest")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        var connectionString = container.GetConnectionString();
        RunMigrations(connectionString, "mysql");

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

        await using var command = new MySqlCommand(
            "SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'translation_request_lines' AND COLUMN_NAME = 'model'",
            connection);
        Assert.Equal("text", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Postgres_MigrationsRunSuccessfully()
    {
        await using var container = new PostgreSqlBuilder("postgres:latest")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        var connectionString = container.GetConnectionString();
        RunMigrations(connectionString, "postgres");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

        await using var command = new NpgsqlCommand(
            "SELECT data_type FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'translation_request_lines' AND column_name = 'model'",
            connection);
        Assert.Equal("text", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
