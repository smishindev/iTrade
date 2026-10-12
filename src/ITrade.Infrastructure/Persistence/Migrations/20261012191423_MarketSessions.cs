using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_sessions",
                columns: table => new
                {
                    calendar = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    session = table.Column<LocalDate>(type: "date", nullable: false),
                    imported_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_market_sessions", x => new { x.calendar, x.session });
                });

            migrationBuilder.CreateTable(
                name: "price_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exported_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    imported_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_imports", x => x.id);
                    table.CheckConstraint("ck_price_imports_sha256_is_lower_case_hex", "sha256 ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_market_sessions_session",
                table: "market_sessions",
                column: "session");

            migrationBuilder.CreateIndex(
                name: "ix_price_imports_exported_at",
                table: "price_imports",
                column: "exported_at");

            // P3.3.02: whether a day without a price is a closed exchange or a gap in the data is read from
            // market_sessions, and which export was imported last from price_imports - so neither is ever
            // changed or removed, by anyone. And a close is stored only for a day that is a session - an
            // import stores its sessions first. (Closes stored before this migration are not looked at: they
            // get their sessions with the next import.)
            migrationBuilder.Sql("""
                CREATE FUNCTION market_sessions_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER market_sessions_no_update_delete
                    BEFORE UPDATE OR DELETE ON market_sessions
                    FOR EACH ROW EXECUTE FUNCTION market_sessions_append_only();
                CREATE TRIGGER market_sessions_no_truncate
                    BEFORE TRUNCATE ON market_sessions
                    FOR EACH STATEMENT EXECUTE FUNCTION market_sessions_append_only();
                CREATE TRIGGER price_imports_no_update_delete
                    BEFORE UPDATE OR DELETE ON price_imports
                    FOR EACH ROW EXECUTE FUNCTION market_sessions_append_only();
                CREATE TRIGGER price_imports_no_truncate
                    BEFORE TRUNCATE ON price_imports
                    FOR EACH STATEMENT EXECUTE FUNCTION market_sessions_append_only();

                CREATE FUNCTION closing_prices_session_is_known() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM market_sessions WHERE session = NEW.session) THEN
                        RAISE EXCEPTION 'closing_prices: % is not a session of a stored exchange calendar', NEW.session
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER closing_prices_session_known
                    BEFORE INSERT ON closing_prices
                    FOR EACH ROW EXECUTE FUNCTION closing_prices_session_is_known();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS closing_prices_session_known ON closing_prices;
                DROP FUNCTION IF EXISTS closing_prices_session_is_known();
                DROP TRIGGER IF EXISTS price_imports_no_truncate ON price_imports;
                DROP TRIGGER IF EXISTS price_imports_no_update_delete ON price_imports;
                DROP TRIGGER IF EXISTS market_sessions_no_truncate ON market_sessions;
                DROP TRIGGER IF EXISTS market_sessions_no_update_delete ON market_sessions;
                DROP FUNCTION IF EXISTS market_sessions_append_only();
                """);

            migrationBuilder.DropTable(
                name: "market_sessions");

            migrationBuilder.DropTable(
                name: "price_imports");
        }
    }
}
