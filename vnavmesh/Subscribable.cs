using System;
using System.Collections.Generic;
using System.Linq;

namespace Navmesh;

public abstract class Subscribable<TVal> : IObservable<TVal>, IDisposable
{
    protected readonly List<Subscription<TVal>> _subscribers = [];

    public IDisposable Subscribe(IObserver<TVal> observer)
    {
        var sub = new Subscription<TVal>(this, observer);
        var cnt = _subscribers.Count;
        _subscribers.Add(sub);
        if (cnt == 0)
        {
            Service.Log.Verbose($"{GetType()}: OnSubscribeFirst");
            OnSubscribeFirst();
        }
        return sub;
    }

    public void Unsubscribe(Subscription<TVal> subscriber)
    {
        _subscribers.Remove(subscriber);
        if (_subscribers.Count == 0)
        {
            Service.Log.Verbose($"{GetType()}: OnUnsubscribeAll");
            OnUnsubscribeAll();
        }
    }

    protected virtual void OnSubscribeFirst() { }
    protected virtual void OnUnsubscribeAll() { }

    protected void Notify(TVal val)
    {
        foreach (var s in _subscribers.ToList())
            s.Observer.OnNext(val);
    }

    protected void NotifyError(Exception e)
    {
        foreach (var s in _subscribers.ToList())
            s.Observer.OnError(e);
    }

    public virtual void Dispose(bool disposing) { }
    public void Dispose()
    {
        _subscribers.Clear();
        OnUnsubscribeAll();
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

public sealed class Subscription<TVal>(Subscribable<TVal>? parent, IObserver<TVal> observer) : IDisposable
{
    private Subscribable<TVal>? parent = parent;
    public IObserver<TVal> Observer { get; } = observer;

    public void Dispose()
    {
        parent?.Unsubscribe(this);
        parent = null;
    }
}
