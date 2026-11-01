using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NoiseReduction.Avalonia
{
    internal enum State { Paused, Idle, Working, Error, Recovering, Unavailable, Bypass, Fallback }

    internal sealed record Microphone(string Id, string Name, string SourceName, bool IsDefault, bool Available = true);

    internal sealed record RecordingApp(uint StreamId, string Id, string Name, string Icon, string SourceName, bool Corked, bool Muted, uint ClientId = 0);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BridgeMetrics
    {
        internal ulong CaptureFrames, ModelFrames, PlaybackFrames, Errors, Underruns, DroppedFrames;
        internal float InputPeak, OutputPeak;
        internal int ActualModel, Running;
    }

    internal sealed record DeviceStatus(State State, string Message, bool EffectiveDeepFilter, BridgeMetrics Metrics);
}
