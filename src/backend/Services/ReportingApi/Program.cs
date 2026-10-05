using Infrastructure;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddWebApiDefaults();

builder.AddClickHouseReader();

// No JWT here - reports are read-only aggregates over click counts, same "never require a token"
// posture as LinkApi; see the plan's open call if this ever needs to be scoped to a link's owner.
builder.Services.AddFrontendCors(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddClickHouseHealthCheck<IClickFactQueryService>();

var app = builder.Build();

app.MapApiDocumentation();

app.UseApiExceptionHandling();
app.UseHttpsRedirection();
app.UseFrontendCors();
app.UseAuthorization();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
