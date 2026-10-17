using ITrade.Application.Tax;
using ITrade.Infrastructure.Tax;

namespace ITrade.Host.Tax;

internal static class TaxSetup
{
    /// <summary>
    /// The tax parameters are read from the repository's config/tax.toml the first time something needs them, not
    /// at start: a Host run without the file starts, and only a tax figure fails - naming the file.
    /// </summary>
    public static void AddTax(this IServiceCollection services)
    {
        services.AddSingleton(_ => TaxParametersFile.Read(Locate()));
        services.AddScoped<CapitalGains>();
    }

    /// <summary>
    /// config/tax.toml as the commands find their files: from the folder the Host runs in upwards (`dotnet run`
    /// starts it in src/ITrade.Host), then from the folder of the executable. A result names the file it used.
    /// </summary>
    public static string Locate() => TaxParametersFile.Locate(Directory.GetCurrentDirectory(), AppContext.BaseDirectory);
}
