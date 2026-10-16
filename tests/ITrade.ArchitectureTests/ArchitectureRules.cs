using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Loader;

namespace ITrade.ArchitectureTests;

/// <summary>
/// PLAN v4 §3.1: dependency and API rules, checked in every build.
/// Rules inspect the dependency targets of the solution's own types by full name. (ArchUnitNET's fluent
/// "NotDependOnAny(Types()...)" only sees types of the loaded assemblies, so a forbidden system type such as
/// HttpClient would never match — proven with a deliberate violation, P2.1.05.)
/// </summary>
public sealed partial class ArchitectureRules
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(Domain.AssemblyMarker).Assembly,
            typeof(Application.AssemblyMarker).Assembly,
            typeof(Infrastructure.AssemblyMarker).Assembly,
            typeof(Broker.IBKR.AssemblyMarker).Assembly,
            typeof(Host.AssemblyMarker).Assembly)
        .Build();

    [GeneratedRegex(@"^ITrade\.Domain(\..*)?$")]
    private static partial Regex DomainNamespace();

    [GeneratedRegex(@"^ITrade\.Application(\..*)?$")]
    private static partial Regex ApplicationNamespace();

    private static IEnumerable<IType> TypesIn(Regex ns) =>
        Architecture.Types.Where(t => ns.IsMatch(t.Namespace.FullName));

    /// <summary>"Source -> target" for every dependency of <paramref name="types"/> whose target matches.</summary>
    private static List<string> Violations(IEnumerable<IType> types, Regex forbiddenTarget) =>
        types
            .SelectMany(t => t.Dependencies.Select(d => (Source: t.FullName, Target: d.Target.FullName)))
            .Where(d => forbiddenTarget.IsMatch(d.Target))
            .Select(d => $"{d.Source} -> {d.Target}")
            .Distinct()
            .ToList();

    [Fact]
    public void DomainDependsOnNothingElseInTheSolution()
    {
        var bad = Violations(TypesIn(DomainNamespace()),
            new Regex(@"^ITrade\.(Application|Infrastructure|Broker|Host)\."));
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    [Fact]
    public void ApplicationDoesNotDependOnInfrastructureBrokerOrHost()
    {
        var bad = Violations(TypesIn(ApplicationNamespace()),
            new Regex(@"^ITrade\.(Infrastructure|Broker|Host)\."));
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    [Fact]
    public void DomainHasNoClockNetworkDatabaseOrFiles()
    {
        // Domain is pure: time comes in as a parameter (the clock is NodaTime.IClock, injected outside Domain),
        // and dates are NodaTime types - System.DateOnly / TimeOnly carry no calendar or zone discipline.
        var clock = TypesIn(DomainNamespace())
            .SelectMany(t => t.Dependencies.OfType<MethodCallDependency>()
                .Select(d => (Source: t.FullName, Member: d.TargetMember.FullName)))
            .Where(d => Regex.IsMatch(d.Member, @"System\.(DateTime|DateTimeOffset)::get_(Now|UtcNow|Today)\("))
            .Select(d => $"{d.Source} calls {d.Member}")
            .ToList();
        var io = Violations(TypesIn(DomainNamespace()), new Regex(
            @"^(System\.Net\.Http\.|System\.IO\.(File|Directory|FileInfo|DirectoryInfo|Stream)\b|Microsoft\.EntityFrameworkCore\.|NodaTime\.SystemClock$|System\.(DateOnly|TimeOnly)$)"));
        var bad = clock.Concat(io).ToList();
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    [Fact]
    public void OnlyTheIbkrAdapterUsesTheIbkrApi()
    {
        var bad = Violations(TypesIn(new Regex(@"^ITrade\.(Domain|Application|Infrastructure|Host)(\..*)?$")),
            new Regex(@"^IBApi\."));
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    [Fact]
    public void OnlyTheTransactionLedgerWritesTheLedger()
    {
        // P3.2.02: an import, a command or a job records through TransactionLedger, which checks every entry
        // against the ledger first. Calls from inside an async method sit in a nested state machine type, hence
        // the prefix. The callers found must include the ledger itself, or the rule would see nothing at all.
        var callers = Architecture.Types
            .SelectMany(t => t.Dependencies.OfType<MethodCallDependency>()
                .Select(d => (Source: t.FullName, Member: d.TargetMember.FullName)))
            .Where(d => d.Member.Contains("ITrade.Application.Portfolio.ITransactionStore::AddAsync(", StringComparison.Ordinal))
            .Select(d => d.Source)
            .Distinct()
            .ToList();
        Assert.Contains(callers, c => c.StartsWith("ITrade.Application.Portfolio.TransactionLedger", StringComparison.Ordinal));
        var bad = callers.Where(c => !c.StartsWith("ITrade.Application.Portfolio.TransactionLedger", StringComparison.Ordinal)).ToList();
        Assert.True(bad.Count == 0, "only TransactionLedger may add to the ledger: " + string.Join(", ", bad));

        // And nothing goes around the store to the table: only the store and the database context know its rows.
        var rows = Violations(
                Architecture.Types.Where(t => !Regex.IsMatch(
                    t.FullName, @"^ITrade\.Infrastructure\.Persistence\.(TransactionStore|ITradeDbContext|TransactionRow)\b")),
                new Regex(@"^ITrade\.Infrastructure\.Persistence\.TransactionRow$"));
        Assert.True(rows.Count == 0, string.Join(Environment.NewLine, rows));
    }

    [Fact]
    public void OnlyTheExecutionServiceSendsOrders()
    {
        // IOrderSender arrives with the broker work (P5.2.04); the rule is in force from the start.
        var bad = Architecture.Types
            .Where(t => t.Name is not ("IOrderSender" or "ExecutionService"))
            .SelectMany(t => t.Dependencies.Select(d => (Source: t.FullName, Target: d.Target.Name)))
            .Where(d => d.Target == "IOrderSender")
            .Select(d => d.Source)
            .Distinct()
            .ToList();
        Assert.True(bad.Count == 0, "only ExecutionService may use IOrderSender: " + string.Join(", ", bad));
    }
}
