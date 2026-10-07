using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrafficService.Migrations
{
    // EF Core has no native PARTITION BY support, so this whole migration is raw SQL rather than
    // MigrationBuilder calls - see DatabaseContext.cs's OnModelCreating comment. Converting an
    // EXISTING table to partitioned isn't a simple ALTER TABLE in Postgres (there's no
    // "ALTER TABLE ... PARTITION BY") - the real technique is build a new partitioned table
    // alongside the old one, copy the data across, then swap names. The Designer.cs/
    // DatabaseContextModelSnapshot.cs siblings of this file were left as `dotnet ef migrations add`
    // generated them (the composite-key model change) - they only describe the logical EF model,
    // which has no concept of partitioning, so there's nothing in them to hand-edit for the DDL below.
    /// <inheritdoc />
    public partial class ConvertClicksToPartitionedTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Renaming a table does NOT rename its constraints/indexes - without renaming this one
            // first, creating "PK_clicks" on the new table below would collide with the old table's
            // still-attached index of the same name (index/constraint names share one namespace per
            // schema in Postgres).
            migrationBuilder.Sql(@"
                ALTER TABLE clicks RENAME CONSTRAINT ""PK_clicks"" TO ""PK_clicks_unpartitioned"";
                ALTER TABLE clicks RENAME TO clicks_unpartitioned;
            ");

            // ClickedAt must be part of the primary key - Postgres requires the partition key in
            // every unique/primary key on a table declared PARTITION BY RANGE on it.
            migrationBuilder.Sql(@"
                CREATE TABLE clicks (
                    ""Id"" uuid NOT NULL,
                    ""ClickedAt"" timestamp with time zone NOT NULL,
                    ""InboundLink"" text NOT NULL,
                    ""OutboundLink"" text NOT NULL,
                    ""Hash"" text NOT NULL,
                    CONSTRAINT ""PK_clicks"" PRIMARY KEY (""Id"", ""ClickedAt"")
                ) PARTITION BY RANGE (""ClickedAt"");
            ");

            // Bootstrap partitions relative to whenever this migration actually RUNS, not to when
            // it was authored (a date computed with `now()` here instead of hardcoded literals) -
            // 2 months back through 3 months ahead, monthly. A real deployment keeps rolling
            // partitions created ahead of time via a scheduled job (not built yet - see
            // .notes/PLAN.md's Scenario 4 Infra section); this bootstrap just means the DEFAULT
            // partition below isn't the only place new rows can land on a freshly migrated stack.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    start_month date := date_trunc('month', now() AT TIME ZONE 'UTC')::date - interval '2 months';
                    i int;
                    partition_start date;
                    partition_end date;
                    partition_name text;
                BEGIN
                    FOR i IN 0..5 LOOP
                        partition_start := (start_month + (i || ' months')::interval)::date;
                        partition_end := (start_month + ((i + 1) || ' months')::interval)::date;
                        partition_name := 'clicks_y' || to_char(partition_start, 'YYYY') || 'm' || to_char(partition_start, 'MM');
                        EXECUTE format(
                            'CREATE TABLE %I PARTITION OF clicks FOR VALUES FROM (%L) TO (%L)',
                            partition_name, partition_start, partition_end
                        );
                    END LOOP;
                END $$;
            ");

            // Catches anything outside the bootstrapped window - older historical data from before
            // this migration ran, or any gap if the (not-yet-built) partition-maintenance job ever
            // falls behind. Without this, an insert outside every explicit partition's range fails
            // outright instead of degrading to "slow but correct" (a sequential scan over this one
            // partition, same as the unpartitioned table used to be for everything).
            migrationBuilder.Sql(@"CREATE TABLE clicks_default PARTITION OF clicks DEFAULT;");

            // Copies directly into each partition table (and clicks_default for anything outside
            // the bootstrapped window) instead of INSERT-ing through the partitioned parent -
            // targeting the parent would make Postgres re-derive which partition every single row
            // belongs to (tuple routing) instead of just bulk-copying into a table we already know
            // is the right one, and it would hold the entire copy as one long transaction with no
            // per-partition commit boundaries. Same date math as the partition-creation block above,
            // recomputed here rather than shared across DO blocks - keeps each step self-contained.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    start_month date := date_trunc('month', now() AT TIME ZONE 'UTC')::date - interval '2 months';
                    end_month date := start_month + interval '6 months';
                    i int;
                    partition_start date;
                    partition_end date;
                    partition_name text;
                BEGIN
                    FOR i IN 0..5 LOOP
                        partition_start := (start_month + (i || ' months')::interval)::date;
                        partition_end := (start_month + ((i + 1) || ' months')::interval)::date;
                        partition_name := 'clicks_y' || to_char(partition_start, 'YYYY') || 'm' || to_char(partition_start, 'MM');
                        EXECUTE format(
                            'INSERT INTO %I (""Id"", ""ClickedAt"", ""InboundLink"", ""OutboundLink"", ""Hash"")
                             SELECT ""Id"", ""ClickedAt"", ""InboundLink"", ""OutboundLink"", ""Hash"" FROM clicks_unpartitioned
                             WHERE ""ClickedAt"" >= %L AND ""ClickedAt"" < %L',
                            partition_name, partition_start, partition_end
                        );
                    END LOOP;

                    -- Older historical data (or anything future-dated past the bootstrap window)
                    -- goes straight into the default partition.
                    INSERT INTO clicks_default (""Id"", ""ClickedAt"", ""InboundLink"", ""OutboundLink"", ""Hash"")
                    SELECT ""Id"", ""ClickedAt"", ""InboundLink"", ""OutboundLink"", ""Hash"" FROM clicks_unpartitioned
                    WHERE ""ClickedAt"" < start_month OR ""ClickedAt"" >= end_month;
                END $$;
            ");

            migrationBuilder.Sql(@"DROP TABLE clicks_unpartitioned;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Same reason as Up()'s identical step: renaming a table does NOT rename its
            // constraints/indexes, so without freeing "PK_clicks" here first, the CREATE TABLE
            // below would collide with the partitioned table's own still-attached PK index of that
            // name (this is exactly the bug Up() was written to avoid - Down() needs the same fix).
            migrationBuilder.Sql(@"ALTER TABLE clicks RENAME CONSTRAINT ""PK_clicks"" TO ""PK_clicks_partitioned"";");
            migrationBuilder.Sql(@"ALTER TABLE clicks RENAME TO clicks_partitioned;");

            migrationBuilder.Sql(@"
                CREATE TABLE clicks (
                    ""Id"" uuid NOT NULL,
                    ""ClickedAt"" timestamp with time zone NOT NULL,
                    ""InboundLink"" text NOT NULL,
                    ""OutboundLink"" text NOT NULL,
                    ""Hash"" text NOT NULL,
                    CONSTRAINT ""PK_clicks"" PRIMARY KEY (""Id"")
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO clicks (""Id"", ""ClickedAt"", ""InboundLink"", ""OutboundLink"", ""Hash"")
                SELECT ""Id"", ""ClickedAt"", ""InboundLink"", ""OutboundLink"", ""Hash"" FROM clicks_partitioned;
            ");

            // Dropping the renamed partitioned parent cascades to every partition it owns
            // (clicks_y*, clicks_default) - Postgres always does this for a partitioned table's
            // children, no CASCADE needed.
            migrationBuilder.Sql(@"DROP TABLE clicks_partitioned;");
        }
    }
}
