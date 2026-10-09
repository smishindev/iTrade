namespace ITrade.Domain;

/// <summary>Time-ordered identifiers (UUIDv7): unique without a database round trip, and sortable by creation.</summary>
public static class Ids
{
    public static Guid NewV7() => Guid.CreateVersion7();
}

/// <summary>An instrument (ETF, fund) the platform tracks.</summary>
public readonly record struct InstrumentId(Guid Value)
{
    public static InstrumentId New() => new(Ids.NewV7());

    public override string ToString() => Value.ToString();
}

/// <summary>A place where money is held: the IBKR account, keren hishtalmut, another broker.</summary>
public readonly record struct HoldingPlaceId(Guid Value)
{
    public static HoldingPlaceId New() => new(Ids.NewV7());

    public override string ToString() => Value.ToString();
}

/// <summary>One accounting transaction (buy, sell, dividend, tax withheld, fee, FX, deposit, withdrawal).</summary>
public readonly record struct TransactionId(Guid Value)
{
    public static TransactionId New() => new(Ids.NewV7());

    public override string ToString() => Value.ToString();
}

/// <summary>An order the platform proposed and the owner approved (idempotency key for the broker).</summary>
public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Ids.NewV7());

    public override string ToString() => Value.ToString();
}
