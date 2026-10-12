using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ITrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FxRatesAcceptedReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_fx_rates_revision_has_its_reason",
                table: "fx_rates");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fx_rates_revision_says_why",
                table: "fx_rates",
                sql: "revision >= 0 AND (revision = 0 OR reason IS NOT NULL) AND (reason IS NULL OR btrim(reason) <> '')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_fx_rates_revision_says_why",
                table: "fx_rates");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fx_rates_revision_has_its_reason",
                table: "fx_rates",
                sql: "revision >= 0 AND (revision = 0) = (reason IS NULL) AND (reason IS NULL OR btrim(reason) <> '')");
        }
    }
}
