using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;

namespace PayApp.Core.Configuration;

public static class Settings
{
    public static string Required(this IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing setting '{key}'. Add it to your .env file.");

    public static string Optional(this IConfiguration config, string key, string fallback) =>
        config[key] is { Length: > 0 } value ? value : fallback;

    public static string PostgresConnectionString(this IConfiguration config) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = config.Optional("POSTGRES_HOST", "localhost"),
            Port = int.Parse(config.Optional("POSTGRES_PORT", "5432")),
            Database = config.Required("POSTGRES_DB"),
            Username = config.Required("POSTGRES_USER"),
            Password = config.Required("POSTGRES_PASSWORD"),
        }.ConnectionString;

    public static ConnectionFactory RabbitMqConnectionFactory(this IConfiguration config)
    {
        if (config["RABBITMQ_URL"] is { Length: > 0 } url)
            return new ConnectionFactory { Uri = new Uri(url) };

        return new ConnectionFactory
        {
            HostName = config.Optional("RABBITMQ_HOST", "localhost"),
            Port = int.Parse(config.Optional("RABBITMQ_PORT", "5672")),
            UserName = config.Required("RABBITMQ_USER"),
            Password = config.Required("RABBITMQ_PASSWORD"),
        };
    }
}
