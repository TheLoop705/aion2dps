using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

/// <summary>
/// Decodes frames into typed <see cref="GameEvent"/>s (PROTOCOL.md §7/§8) and passes them to an
/// <see cref="IGameEventSink"/>. Dispatch goes through the <see cref="OpcodeTable"/>, so a patch that moves an opcode
/// is fixed by editing <c>data/protocol/opcodes.json</c>.
/// <para>Never throws out of <see cref="OnFrame"/>: malformed frames are counted (census "failed"), recorded in the
/// recent-error ring of <see cref="ProtocolDiagnostics"/>, and skipped. Not thread-safe (capture thread only).</para>
/// </summary>
public sealed partial class PacketDecoder : IFrameSink
{
    private enum Handler : byte
    {
        None = 0,
        Heartbeat,
        Damage,
        Dot,
        EntityStats,
        Spawn,
        SelfInfo,
        PlayerInfo,
        Kill,
        Death,
        MapLoad,
        Teleport,
        PartyRoster,
        HpUpdate,
        BuffApplied,
        BuffRemoved,
        Cast,
        Ping,
        GlobalIdLink,
        BattleToggle,
        /// <summary>Recognised opcode without a Contracts event (01 97, 06 38, 01 91): counted, not decoded.</summary>
        Ignored,
    }

    /// <summary>Amounts above this are placeholders, not damage (§8.2.2).</summary>
    public const long AmountCap = 99_999_999;

    /// <summary>Entity id plausibility range used by validators (§6.2).</summary>
    public const uint MaxEntityId = 9_999_999;

    private readonly IGameEventSink _sink;
    private readonly OpcodeTable _ops;
    private readonly ProtocolDiagnostics _diag;
    private readonly Handler[] _handlers = new Handler[65536];

    // Per-frame context (set in OnFrame).
    private DateTime _time;
    private int _depth;
    private ushort _opcode;
    private string? _failReason;

    /// <param name="sink">Receives the decoded events.</param>
    /// <param name="opcodes">Dispatch table. Default: <see cref="OpcodeTable.Default"/>.</param>
    /// <param name="diagnostics">Counters (shared with the frame decoder). Default: a new instance.</param>
    public PacketDecoder(IGameEventSink sink, OpcodeTable? opcodes = null, ProtocolDiagnostics? diagnostics = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _ops = opcodes ?? OpcodeTable.Default;
        _diag = diagnostics ?? new ProtocolDiagnostics();
        BuildDispatch();
    }

    public ProtocolDiagnostics Diagnostics => _diag;
    public OpcodeTable Opcodes => _ops;

    /// <summary>When true (default), damage frames try to chain further records after the first (§8.2 #16), each
    /// strictly validated.</summary>
    public bool ChainDamageRecords { get; set; } = true;

    private void BuildDispatch()
    {
        // Lowest priority first, so that a misconfigured duplicate resolves to the more important decoder.
        Set(_ops.FieldBossList, Handler.Ignored);
        Set(_ops.PartyScope, Handler.Ignored);
        Set(_ops.OtherPartyRoster, Handler.Ignored);
        Set(_ops.BattleToggle, Handler.BattleToggle);
        Set(_ops.GlobalIdLink, Handler.GlobalIdLink);
        Set(_ops.Ping, Handler.Ping);
        Set(_ops.Cast, Handler.Cast);
        Set(_ops.BuffRemoved, Handler.BuffRemoved);
        Set(_ops.BuffApplied2, Handler.BuffApplied);
        Set(_ops.BuffApplied, Handler.BuffApplied);
        Set(_ops.HpUpdate, Handler.HpUpdate);
        Set(_ops.PartyRoster, Handler.PartyRoster);
        Set(_ops.Teleport, Handler.Teleport);
        Set(_ops.MapLoad, Handler.MapLoad);
        Set(_ops.Death, Handler.Death);
        Set(_ops.Kill, Handler.Kill);
        Set(_ops.PlayerInfo, Handler.PlayerInfo);
        Set(_ops.SelfInfo, Handler.SelfInfo);
        Set(_ops.Spawn, Handler.Spawn);
        Set(_ops.EntityStats, Handler.EntityStats);
        Set(_ops.DotTick, Handler.Dot);
        Set(_ops.Heartbeat, Handler.Heartbeat);
        Set(_ops.Damage, Handler.Damage);
        _handlers[Aion2Dps.Contracts.Opcodes.Bundle] = Handler.None;

        void Set(ushort op, Handler h)
        {
            if (_handlers[op] != Handler.None && _handlers[op] != h)
                AppLog.Warn("Protocol", $"Opcode {Aion2Dps.Contracts.Opcodes.Format(op)} is assigned twice in the opcode table; {h} wins.");
            _handlers[op] = h;
        }
    }

    /// <inheritdoc />
    public void OnFrame(in Frame frame)
    {
        var handler = _handlers[frame.Opcode];
        if (handler == Handler.None)
        {
            _diag.IncrementUnhandled();
            return;
        }

        _time = frame.TimeUtc;
        _depth = frame.BundleDepth;
        _opcode = frame.Opcode;
        _failReason = null;

        bool ok;
        try
        {
            ReadOnlySpan<byte> body = frame.Body.Span;
            ok = handler switch
            {
                Handler.Heartbeat => DecodeHeartbeat(body),
                Handler.Damage => DecodeDamage(body),
                Handler.Dot => DecodeDot(body),
                Handler.EntityStats => DecodeEntityStats(body),
                Handler.Spawn => DecodeSpawn(body),
                Handler.SelfInfo => DecodeSelfInfo(body),
                Handler.PlayerInfo => DecodePlayerInfo(body),
                Handler.Kill => DecodeKill(body),
                Handler.Death => DecodeDeath(body),
                Handler.MapLoad => DecodeMapLoad(body),
                Handler.Teleport => DecodeTeleport(body),
                Handler.PartyRoster => DecodePartyRoster(body),
                Handler.HpUpdate => DecodeHpUpdate(body),
                Handler.BuffApplied => DecodeBuffApplied(body),
                Handler.BuffRemoved => DecodeBuffRemoved(body),
                Handler.Cast => DecodeCast(body),
                Handler.Ping => DecodePing(body),
                Handler.GlobalIdLink => DecodeGlobalIdLink(body),
                Handler.BattleToggle => DecodeBattleToggle(body),
                Handler.Ignored => true,
                _ => true,
            };
        }
        catch (Exception ex)
        {
            ok = false;
            _failReason = $"exception {ex.GetType().Name}: {ex.Message}";
        }

        if (ok)
        {
            _diag.RecordDecoded(frame.Opcode);
        }
        else
        {
            _diag.RecordFailed(frame.Opcode);
            _diag.RecordError(frame.TimeUtc,
                $"{Aion2Dps.Contracts.Opcodes.Format(frame.Opcode)} d{frame.BundleDepth} {_failReason ?? "malformed"}: {Hex.Format(frame.Body.Span, 48)}");
        }
    }

    /// <summary>Records why the current frame failed; returns false for <c>return Fail(...)</c>.</summary>
    private bool Fail(string reason)
    {
        _failReason = reason;
        return false;
    }

    private void Emit(GameEvent e)
    {
        _diag.IncrementEvents();
        try
        {
            _sink.OnEvent(e);
        }
        catch (Exception ex)
        {
            _diag.IncrementSinkErrors();
            _diag.RecordError(_time, $"event sink threw on {e.GetType().Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
