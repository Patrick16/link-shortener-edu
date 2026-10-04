namespace Infrastructure;

// Marker-only: implemented exclusively by the DbContext type of the one service that actually
// owns and migrates a given database (AuthApi/users_db, ShortenerService/links_db,
// TrafficService/clicks_db). LinkApi's and RedirectApi's own DatabaseContext types deliberately do
// NOT implement this - they only ever read links_db - so MigratePostgresAsync's constraint below
// turns an accidental "migrate a database this service doesn't own" call into a compile error
// instead of relying solely on the comment convention next to each real call site.
public interface IOwnedDbContext;
