using System.Buffers.Binary;

namespace Aion2Dps.Capture;

/// <summary>Scans contiguous, unique TCP bytes. At most ten unfinished signature bytes survive between reads.</summary>
internal sealed class HeartbeatSignatureScanner
{
    private readonly byte[] _pending = new byte[11];
    private int _length;

    public void Reset() => _length = 0;

    public int Push(ReadOnlySpan<byte> bytes, bool requireServerClock)
    {
        int hits = 0;
        foreach (byte value in bytes)
        {
            _pending[_length++] = value;
            while (_length >= 3)
            {
                bool opcode = _pending[1] == 0 && _pending[2] == 0x36;
                if (opcode && _pending[0] == 0x06 && !requireServerClock)
                {
                    hits++;
                    _length = 0;
                    break;
                }

                if (opcode && _pending[0] == 0x0E)
                {
                    if (_length < 11) break;
                    ulong clock = BinaryPrimitives.ReadUInt64LittleEndian(_pending.AsSpan(3));
                    if (!requireServerClock || clock is >= GameSignature.MinServerClockMs and <= GameSignature.MaxServerClockMs)
                    {
                        hits++;
                        _length = 0;
                        break;
                    }
                }

                _pending.AsSpan(1, --_length).CopyTo(_pending);
            }
        }

        return hits;
    }
}
