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
    }

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
