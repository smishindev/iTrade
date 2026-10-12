using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FxRatesRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_fx_rates",
                table: "fx_rates");

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "fx_rates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "fx_rates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_fx_rates",
                table: "fx_rates",
                columns: new[] { "base_currency", "quote_currency", "rate_date", "source", "revision" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_fx_rates_revision_has_its_reason",
                table: "fx_rates",
                sql: "revision >= 0 AND (revision = 0) = (reason IS NULL) AND (reason IS NULL OR btrim(reason) <> '')");

            // P3.3.01: amounts in shekels rest on these rates, so a stored rate is never changed or removed -
            // enforced by the database itself (also for the owner role). A rate the source corrected is a
            // further row with the next revision, and revisions are numbered without gaps.
            migrationBuilder.Sql("""
                CREATE FUNCTION fx_rates_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'fx_rates is append-only: % is not allowed; record a revision instead', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER fx_rates_no_update_delete
                    BEFORE UPDATE OR DELETE ON fx_rates
                    FOR EACH ROW EXECUTE FUNCTION fx_rates_append_only();
                CREATE TRIGGER fx_rates_no_truncate
                    BEFORE TRUNCATE ON fx_rates
                    FOR EACH STATEMENT EXECUTE FUNCTION fx_rates_append_only();

                CREATE FUNCTION fx_rates_revisions_in_order() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.revision > 0 AND NOT EXISTS (
                        SELECT 1 FROM fx_rates
                        WHERE base_currency = NEW.base_currency AND quote_currency = NEW.quote_currency
                          AND rate_date = NEW.rate_date AND source = NEW.source AND revision = NEW.revision - 1) THEN
                        RAISE EXCEPTION 'fx_rates: revision % of % has no revision before it', NEW.revision, NEW.rate_date
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER fx_rates_revision_order
                    BEFORE INSERT ON fx_rates
                    FOR EACH ROW EXECUTE FUNCTION fx_rates_revisions_in_order();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS fx_rates_revision_order ON fx_rates;
                DROP TRIGGER IF EXISTS fx_rates_no_truncate ON fx_rates;
                DROP TRIGGER IF EXISTS fx_rates_no_update_delete ON fx_rates;
                DROP FUNCTION IF EXISTS fx_rates_revisions_in_order();
                DROP FUNCTION IF EXISTS fx_rates_append_only();
                """);

            migrationBuilder.DropPrimaryKey(
                name: "pk_fx_rates",
                table: "fx_rates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_fx_rates_revision_has_its_reason",
                table: "fx_rates");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "fx_rates");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "fx_rates");

            migrationBuilder.AddPrimaryKey(
                name: "pk_fx_rates",
                table: "fx_rates",
                columns: new[] { "base_currency", "quote_currency", "rate_date", "source" });
        }
    }
}
