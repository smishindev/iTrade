using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Withdrawals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_market_sessions",
                table: "market_sessions");

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "market_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "market_sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "withdrawn",
                table: "market_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "fund_events",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "withdrawn",
                table: "fund_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "pk_market_sessions",
                table: "market_sessions",
                columns: new[] { "calendar", "session", "revision" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_market_sessions_first_is_not_withdrawn",
                table: "market_sessions",
                sql: "revision > 0 OR NOT withdrawn");

            migrationBuilder.AddCheckConstraint(
                name: "ck_market_sessions_revision_is_not_negative",
                table: "market_sessions",
                sql: "revision >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_market_sessions_withdrawal_has_a_reason",
                table: "market_sessions",
                sql: "(withdrawn AND reason IS NOT NULL AND btrim(reason) <> '') OR (NOT withdrawn AND reason IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fund_events_first_is_not_withdrawn",
                table: "fund_events",
                sql: "revision > 0 OR NOT withdrawn");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fund_events_withdrawal_has_a_reason",
                table: "fund_events",
                sql: "(withdrawn AND reason IS NOT NULL AND btrim(reason) <> '') OR (NOT withdrawn AND reason IS NULL)");

            // P3.4.05: a session or a fund event the owner withdrew stays, followed by a row that says so; the
            // latest row of a day is what it is. So a close or an event is taken only for a day whose latest row
            // is a session; sessions, like events, are numbered without gaps; and a day is withdrawn only when
            // nothing traded on it (no close) and every event of it was withdrawn first - otherwise the rows that
            // rest on it would stand on a day that is none.
            migrationBuilder.Sql("""
                CREATE FUNCTION market_session_is_live(day date) RETURNS boolean LANGUAGE sql STABLE AS $$
                    SELECT EXISTS (
                        SELECT 1 FROM market_sessions s
                        WHERE s.session = day AND NOT s.withdrawn
                          AND s.revision = (SELECT max(m.revision) FROM market_sessions m
                                            WHERE m.calendar = s.calendar AND m.session = s.session));
                $$;

                CREATE OR REPLACE FUNCTION closing_prices_session_is_known() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT market_session_is_live(NEW.session) THEN
                        RAISE EXCEPTION 'closing_prices: % is not a session of a stored exchange calendar', NEW.session
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE OR REPLACE FUNCTION fund_events_are_in_order() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.revision > 0 AND NOT EXISTS (
                        SELECT 1 FROM fund_events
                        WHERE instrument_id = NEW.instrument_id AND day = NEW.day AND kind = NEW.kind
                          AND revision = NEW.revision - 1) THEN
                        RAISE EXCEPTION 'fund_events: revision % of the % of % has no revision before it', NEW.revision, NEW.kind, NEW.day
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT market_session_is_live(NEW.day) THEN
                        RAISE EXCEPTION 'fund_events: % is not a session of a stored exchange calendar', NEW.day
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION market_sessions_are_in_order() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.revision > 0 AND NOT EXISTS (
                        SELECT 1 FROM market_sessions
                        WHERE calendar = NEW.calendar AND session = NEW.session AND revision = NEW.revision - 1) THEN
                        RAISE EXCEPTION 'market_sessions: revision % of % has no revision before it', NEW.revision, NEW.session
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.withdrawn AND EXISTS (SELECT 1 FROM closing_prices WHERE session = NEW.session) THEN
                        RAISE EXCEPTION 'market_sessions: closing prices are stored for %: a day something traded on is a session', NEW.session
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.withdrawn AND EXISTS (
                        SELECT 1 FROM fund_events f
                        WHERE f.day = NEW.session AND NOT f.withdrawn
                          AND f.revision = (SELECT max(g.revision) FROM fund_events g
                                            WHERE g.instrument_id = f.instrument_id AND g.day = f.day AND g.kind = f.kind)) THEN
                        RAISE EXCEPTION 'market_sessions: % has dividends or splits that are not withdrawn', NEW.session
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER market_sessions_order
                    BEFORE INSERT ON market_sessions
                    FOR EACH ROW EXECUTE FUNCTION market_sessions_are_in_order();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The functions as migrations MarketSessions and FundEvents left them, before the columns they read go.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS market_sessions_order ON market_sessions;
                DROP FUNCTION IF EXISTS market_sessions_are_in_order();

                CREATE OR REPLACE FUNCTION closing_prices_session_is_known() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM market_sessions WHERE session = NEW.session) THEN
                        RAISE EXCEPTION 'closing_prices: % is not a session of a stored exchange calendar', NEW.session
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE OR REPLACE FUNCTION fund_events_are_in_order() RETURNS trigger LANGUAGE plpgsql AS $$
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

                DROP FUNCTION IF EXISTS market_session_is_live(date);
                """);

            migrationBuilder.DropPrimaryKey(
                name: "pk_market_sessions",
                table: "market_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_market_sessions_first_is_not_withdrawn",
                table: "market_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_market_sessions_revision_is_not_negative",
                table: "market_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_market_sessions_withdrawal_has_a_reason",
                table: "market_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_fund_events_first_is_not_withdrawn",
                table: "fund_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_fund_events_withdrawal_has_a_reason",
                table: "fund_events");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "market_sessions");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "market_sessions");

            migrationBuilder.DropColumn(
                name: "withdrawn",
                table: "market_sessions");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "fund_events");

            migrationBuilder.DropColumn(
                name: "withdrawn",
                table: "fund_events");

            migrationBuilder.AddPrimaryKey(
                name: "pk_market_sessions",
                table: "market_sessions",
                columns: new[] { "calendar", "session" });
        }
    }
}
