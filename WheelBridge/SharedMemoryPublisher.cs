using System.IO.MemoryMappedFiles;

namespace WheelBridge;

/// <summary>
/// Mirrors the latest raw frame into Local\SWGR_WheelBridge for other tools (FFB loop, overlays).
/// Layout: uint32 sequence lock (odd while writing), uint32 frame size, then the raw frame bytes
/// exactly as sent by the exporter (see TelemetryFrame for the format).
/// </summary>
public sealed class SharedMemoryPublisher : IDisposable
{
    public const string MapName = @"Local\SWGR_WheelBridge";
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private uint _lock;

    public SharedMemoryPublisher()
    {
        _map = MemoryMappedFile.CreateOrOpen(MapName, 8 + TelemetryFrame.Size);
        _view = _map.CreateViewAccessor();
        _view.Write(4, (uint)TelemetryFrame.Size);
    }

    public void Publish(byte[] rawFrame)
    {
        _view.Write(0, ++_lock);
        Thread.MemoryBarrier();
        _view.WriteArray(8, rawFrame, 0, rawFrame.Length);
        Thread.MemoryBarrier();
        _view.Write(0, ++_lock);
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }
}
