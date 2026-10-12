namespace ITrade.Domain;

/// <summary>What kind of place holds the owner's money.</summary>
public enum HoldingPlaceKind
{
    /// <summary>A brokerage account: IBKR, an Israeli broker.</summary>
    Broker,

    /// <summary>Keren hishtalmut (קרן השתלמות): a study fund with its own tax rules.</summary>
    StudyFund,

    /// <summary>Anything else the owner wants to see in the totals.</summary>
    Other,
}

/// <summary>How the platform learns what a holding place contains.</summary>
public enum IntegrationKind
{
    /// <summary>The owner types it in or imports a statement file.</summary>
    Manual,

    /// <summary>Read from the broker (statements and the API) and reconciled with it.</summary>
    Broker,
}

/// <summary>
/// A place where the owner's money is held: the IBKR account, keren hishtalmut, another broker (PLAN §3).
/// Only a brokerage account can be read automatically; everything else is entered by hand.
/// </summary>
public sealed record HoldingPlace
{
    public const int MaxNameLength = 100;

    public HoldingPlace(HoldingPlaceId id, string name, HoldingPlaceKind kind, IntegrationKind integration)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A holding place needs an id.", nameof(id));
        }

        var title = name?.Trim() ?? "";
        if (title.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException(
                $"A holding place needs a name of 1 to {MaxNameLength} characters.", nameof(name));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of holding place.");
        }

        if (!Enum.IsDefined(integration))
        {
            throw new ArgumentOutOfRangeException(nameof(integration), integration, "Unknown kind of integration.");
        }

        if (integration == IntegrationKind.Broker && kind != HoldingPlaceKind.Broker)
        {
            throw new ArgumentException(
                $"Only a brokerage account can be read automatically; a {kind} holding place is entered by hand.",
                nameof(integration));
        }

        Id = id;
        Name = title;
        Kind = kind;
        Integration = integration;
    }

    public HoldingPlaceId Id { get; }

    public string Name { get; }

    public HoldingPlaceKind Kind { get; }

    public IntegrationKind Integration { get; }

    /// <summary>True when the platform's records are the only record it can check: nothing to reconcile with.</summary>
    public bool IsManual => Integration == IntegrationKind.Manual;
}
