using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstrumentsAndHoldingPlaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holding_places",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    integration = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holding_places", x => x.id);
                    table.CheckConstraint("ck_holding_places_only_a_broker_is_read_automatically", "integration <> 'Broker' OR kind = 'Broker'");
                });

            migrationBuilder.CreateTable(
                name: "instruments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    exchange = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    isin = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    ibkr_con_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instruments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "instrument_former_tickers",
                columns: table => new
                {
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    until = table.Column<LocalDate>(type: "date", nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instrument_former_tickers", x => new { x.instrument_id, x.until });
                    table.ForeignKey(
                        name: "fk_instrument_former_tickers_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalTable: "instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_holding_places_name",
                table: "holding_places",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_instrument_former_tickers_ticker",
                table: "instrument_former_tickers",
                column: "ticker");

            migrationBuilder.CreateIndex(
                name: "ix_instruments_ibkr_con_id",
                table: "instruments",
                column: "ibkr_con_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_instruments_isin",
                table: "instruments",
                column: "isin");

            migrationBuilder.CreateIndex(
                name: "ix_instruments_ticker_exchange",
                table: "instruments",
                columns: new[] { "ticker", "exchange" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holding_places");

            migrationBuilder.DropTable(
                name: "instrument_former_tickers");

            migrationBuilder.DropTable(
                name: "instruments");
        }
    }
}
