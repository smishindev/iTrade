using System.Text;

namespace ITrade.Host.Auth;

/// <summary>
/// `ITrade.Host set-password`: sets the owner's password. The password is typed (hidden) or piped in,
/// never given as an argument — arguments end up in the shell history.
/// </summary>
internal static class SetPasswordCommand
{
    public const string Name = "set-password";

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="output">Where messages go.</param>
    /// <param name="readSecret">Shows a prompt and returns what was entered.</param>
    /// <param name="confirm">Ask twice (interactive use).</param>
    public static async Task<int> RunAsync(
        WebApplication app, TextWriter output, Func<string, string?> readSecret, bool confirm)
    {
        var password = readSecret("New password: ");
        if (OwnerAccount.PasswordProblem(password) is { } problem)
        {
            await output.WriteLineAsync(problem);
            return 1;
        }

        if (confirm && readSecret("Repeat the password: ") != password)
        {
            await output.WriteLineAsync("The passwords do not match. Nothing was changed.");
            return 1;
        }

        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccounts>();
        await accounts.SetPasswordAsync(password!, "cli", CancellationToken.None);
        await output.WriteLineAsync("The password is set. Existing sessions are signed out.");
        return 0;
    }

    /// <summary>Reads a line from the keyboard without showing it; from a pipe when input is redirected.</summary>
    public static string? ReadFromConsole(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                text.Length = Math.Max(0, text.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
