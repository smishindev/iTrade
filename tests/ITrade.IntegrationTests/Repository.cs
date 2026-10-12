namespace ITrade.IntegrationTests;

/// <summary>Files of the repository the tests compare against (settings, the OpenAPI snapshot).</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string PathTo(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ITrade.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName
            ?? throw new InvalidOperationException($"ITrade.slnx was not found above {AppContext.BaseDirectory}.");
    }
}
