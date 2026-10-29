namespace AlertaIBT.Core.Ndi;

/// <summary>A frame taken from a <see cref="FrameMailbox"/>; valid until <see cref="FrameMailbox.Release"/>.</summary>
/// <param name="Buffer">The pixels (BGRA), at least <c>Stride * Height</c> bytes.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Stride">Bytes per row.</param>
/// <param name="VerticalPosition">The camera's vertical position (0..100) that was current when the frame was posted.</param>
/// <param name="Sequence">The number of the frame (grows by one per post).</param>
public readonly record struct MailboxFrame(byte[] Buffer, int Width, int Height, int Stride, double VerticalPosition, long Sequence);

/// <summary>
/// The hand-over between the capture thread (writes the newest frame) and the render thread (reads the newest frame), with three reusable
/// buffers: no allocation per frame in steady state, the writer never waits for the reader, and a slow reader just skips frames (the newest wins).
/// </summary>
public sealed class FrameMailbox
{
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly byte[]?[] _buffers = new byte[3][];
    private readonly (int Width, int Height, int Stride, double Position)[] _meta = new (int, int, int, double)[3];
    private int _ready = -1;
    private int _reading = -1;
    private long _sequence;

    /// <summary>The number of the newest posted frame (0 when none).</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    /// <summary>Copies <paramref name="frame"/> in as the newest frame.</summary>
    public void Post(in NdiFrameView frame, double verticalPosition)
    {
        var size = frame.Stride * frame.Height;
        if (size <= 0 || frame.Data.Length < size)
        {
            return;
        }

        lock (_writeGate)
        {
            int index;
            lock (_gate)
            {
                index = 0;
                while (index == _ready || index == _reading)
                {
                    index++;
                }
            }

            var buffer = _buffers[index];
            if (buffer is null || buffer.Length < size)
            {
                buffer = new byte[size];
                _buffers[index] = buffer;
            }

            frame.Data[..size].CopyTo(buffer);
            lock (_gate)
            {
                _meta[index] = (frame.Width, frame.Height, frame.Stride, verticalPosition);
                _ready = index;
                _sequence++;
            }
        }
    }

    /// <summary>Takes the newest frame when it is newer than <paramref name="lastSequence"/>; call <see cref="Release"/> after using it.</summary>
    public bool TryAcquire(long lastSequence, out MailboxFrame frame)
    {
        lock (_gate)
        {
            if (_ready < 0 || _sequence == lastSequence)
            {
                frame = default;
                return false;
            }

            _reading = _ready;
            var meta = _meta[_reading];
            frame = new MailboxFrame(_buffers[_reading]!, meta.Width, meta.Height, meta.Stride, meta.Position, _sequence);
            return true;
        }
    }

    /// <summary>Gives the acquired buffer back to the writer.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _reading = -1;
        }
    }
}
