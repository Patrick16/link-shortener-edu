namespace ControlApi.Models;

// The three standing infra toggles the graph exposes on nginx/pgcat/redis's own node panels -
// unlike a chaos run or a traffic run, these persist until switched back, because the whole point
// is to show how the *system* behaves without that piece, not just one synthetic test.
//
// NginxBypassed lives purely in control-api's memory (it only changes which URL a k6 container is
// given, nginx itself keeps running so the frontend app's own manual use of it is unaffected).
// PgcatEnabled/CacheEnabled instead reflect real container state - toggling them recreates the
// affected services with a different connection string / cache flag via docker compose - so on a
// control-api restart these two fields could in principle drift from the containers' actual last
// setting; a real restart during an experiment is rare enough for a local sandbox tool that this is
// an accepted, documented limitation rather than something worth reconciling on startup.
public record InfraStatus(bool NginxBypassed, bool PgcatEnabled, bool CacheEnabled);

public record InfraToggleRequest(bool Enabled);
