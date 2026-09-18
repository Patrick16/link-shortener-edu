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

    // Shared between AuthApi (issues tokens) and any service that validates them (LinkApi, ...) —
    // one set of config keys so issuer/validator can't drift apart on where the signing key lives.
    public const string JwtSigningKeySection = "Jwt:SigningKey";
    public const string JwtIssuerSection = "Jwt:Issuer";
    public const string JwtAudienceSection = "Jwt:Audience";
}
