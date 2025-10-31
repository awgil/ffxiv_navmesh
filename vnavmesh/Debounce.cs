using System;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public sealed class Debounce(int delayMS) : IDisposable
{
    private readonly int _delay = delayMS;
    private CancellationTokenSource? _source;

    public void Dispose()
    {
        _source?.Dispose();
    }

    public void Spawn(Func<CancellationToken, Task> action)
    {
        _source?.Cancel();
        var c = _source = new CancellationTokenSource();
        Service.Framework.Run(async () =>
        {
            await Task.Delay(_delay, c.Token);
            await action(c.Token);
        }, c.Token);
    }

    public void Spawn(Action<CancellationToken> action) => Spawn(tok =>
    {
        action(tok);
        return Task.CompletedTask;
    });
}
