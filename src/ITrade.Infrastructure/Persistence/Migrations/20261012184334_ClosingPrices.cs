using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClosingPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "closing_prices",
                columns: table => new
                {
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session = table.Column<LocalDate>(type: "date", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    close = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    imported_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_closing_prices", x => new { x.instrument_id, x.session, x.revision });
                    table.CheckConstraint("ck_closing_prices_close_is_positive", "close > 0");
                    table.CheckConstraint("ck_closing_prices_revision_is_not_negative", "revision >= 0");
                    table.ForeignKey(
                        name: "fk_closing_prices_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalTable: "instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_closing_prices_session",
                table: "closing_prices",
                column: "session");

            // P3.3.02: valuations rest on these prices, so a stored close is never changed or removed -
            // enforced by the database itself (also for the owner role). A close the source corrected is a
            // further row with the next revision, and revisions are numbered without gaps.
            migrationBuilder.Sql("""
                CREATE FUNCTION closing_prices_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'closing_prices is append-only: % is not allowed; record a revision instead', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER closing_prices_no_update_delete
                    BEFORE UPDATE OR DELETE ON closing_prices
                    FOR EACH ROW EXECUTE FUNCTION closing_prices_append_only();
                CREATE TRIGGER closing_prices_no_truncate
                    BEFORE TRUNCATE ON closing_prices
                    FOR EACH STATEMENT EXECUTE FUNCTION closing_prices_append_only();

                CREATE FUNCTION closing_prices_revisions_in_order() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.revision > 0 AND NOT EXISTS (
                        SELECT 1 FROM closing_prices
                        WHERE instrument_id = NEW.instrument_id AND session = NEW.session
                          AND revision = NEW.revision - 1) THEN
                        RAISE EXCEPTION 'closing_prices: revision % of % has no revision before it', NEW.revision, NEW.session
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER closing_prices_revision_order
                    BEFORE INSERT ON closing_prices
                    FOR EACH ROW EXECUTE FUNCTION closing_prices_revisions_in_order();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS closing_prices_revision_order ON closing_prices;
                DROP TRIGGER IF EXISTS closing_prices_no_truncate ON closing_prices;
                DROP TRIGGER IF EXISTS closing_prices_no_update_delete ON closing_prices;
                DROP FUNCTION IF EXISTS closing_prices_revisions_in_order();
                DROP FUNCTION IF EXISTS closing_prices_append_only();
                """);

            migrationBuilder.DropTable(
                name: "closing_prices");
        }
    }
}
