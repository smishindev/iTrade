using ITrade.Application.Jobs;
using ITrade.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace ITrade.Infrastructure.Persistence;

/// <summary>
/// The platform database (PLAN v4 §4). Conventions: snake_case names; money and quantities are numeric
/// (never float) — every decimal is numeric(18,6) unless its column says otherwise (the tables of phase 3
/// set amounts to numeric(18,4) and FX rates to numeric(12,6) where they are declared);
/// instants are NodaTime <see cref="Instant"/> → timestamptz (UTC); trade dates → date; typed ids → uuid.
/// </summary>
public sealed class ITradeDbContext(DbContextOptions<ITradeDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    public DbSet<InstrumentRow> Instruments => Set<InstrumentRow>();

    public DbSet<FormerTickerRow> FormerTickers => Set<FormerTickerRow>();

    public DbSet<HoldingPlaceRow> HoldingPlaces => Set<HoldingPlaceRow>();

    public DbSet<TransactionRow> Transactions => Set<TransactionRow>();

    public DbSet<FxRateRow> FxRates => Set<FxRateRow>();

    public DbSet<ClosingPriceRow> ClosingPrices => Set<ClosingPriceRow>();

    public DbSet<MarketSessionRow> MarketSessions => Set<MarketSessionRow>();

    public DbSet<PriceImportRow> PriceImports => Set<PriceImportRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<decimal>().HavePrecision(18, 6);
        configurationBuilder.Properties<Quantity>().HaveConversion<QuantityConverter>().HavePrecision(18, 6);
        configurationBuilder.Properties<Currency>().HaveConversion<CurrencyConverter>().HaveMaxLength(3);
        configurationBuilder.Properties<InstrumentId>().HaveConversion<InstrumentIdConverter>();
        configurationBuilder.Properties<HoldingPlaceId>().HaveConversion<HoldingPlaceIdConverter>();
        configurationBuilder.Properties<TransactionId>().HaveConversion<TransactionIdConverter>();
        configurationBuilder.Properties<OrderId>().HaveConversion<OrderIdConverter>();
        configurationBuilder.Properties<TradeDate>().HaveConversion<TradeDateConverter>();
        configurationBuilder.Properties<Ticker>().HaveConversion<TickerConverter>().HaveMaxLength(Ticker.MaxLength);
        configurationBuilder.Properties<Isin>().HaveConversion<IsinConverter>().HaveMaxLength(Isin.Length);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(200);
            e.Property(s => s.Value).HasColumnType("jsonb");
        });
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Kind).HasMaxLength(100);
            e.Property(a => a.Actor).HasMaxLength(100);
            e.Property(a => a.Payload).HasColumnType("jsonb");
            e.HasIndex(a => a.OccurredAt);
        });
        modelBuilder.Entity<JobRun>(e =>
        {
            e.HasKey(j => new { j.Name, j.TradeDate });
            e.Property(j => j.Name).HasMaxLength(100);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        });
        modelBuilder.Entity<InstrumentRow>(e =>
        {
            e.ToTable("instruments");
            e.HasKey(i => i.Id);
            e.Property(i => i.Exchange).HasMaxLength(Instrument.MaxExchangeLength);
            e.Property(i => i.Name).HasMaxLength(Instrument.MaxNameLength);
            // One instrument per ticker on an exchange today, and per IBKR contract (several rows may have none).
            e.HasIndex(i => new { i.Ticker, i.Exchange }).IsUnique();
            e.HasIndex(i => i.IbkrConId).IsUnique();
            e.HasIndex(i => i.Isin);
        });
        modelBuilder.Entity<FormerTickerRow>(e =>
        {
            e.ToTable("instrument_former_tickers");
            e.HasKey(f => new { f.InstrumentId, f.Until });
            e.HasOne<InstrumentRow>().WithMany().HasForeignKey(f => f.InstrumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => f.Ticker);
        });
        modelBuilder.Entity<HoldingPlaceRow>(e =>
        {
            e.ToTable("holding_places", table => table.HasCheckConstraint(
                "ck_holding_places_only_a_broker_is_read_automatically",
                "integration <> 'Broker' OR kind = 'Broker'"));
            e.HasKey(h => h.Id);
            e.Property(h => h.Name).HasMaxLength(HoldingPlace.MaxNameLength);
            e.Property(h => h.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(h => h.Integration).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(h => h.Name).IsUnique();
        });
        modelBuilder.Entity<TransactionRow>(e =>
        {
            e.ToTable("transactions", table =>
            {
                table.HasCheckConstraint("ck_transactions_settles_on_or_after_the_trade", "settlement_date >= trade_date");
                table.HasCheckConstraint("ck_transactions_changes_something", "quantity_change <> 0 OR cash_amount <> 0");
                // A row of the ledger can never be corrected or removed, so the rules of the domain type
                // (Transaction) are repeated here: a wrong row must not get in by any way at all.
                table.HasCheckConstraint(
                    "ck_transactions_kind_is_known",
                    "kind IN ('Buy', 'Sell', 'Dividend', 'TaxWithheld', 'Fee', 'Interest', 'FxConversion', 'Deposit', " +
                    "'Withdrawal', 'CorporateAction')");
                table.HasCheckConstraint("ck_transactions_source_is_known", "source IN ('Manual', 'Flex', 'ApiExecution')");
                table.HasCheckConstraint(
                    "ck_transactions_imported_entry_has_its_external_id",
                    "(source = 'Manual' OR external_id IS NOT NULL) AND (external_id IS NULL OR btrim(external_id) <> '')");
                table.HasCheckConstraint(
                    "ck_transactions_revision_belongs_to_an_external_id",
                    "external_revision >= 0 AND (external_revision = 0 OR external_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_transactions_does_not_point_at_itself",
                    "(reverses IS NULL OR reverses <> id) AND (related_to IS NULL OR related_to <> id)");
                table.HasCheckConstraint(
                    "ck_transactions_only_a_trade_has_a_price",
                    "(kind IN ('Buy', 'Sell')) = (price IS NOT NULL) AND (price IS NULL OR price > 0)");
                table.HasCheckConstraint(
                    "ck_transactions_instrument_fits_the_kind",
                    "CASE WHEN kind IN ('Buy', 'Sell', 'Dividend', 'CorporateAction') THEN instrument_id IS NOT NULL " +
                    "WHEN kind IN ('TaxWithheld', 'Fee') THEN true ELSE instrument_id IS NULL END");
                // For a reversal the signs are those of the entry it cancels, turned around.
                table.HasCheckConstraint(
                    "ck_transactions_effects_fit_the_kind",
                    "CASE kind " +
                    "WHEN 'Buy' THEN quantity_change * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) > 0 " +
                    "AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) < 0 " +
                    "WHEN 'Sell' THEN quantity_change * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) < 0 " +
                    "AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) > 0 " +
                    "WHEN 'Deposit' THEN quantity_change = 0 AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) > 0 " +
                    "WHEN 'Withdrawal' THEN quantity_change = 0 AND cash_amount * (CASE WHEN reverses IS NULL THEN 1 ELSE -1 END) < 0 " +
                    "WHEN 'CorporateAction' THEN true " +
                    "ELSE quantity_change = 0 AND cash_amount <> 0 END");
            });
            e.HasKey(t => t.Id);
            e.Property(t => t.Sequence).UseIdentityAlwaysColumn();
            e.HasIndex(t => t.Sequence).IsUnique();
            e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.ExternalId).HasMaxLength(Transaction.MaxExternalIdLength);
            e.Property(t => t.Note).HasMaxLength(Transaction.MaxNoteLength);
            // The ledger references are never cascaded: nothing a transaction points at may disappear.
            e.HasOne<HoldingPlaceRow>().WithMany().HasForeignKey(t => t.HoldingPlaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InstrumentRow>().WithMany().HasForeignKey(t => t.InstrumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TransactionRow>().WithMany().HasForeignKey(t => t.RelatedTo).OnDelete(DeleteBehavior.Restrict);
            // Declared as many-to-one on purpose, with the unique index below doing the "at most one reversal".
            // As a one-to-one relationship EF Core would "move" the reversal: adding a second one made it try to
            // null the first one's reference - an UPDATE of the ledger (stopped only by the append-only trigger).
            e.HasOne<TransactionRow>().WithMany().HasForeignKey(t => t.Reverses).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => new { t.HoldingPlaceId, t.TradeDate });
            e.HasIndex(t => new { t.InstrumentId, t.TradeDate });
            // What a repeated import recognises an entry by; entries typed in by hand have no external id.
            // The revision lets a line be recorded again after its earlier recording was reversed.
            e.HasIndex(t => new { t.HoldingPlaceId, t.Source, t.ExternalId, t.ExternalRevision })
                .IsUnique()
                .HasDatabaseName("ix_transactions_line_of_the_source");
            // An entry is reversed at most once.
            e.HasIndex(t => t.Reverses).IsUnique();
        });
        modelBuilder.Entity<FxRateRow>(e =>
        {
            e.ToTable("fx_rates", table =>
            {
                table.HasCheckConstraint("ck_fx_rates_rate_is_positive", "rate > 0");
                table.HasCheckConstraint("ck_fx_rates_two_currencies", "base_currency <> quote_currency");
                // A revision is the owner's decision and says why; so does a first rate the owner had to accept.
                table.HasCheckConstraint(
                    "ck_fx_rates_revision_says_why",
                    "revision >= 0 AND (revision = 0 OR reason IS NOT NULL) AND (reason IS NULL OR btrim(reason) <> '')");
            });
            e.HasKey(r => new { r.BaseCurrency, r.QuoteCurrency, r.RateDate, r.Source, r.Revision });
            e.Property(r => r.Source).HasMaxLength(50);
            e.Property(r => r.Reason).HasMaxLength(FxRateStore.MaxReasonLength);
            e.Property(r => r.Rate).HasPrecision(12, 6);
        });
        modelBuilder.Entity<ClosingPriceRow>(e =>
        {
            e.ToTable("closing_prices", table =>
            {
                table.HasCheckConstraint("ck_closing_prices_close_is_positive", "close > 0");
                table.HasCheckConstraint("ck_closing_prices_revision_is_not_negative", "revision >= 0");
            });
            e.HasKey(p => new { p.InstrumentId, p.Session, p.Revision });
            e.Property(p => p.Source).HasMaxLength(50);
            e.HasOne<InstrumentRow>().WithMany().HasForeignKey(p => p.InstrumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => p.Session);
        });
        modelBuilder.Entity<MarketSessionRow>(e =>
        {
            e.ToTable("market_sessions");
            e.HasKey(s => new { s.Calendar, s.Session });
            e.Property(s => s.Calendar).HasMaxLength(10);
            // "Was any exchange open that day?" - asked for every close that is stored (migration MarketSessions).
            e.HasIndex(s => s.Session);
        });
        modelBuilder.Entity<PriceImportRow>(e =>
        {
            e.ToTable("price_imports", table => table.HasCheckConstraint(
                "ck_price_imports_sha256_is_lower_case_hex", "sha256 ~ '^[0-9a-f]{64}$'"));
            e.HasKey(i => i.Id);
            e.Property(i => i.Sha256).HasMaxLength(64);
            e.HasIndex(i => i.ExportedAt);
        });
    }

    private sealed class TickerConverter() : ValueConverter<Ticker, string>(t => t.Value, v => new Ticker(v));

    private sealed class IsinConverter() : ValueConverter<Isin, string>(i => i.Value, v => new Isin(v));

    private sealed class TradeDateConverter() : ValueConverter<TradeDate, LocalDate>(d => d.Value, v => new TradeDate(v));

    private sealed class QuantityConverter() : ValueConverter<Quantity, decimal>(q => q.Value, v => new Quantity(v));

    private sealed class CurrencyConverter() : ValueConverter<Currency, string>(c => c.Code, v => Currency.Of(v));

    private sealed class InstrumentIdConverter() : ValueConverter<InstrumentId, Guid>(id => id.Value, v => new InstrumentId(v));

    private sealed class HoldingPlaceIdConverter() : ValueConverter<HoldingPlaceId, Guid>(id => id.Value, v => new HoldingPlaceId(v));

    private sealed class TransactionIdConverter() : ValueConverter<TransactionId, Guid>(id => id.Value, v => new TransactionId(v));

    private sealed class OrderIdConverter() : ValueConverter<OrderId, Guid>(id => id.Value, v => new OrderId(v));
}

/// <summary>A key → JSON value setting of the platform.</summary>
public sealed class Setting
{
    public required string Key { get; init; }

    public required string Value { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary>Append-only record of what happened (who approved what, imports, reconciliations). Never updated.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Ids.NewV7();

    public Instant OccurredAt { get; init; }

    public required string Actor { get; init; }

    public required string Kind { get; init; }

    public required string Payload { get; init; }
}

/// <summary>
/// One daily job on one trade date (P2.4.05). The key (name, trade date) is what lets missed sessions
/// catch up and keeps a finished date from running twice.
/// </summary>
public sealed class JobRun
{
    public required string Name { get; init; }

    public required TradeDate TradeDate { get; init; }

    public JobRunStatus Status { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    public Instant StartedAt { get; set; }

    public Instant? FinishedAt { get; set; }
}
