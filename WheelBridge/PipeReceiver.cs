using System.IO.Pipes;

namespace WheelBridge;

/// <summary>
/// Hosts \\.\pipe\swgr_wheelbridge and decodes frames from the in-game exporter.
/// The exporter reconnects on its own, so this just loops: wait, read until EOF, repeat.
/// </summary>
public sealed class PipeReceiver(string pipeName, Action<TelemetryFrame, byte[]> onFrame)
{
    public bool Connected { get; private set; }
    public long FramesReceived { get; private set; }
    public long BadFrames { get; private set; }
    public string? LastError { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // CurrentUserOnly: only processes of this Windows user (the game) may connect; other accounts can't feed fake telemetry.
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, inBufferSize: 1 << 16, outBufferSize: 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process already owns the pipe name (a second bridge, or something squatting it).
                LastError = "can't create the telemetry pipe: " + ex.Message;
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                    Connected = true;
                    await ReadFramesAsync(pipe, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException ex) { LastError = ex.Message; }
                catch (Exception ex) { LastError = $"{ex.GetType().Name}: {ex.Message}"; }   // keep serving the game whatever happens
                finally { Connected = false; }
            }
        }
    }

    private async Task ReadFramesAsync(Stream pipe, CancellationToken ct)
    {
        var buf = new byte[TelemetryFrame.Size * 64];
        int filled = 0;
        while (true)
        {
            int n = await pipe.ReadAsync(buf.AsMemory(filled), ct);
            if (n == 0) return;
            filled += n;

            int pos = 0;
            while (filled - pos >= TelemetryFrame.Size)
            {
                var span = buf.AsSpan(pos, TelemetryFrame.Size);
                var frame = TelemetryFrame.Parse(span, out var error);
                if (frame is null)
                {
                    // Resync: skip to the next magic, or one byte if none is in view.
                    BadFrames++;
                    LastError = error;
                    int next = buf.AsSpan(pos + 1, filled - pos - 1).IndexOf(TelemetryFrame.Magic);
                    pos = next < 0 ? filled - 3 : pos + 1 + next;
                    continue;
                }
                FramesReceived++;
                try { onFrame(frame, span.ToArray()); }
                catch (Exception ex) { LastError = $"frame handler: {ex.GetType().Name}: {ex.Message}"; }
                pos += TelemetryFrame.Size;
            }
            Buffer.BlockCopy(buf, pos, buf, 0, filled - pos);
            filled -= pos;
        }
    }
}
