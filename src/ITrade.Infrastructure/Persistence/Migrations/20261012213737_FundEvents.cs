using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FundEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fund_events",
                columns: table => new
                {
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<LocalDate>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    imported_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fund_events", x => new { x.instrument_id, x.day, x.kind, x.revision });
                    table.CheckConstraint("ck_fund_events_kind_is_known", "kind IN ('Dividend', 'Split')");
                    table.CheckConstraint("ck_fund_events_revision_is_not_negative", "revision >= 0");
                    table.CheckConstraint("ck_fund_events_value_is_positive", "value > 0");
                    table.ForeignKey(
                        name: "fk_fund_events_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalTable: "instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // P3.4.04: what a fund paid and how it split is what an index is followed with, and what a split
            // missing from the ledger is noticed by - so a stored event is never changed or removed (the
            // function is the one of market_sessions: it names the table it was called for). An event the
            // source corrected is a further row with the next revision, numbered without gaps; and an event
            // belongs to a day the exchange was open - an import stores its sessions first.
            migrationBuilder.Sql("""
                CREATE TRIGGER fund_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON fund_events
                    FOR EACH ROW EXECUTE FUNCTION market_sessions_append_only();
                CREATE TRIGGER fund_events_no_truncate
                    BEFORE TRUNCATE ON fund_events
                    FOR EACH STATEMENT EXECUTE FUNCTION market_sessions_append_only();

                CREATE FUNCTION fund_events_are_in_order() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.revision > 0 AND NOT EXISTS (
                        SELECT 1 FROM fund_events
                        WHERE instrument_id = NEW.instrument_id AND day = NEW.day AND kind = NEW.kind
                          AND revision = NEW.revision - 1) THEN
                        RAISE EXCEPTION 'fund_events: revision % of the % of % has no revision before it', NEW.revision, NEW.kind, NEW.day
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM market_sessions WHERE session = NEW.day) THEN
                        RAISE EXCEPTION 'fund_events: % is not a session of a stored exchange calendar', NEW.day
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER fund_events_order
                    BEFORE INSERT ON fund_events
                    FOR EACH ROW EXECUTE FUNCTION fund_events_are_in_order();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS fund_events_order ON fund_events;
                DROP FUNCTION IF EXISTS fund_events_are_in_order();
                DROP TRIGGER IF EXISTS fund_events_no_truncate ON fund_events;
                DROP TRIGGER IF EXISTS fund_events_no_update_delete ON fund_events;
                """);

            migrationBuilder.DropTable(
                name: "fund_events");
        }
    }
}
