using System.Reflection;
using IBApi;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// Implements every <see cref="EWrapper"/> callback at once via <see cref="DispatchProxy"/>:
/// each call is written to the <see cref="MessageLog"/> with parameter names, then handed to
/// optional typed handlers. New callbacks in future API versions are logged automatically.
/// </summary>
public class LoggingWrapper : DispatchProxy
{
    private MessageLog? _log;
    private readonly Dictionary<string, List<Action<object?[]>>> _handlers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public static (EWrapper Wrapper, LoggingWrapper Control) Create(MessageLog log)
    {
        var wrapper = DispatchProxy.Create<EWrapper, LoggingWrapper>();
        var control = (LoggingWrapper)(object)wrapper;
        control._log = log;
        return (wrapper, control);
    }

    /// <summary>
    /// Adds a handler (several per callback are allowed). Called on the reader thread after logging;
    /// keep handlers short. Dispose the result to remove the handler.
    /// </summary>
    public IDisposable On(string callback, Action<object?[]> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(callback, out var list))
            {
                list = [];
                _handlers[callback] = list;
            }

            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _handlers[callback].Remove(handler);
            }
        });
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        args ??= [];
        var parameters = targetMethod.GetParameters();
        var fields = new List<KeyValuePair<string, object?>>(parameters.Length);
        for (var i = 0; i < parameters.Length; i++)
        {
            fields.Add(new(parameters[i].Name ?? $"arg{i}", args[i]));
        }

        _log?.Write("in", targetMethod.Name, fields);

        // Overloads (e.g. three error(...) variants) can be told apart by name + arity ("error/5").
        Action<object?[]>[] handlers;
        lock (_gate)
        {
            handlers = [.. Get($"{targetMethod.Name}/{args.Length}"), .. Get(targetMethod.Name)];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(args);
            }
            catch (Exception ex)
            {
                // A failing handler must not kill the reader thread.
                _log?.Write("internal", "handlerException", [new("callback", targetMethod.Name), new("exception", ex.ToString())]);
                Console.Error.WriteLine($"  handler for {targetMethod.Name} failed: {ex.Message}");
            }
        }

        return null;
    }

    private List<Action<object?[]>> Get(string key) => _handlers.TryGetValue(key, out var list) ? list : [];

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
