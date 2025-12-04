using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.Interop;
using SQLite;
using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Navmesh;

public class LayoutEvent
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public long ObjectId { get; set; }
    public uint LayerId { get; set; }
    public int LayoutType { get; set; }
    public uint Zone { get; set; }
    public required string EventName { get; set; }
    public DateTime Timestamp { get; set; }
    public string Extra { get; set; } = "";
}

public class GenericEvent
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public required string EventName { get; set; }
    public int Field1 { get; set; }
    public int Field2 { get; set; }
    public int Field3 { get; set; }
    public string Extra { get; set; } = "";
}

public sealed class Slog : IDisposable
{
    private static Slog? _instance;
    private static bool _disposed;

    private readonly SQLiteConnection _conn;
    private readonly Channel<object> _channel;
    private readonly Task _writerTask;

    public bool Enabled
    {
        get => field;
        set
        {
            field = value;
            if (!value)
            {
                _channel.Writer.TryWrite(new System.Reactive.Unit());
            }
        }
    }

    public static Slog Instance()
    {
        _instance ??= new();
        return _instance;
    }

    private Slog()
    {
        if (_disposed)
            throw new InvalidOperationException($"tried to initialize Slog instance after Dispose() has been called");

        _channel = Channel.CreateUnbounded<object>();

        var dir = Service.PluginInterface.GetPluginConfigDirectory();
        _conn = new SQLiteConnection(Path.Join(dir, "meshcache", "log.db"));
        _conn.CreateTable<LayoutEvent>();
        _conn.CreateTable<GenericEvent>();

        _writerTask = Task.Run(async () =>
        {
            try
            {
                _conn.BeginTransaction();
                while (true)
                {
                    var next = await _channel.Reader.ReadAsync();
                    switch (next)
                    {
                        case LayoutEvent l:
                            _conn.Execute(@"INSERT INTO LayoutEvent (ObjectId, LayerId, LayoutType, Zone, EventName, Timestamp, Extra) VALUES (?, ?, ?, ?, ?, ?, ?)", l.ObjectId, l.LayerId, l.LayoutType, l.Zone, l.EventName, l.Timestamp, l.Extra);
                            break;
                        case GenericEvent g:
                            _conn.Execute(@"INSERT INTO GenericEvent (EventName, Field1, Field2, Field3, Extra) VALUES (?, ?, ?, ?, ?)", g.EventName, g.Field1, g.Field2, g.Field3, g.Extra);
                            break;
                        case System.Reactive.Unit:
                            _conn.Commit();
                            _conn.BeginTransaction();
                            break;
                    }
                }
            }
            catch (ChannelClosedException ex)
            {
                Service.Log.Debug(ex, $"writer side closed, no more rows");
                _conn.Commit();
            }
        });
    }

    public static void Trace(Pointer<ILayoutInstance> inst, uint zone, string eventName, string extra = "") => Instance().TraceImpl(inst, zone, eventName, extra);

    private void TraceImpl(Pointer<ILayoutInstance> inst, uint zone, string eventName, string extra = "")
    {
        if (!Enabled)
            return;

        unsafe
        {
            _channel.Writer.TryWrite(new LayoutEvent()
            {
                ObjectId = (long)SceneTool.GetKey(inst.Value),
                LayerId = inst.Value->Layer->LayerGroupId,
                LayoutType = inst.Value->Layout->Type,
                Zone = zone,
                EventName = eventName,
                Timestamp = DateTime.Now,
                Extra = extra
            });
        }
    }

    public static void TraceDelete(Pointer<ILayoutInstance> stalePtr, uint zone, string deleteReason, string extra = "") => Instance().TraceDeleteImpl(stalePtr, zone, deleteReason, extra);

    private unsafe void TraceDeleteImpl(Pointer<ILayoutInstance> stalePtr, uint zone, string deleteReason, string extra = "")
    {
        if (!Enabled)
            return;

        _channel.Writer.TryWrite(new LayoutEvent()
        {
            ObjectId = (long)stalePtr.Value,
            LayerId = 0xFFFF,
            LayoutType = 0xFF,
            Zone = zone,
            EventName = $"delete({deleteReason})",
            Timestamp = DateTime.Now,
            Extra = extra
        });
    }

    public static void LogGeneric(string eventName, int field1 = 0, int field2 = 0, int field3 = 0, string extra = "") => Instance().LogGenericImpl(eventName, field1, field2, field3, extra);

    private void LogGenericImpl(string eventName, int field1 = 0, int field2 = 0, int field3 = 0, string extra = "")
    {
        if (!Enabled)
            return;

        _channel.Writer.TryWrite(new GenericEvent() { EventName = eventName, Field1 = field1, Field2 = field2, Field3 = field3, Extra = extra });
    }

    public void Dispose()
    {
        _channel.Writer.Complete();
        try
        {
            _writerTask.Wait();
        }
        catch (AggregateException ex)
        {
            Service.Log.Warning(ex, "Logger task failed");
        }
        _writerTask.Dispose();
        _conn.Dispose();
        _disposed = true;
    }
}
