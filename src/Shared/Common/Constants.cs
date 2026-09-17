namespace Common;

public static class Constants
{
    public const string PostgresConnectionString = "Postgres";
    public const string RedisConnectionString = "Redis";
    public const string RedisInstanceName = "Redis:InstanceName";
    public const string RabbitMqConnectionString = "RabbitMq";
    public const string RabbitMqFallbackConnectionString = "RabbitMqFallback";
    public const string MongoDbConnectionString = "Mongo";

    public const string FrontendCorsPolicy = "Frontend";
    public const string CorsAllowedOriginsSection = "Cors:AllowedOrigins";
}
