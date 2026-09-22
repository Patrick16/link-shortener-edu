namespace Common;

public static class Constants
{
    public const string PostgresConnectionString = "Postgres";
    // Migrations run DDL (CREATE TABLE/ALTER TABLE) - PgCat's query parser only reliably classifies
    // SELECT/INSERT/UPDATE/DELETE, and was observed routing a plain CREATE TABLE to a read-only
    // replica (which then rejects it) rather than the primary. Services that own a database and run
    // Database.MigrateAsync() connect directly to the primary for that one call, bypassing PgCat
    // entirely, then use the normal pooled/PgCat-routed connection for everything else at runtime.
    public const string PostgresPrimaryConnectionString = "PostgresPrimary";
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
