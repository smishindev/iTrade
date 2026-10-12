using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LedgerRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transactions_holding_place_id_source_external_id",
                table: "transactions");

            migrationBuilder.AddColumn<int>(
                name: "external_revision",
                table: "transactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_line_of_the_source",
                table: "transactions",
                columns: new[] { "holding_place_id", "source", "external_id", "external_revision" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_does_not_point_at_itself",
                table: "transactions",
                sql: "(reverses IS NULL OR reverses <> id) AND (related_to IS NULL OR related_to <> id)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_effects_fit_the_kind",
                table: "transactions",
                sql: "CASE kind WHEN 'Buy' THEN quantity_change * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) > 0 AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) < 0 WHEN 'Sell' THEN quantity_change * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) < 0 AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) > 0 WHEN 'Deposit' THEN quantity_change = 0 AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) > 0 WHEN 'Withdrawal' THEN quantity_change = 0 AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) < 0 WHEN 'CorporateAction' THEN true ELSE quantity_change = 0 AND cash_amount <> 0 END");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_imported_entry_has_its_external_id",
                table: "transactions",
                sql: "(source = 'Manual' OR external_id IS NOT NULL) AND (external_id IS NULL OR btrim(external_id) <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_instrument_fits_the_kind",
                table: "transactions",
                sql: "CASE WHEN kind IN ('Buy', 'Sell', 'Dividend', 'CorporateAction') THEN instrument_id IS NOT NULL WHEN kind IN ('TaxWithheld', 'Fee') THEN true ELSE instrument_id IS NULL END");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_kind_is_known",
                table: "transactions",
                sql: "kind IN ('Buy', 'Sell', 'Dividend', 'TaxWithheld', 'Fee', 'Interest', 'FxConversion', 'Deposit', 'Withdrawal', 'CorporateAction')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_only_a_trade_has_a_price",
                table: "transactions",
                sql: "(kind IN ('Buy', 'Sell')) = (price IS NOT NULL) AND (price IS NULL OR price > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_revision_belongs_to_an_external_id",
                table: "transactions",
                sql: "external_revision >= 0 AND (external_revision = 0 OR external_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transactions_source_is_known",
                table: "transactions",
                sql: "source IN ('Manual', 'Flex', 'ApiExecution')");

            // P3.1.02 (review): what one row says about another row cannot be a CHECK, so a trigger says it.
            // A row of the ledger is permanent, so a wrong link must not get in by any way at all:
            //  - an entry hangs on a main entry of its own holding place (links are one level deep);
            //  - a reversal is the exact opposite of an entry that is not itself a reversal.
            // That an entry is reversed at most once is the unique index on `reverses`.
            migrationBuilder.Sql("""
                CREATE FUNCTION transactions_links_are_sound() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent transactions%ROWTYPE;
                    target transactions%ROWTYPE;
                BEGIN
                    IF NEW.related_to IS NOT NULL THEN
                        SELECT * INTO parent FROM transactions WHERE id = NEW.related_to;
                        IF FOUND AND (parent.holding_place_id <> NEW.holding_place_id OR parent.related_to IS NOT NULL) THEN
                            RAISE EXCEPTION 'transactions: entry % cannot hang on %: an entry hangs on a main entry of its own holding place', NEW.id, NEW.related_to
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;
                    IF NEW.reverses IS NOT NULL THEN
                        SELECT * INTO target FROM transactions WHERE id = NEW.reverses;
                        IF FOUND AND (target.reverses IS NOT NULL
                            OR target.holding_place_id <> NEW.holding_place_id
                            OR target.kind <> NEW.kind
                            OR target.trade_date <> NEW.trade_date
                            OR target.settlement_date <> NEW.settlement_date
                            OR target.instrument_id IS DISTINCT FROM NEW.instrument_id
                            OR target.price IS DISTINCT FROM NEW.price
                            OR target.cash_currency <> NEW.cash_currency
                            OR target.cash_amount <> -NEW.cash_amount
                            OR target.quantity_change <> -NEW.quantity_change) THEN
                            RAISE EXCEPTION 'transactions: entry % is not the exact opposite of %, which it reverses', NEW.id, NEW.reverses
                                USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER transactions_sound_links
                    BEFORE INSERT ON transactions
                    FOR EACH ROW EXECUTE FUNCTION transactions_links_are_sound();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS transactions_sound_links ON transactions;
                DROP FUNCTION IF EXISTS transactions_links_are_sound();
                """);

            migrationBuilder.DropIndex(
                name: "ix_transactions_line_of_the_source",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_does_not_point_at_itself",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_effects_fit_the_kind",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_imported_entry_has_its_external_id",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_instrument_fits_the_kind",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_kind_is_known",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_only_a_trade_has_a_price",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_revision_belongs_to_an_external_id",
                table: "transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transactions_source_is_known",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "external_revision",
                table: "transactions");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_holding_place_id_source_external_id",
                table: "transactions",
                columns: new[] { "holding_place_id", "source", "external_id" },
                unique: true);
        }
    }
}
