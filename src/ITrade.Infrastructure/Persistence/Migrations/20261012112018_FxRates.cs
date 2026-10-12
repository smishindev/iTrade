using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fx_rates",
                columns: table => new
                {
                    base_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    quote_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rate_date = table.Column<LocalDate>(type: "date", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    fetched_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fx_rates", x => new { x.base_currency, x.quote_currency, x.rate_date, x.source });
                    table.CheckConstraint("ck_fx_rates_rate_is_positive", "rate > 0");
                    table.CheckConstraint("ck_fx_rates_two_currencies", "base_currency <> quote_currency");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fx_rates");
        }
    }
}
