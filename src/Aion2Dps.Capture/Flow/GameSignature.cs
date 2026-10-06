namespace Aion2Dps.Capture;

/// <summary>Byte signatures used to recognise the game connection (PROTOCOL.md §2.3, §2.4).</summary>
public static class GameSignature
{
    /// <summary>The game server port in every region seen (PROTOCOL.md §1).</summary>
    public const int GameServerPort = 13328;

    /// <summary>Heartbeat frame head: varint L = 14, opcode <c>00 36</c>, followed by an 8-byte server clock.</summary>
    public static ReadOnlySpan<byte> Heartbeat => [0x0E, 0x00, 0x36];

    /// <summary>Pre-June-2026 heartbeat: L = 6, opcode <c>00 36</c>, no body.</summary>
    public static ReadOnlySpan<byte> LegacyHeartbeat => [0x06, 0x00, 0x36];

    private static ReadOnlySpan<byte> Opcode0036 => [0x00, 0x36];

    /// <summary>Plausible heartbeat server clock (Unix ms, PROTOCOL.md §8.1): 2020-01-01 ...</summary>
    public const ulong MinServerClockMs = 1_577_836_800_000UL;
    /// <summary>... up to 2100-01-01 (the protocol's "never").</summary>
    public const ulong MaxServerClockMs = 4_102_444_800_000UL;

    /// <summary>Counts heartbeat frames (<c>0E 00 36</c> + 8 bytes, or legacy <c>06 00 36</c>) in one TCP payload.
    /// With <paramref name="requireServerClock"/> only <c>0E 00 36</c> frames whose 8-byte body is a plausible Unix-ms
    /// server clock count (a strong check for flows off the game port; legacy heartbeats carry no clock).</summary>
    public static int CountHeartbeats(ReadOnlySpan<byte> payload, bool requireServerClock = false)
    {
        int count = 0;
        int i = 1;
        while (i + 2 <= payload.Length)
        {
            int idx = payload.Slice(i).IndexOf(Opcode0036);
            if (idx < 0) break;
            int p = i + idx; // position of 00 36; the varint is at p - 1
            byte lead = payload[p - 1];
            if (lead == 0x0E && p + 2 + 8 <= payload.Length)
            {
                if (requireServerClock)
                {
                    ulong clock = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(p + 2, 8));
                    if (clock is < MinServerClockMs or > MaxServerClockMs)
                    {
                        i = p + 1;
                        continue;
                    }
                }

                count++;
                i = p + 10;
                continue;
            }
            if (lead == 0x06 && !requireServerClock)
            {
                count++;
                i = p + 2;
                continue;
            }
            i = p + 1;
        }
        return count;
    }

    /// <summary>True when the payload starts like a TLS record: content type 0x14..0x17, version 03 00..04.</summary>
    public static bool LooksLikeTls(ReadOnlySpan<byte> payload) =>
        payload.Length >= 3 && payload[0] is >= 0x14 and <= 0x17 && payload[1] == 0x03 && payload[2] <= 0x04;
}
