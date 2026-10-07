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
    private readonly Dictionary<string, Action<object?[]>> _handlers = new(StringComparer.Ordinal);

    public static (EWrapper Wrapper, LoggingWrapper Control) Create(MessageLog log)
    {
        var wrapper = DispatchProxy.Create<EWrapper, LoggingWrapper>();
        var control = (LoggingWrapper)(object)wrapper;
        control._log = log;
        return (wrapper, control);
    }

    /// <summary>Called on the reader thread after logging; keep handlers short.</summary>
    public void On(string callback, Action<object?[]> handler) => _handlers[callback] = handler;

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

        // Overloads (e.g. three error(...) variants) share one handler keyed by name + arity.
        if (_handlers.TryGetValue($"{targetMethod.Name}/{args.Length}", out var handler)
            || _handlers.TryGetValue(targetMethod.Name, out handler))
        {
            handler(args);
        }

        return null;
    }
}
