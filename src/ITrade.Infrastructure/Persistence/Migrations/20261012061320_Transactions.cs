using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Transactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    holding_place_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trade_date = table.Column<LocalDate>(type: "date", nullable: false),
                    settlement_date = table.Column<LocalDate>(type: "date", nullable: false),
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity_change = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    cash_amount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    cash_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    related_to = table.Column<Guid>(type: "uuid", nullable: true),
                    reverses = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.CheckConstraint("ck_transactions_changes_something", "quantity_change <> 0 OR cash_amount <> 0");
                    table.CheckConstraint("ck_transactions_settles_on_or_after_the_trade", "settlement_date >= trade_date");
                    table.ForeignKey(
                        name: "fk_transactions_holding_places_holding_place_id",
                        column: x => x.holding_place_id,
                        principalTable: "holding_places",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transactions_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalTable: "instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transactions_transactions_related_to",
                        column: x => x.related_to,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transactions_transactions_reverses",
                        column: x => x.reverses,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_holding_place_id_source_external_id",
                table: "transactions",
                columns: new[] { "holding_place_id", "source", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_holding_place_id_trade_date",
                table: "transactions",
                columns: new[] { "holding_place_id", "trade_date" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_instrument_id_trade_date",
                table: "transactions",
                columns: new[] { "instrument_id", "trade_date" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_related_to",
                table: "transactions",
                column: "related_to");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_reverses",
                table: "transactions",
                column: "reverses",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_sequence",
                table: "transactions",
                column: "sequence",
                unique: true);

            // P3.1.02: the ledger is append-only, enforced by the database itself (also for the owner role).
            // A mistake is corrected by a reversing entry, never by changing or removing a row.
            migrationBuilder.Sql("""
                CREATE FUNCTION transactions_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'transactions is append-only: % is not allowed; record a reversal instead', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER transactions_no_update_delete
                    BEFORE UPDATE OR DELETE ON transactions
                    FOR EACH ROW EXECUTE FUNCTION transactions_append_only();
                CREATE TRIGGER transactions_no_truncate
                    BEFORE TRUNCATE ON transactions
                    FOR EACH STATEMENT EXECUTE FUNCTION transactions_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS transactions_no_truncate ON transactions;
                DROP TRIGGER IF EXISTS transactions_no_update_delete ON transactions;
                DROP FUNCTION IF EXISTS transactions_append_only();
                """);
            migrationBuilder.DropTable(
                name: "transactions");
        }
    }
}
