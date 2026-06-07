using System;

namespace Navmesh;

public static class NavmeshDiagnostics
{
    public static Action<string>? DebugSink { get; set; }
    public static Action<string>? InfoSink { get; set; }
    public static Action<string>? ErrorSink { get; set; }
    public static Func<float> RandomnessMultiplierProvider { get; set; } = static () => 1f;

    public static float RandomnessMultiplier => RandomnessMultiplierProvider();
    public static bool IsDebugEnabled => DebugSink != null;

    public static void Debug(string message) => DebugSink?.Invoke(message);
    public static void Info(string message) => InfoSink?.Invoke(message);
    public static void Error(string message) => ErrorSink?.Invoke(message);
}
