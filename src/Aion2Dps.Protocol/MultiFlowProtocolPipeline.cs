using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

/// <summary>Per-flow protocol counters of a <see cref="MultiFlowProtocolPipeline"/>.</summary>
/// <param name="FlowKey">Key the flow was created with (<see cref="MultiFlowProtocolPipeline.DefaultFlowKey"/> for the
/// single-stream <see cref="MultiFlowProtocolPipeline.Input"/>).</param>
/// <param name="IsOpen">False once released.</param>
/// <param name="CreatedOrder">1 for the first flow of the pipeline, 2 for the next, …</param>
/// <param name="FirstDataUtc">Capture time of the flow's first bytes.</param>
/// <param name="LastDataUtc">Capture time of the flow's latest bytes.</param>
/// <param name="BytesIn">Stream bytes fed to the flow's frame decoder.</param>
/// <param name="Frames">Frames delivered (top level and inside bundles).</param>
/// <param name="Bundles">LZ4 bundles unpacked.</param>
/// <param name="Events">Game events emitted from the flow's frames.</param>
/// <param name="DecodeErrors">Frames of the flow that failed to decode.</param>
/// <param name="Resyncs">Times the flow's framer had to resynchronise.</param>
/// <param name="TcpGaps">TCP gaps reported for the flow.</param>
/// <param name="NewConnections">NewConnection discontinuities reported for the flow.</param>
public sealed record FlowProtocolStats(
    string FlowKey,
    bool IsOpen,
    int CreatedOrder,
    DateTime? FirstDataUtc,
    DateTime? LastDataUtc,
    long BytesIn,
    long Frames,
    long Bundles,
    long Events,
    long DecodeErrors,
    long Resyncs,
    long TcpGaps,
    long NewConnections);

/// <summary>
/// Multi-flow composition of the protocol layer (LIVE-FINDINGS NEW 1): one <see cref="FrameDecoder"/> per game flow
/// (each connection is framed independently), all feeding ONE <see cref="PacketDecoder"/> → the given
/// <see cref="IGameEventSink"/> (entity ids are shared across the world and instance connections, so one combat engine
/// is correct), with one shared <see cref="ProtocolDiagnostics"/> plus per-flow counters (<see cref="GetFlowStats"/>).
/// <para>Capture/replay use it through <see cref="IStreamSinkFactory"/> (<see cref="CreateFlowSink"/> /
/// <see cref="ReleaseFlowSink"/>). <see cref="Input"/> is both an <see cref="IStreamSink"/> (a single default flow, for
/// callers that feed one stream, e.g. the simulator) and the factory, so it can be passed to
/// <see cref="ICaptureService.Start"/> as is: session-wide discontinuities (<see cref="DiscontinuityReason.ReplayStarted"/>,
/// <see cref="DiscontinuityReason.CaptureRestarted"/>) sent to it reset every flow; <see cref="DiscontinuityReason.TcpGap"/>
/// and <see cref="DiscontinuityReason.NewConnection"/> affect only the flow they are sent to.</para>
/// <para>Calls are expected on one thread in capture order (the engine is fed synchronously); they are additionally
/// serialised by a lock, so a misbehaving caller cannot corrupt decoder state. Counter reads are thread-safe.</para>
/// </summary>
public sealed class MultiFlowProtocolPipeline : IStreamSinkFactory
{
    /// <summary>Flow key used by <see cref="Input"/>'s own <see cref="IStreamSink"/> methods.</summary>
    public const string DefaultFlowKey = "default";

    /// <summary>How many released flows <see cref="GetFlowStats"/> keeps.</summary>
    public const int ClosedFlowHistory = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, FlowState> _open = new(StringComparer.Ordinal);
    private readonly Queue<FlowState> _closed = new();
    private readonly FrameDecoderOptions? _frameOptions;
    private readonly Action<string?, DiscontinuityReason>? _onDiscontinuity;
    private int _created;
    private int _maxConcurrent;
    private long _callbackErrors;

    /// <param name="sink">Receives decoded game events of every flow (e.g. the combat engine).</param>
    /// <param name="opcodes">Opcode table; null = <see cref="OpcodeTable.Default"/>. Hosts normally pass
    /// <see cref="OpcodeTable.LoadOrDefault()"/>.</param>
    /// <param name="options">Framing options for every flow's decoder; null = defaults.</param>
    /// <param name="onDiscontinuity">Called (on the capture thread, before the decoders react) for every discontinuity:
    /// the flow key and the reason, or a null key for a session-wide one (replay/capture restart). Use it e.g. to call
    /// <c>CombatEngine.NotifyCaptureGap</c> on a <see cref="DiscontinuityReason.TcpGap"/> of any flow.</param>
    public MultiFlowProtocolPipeline(IGameEventSink sink, OpcodeTable? opcodes = null, FrameDecoderOptions? options = null,
        Action<string?, DiscontinuityReason>? onDiscontinuity = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Opcodes = opcodes ?? OpcodeTable.Default;
        ProtocolDiagnostics = new ProtocolDiagnostics();
        PacketDecoder = new PacketDecoder(sink, Opcodes, ProtocolDiagnostics);
        _frameOptions = options;
        _onDiscontinuity = onDiscontinuity;
        Input = new InputSink(this);
    }

    /// <summary>Pass this to capture/replay: an <see cref="IStreamSink"/> (default flow + session discontinuities) that is
    /// also this pipeline's <see cref="IStreamSinkFactory"/>.</summary>
    public IStreamSink Input { get; }

    /// <summary>Shared live counters of all flows (thread-safe reads).</summary>
    public IProtocolDiagnostics Diagnostics => ProtocolDiagnostics;

    /// <summary>The concrete shared diagnostics (extra counters, census over all flows).</summary>
    public ProtocolDiagnostics ProtocolDiagnostics { get; }

    /// <summary>The single packet decoder shared by every flow.</summary>
    public PacketDecoder PacketDecoder { get; }

    public OpcodeTable Opcodes { get; }

    /// <summary>Flows currently open (the default flow counts once it received data).</summary>
    public int OpenFlowCount
    {
        get { lock (_gate) return _open.Count; }
    }

    /// <summary>Flows created since construction.</summary>
    public int TotalFlowCount
    {
        get { lock (_gate) return _created; }
    }

    /// <summary>Largest number of flows that were open at the same time.</summary>
    public int MaxConcurrentFlows
    {
        get { lock (_gate) return _maxConcurrent; }
    }

    /// <summary>Exceptions thrown by the discontinuity callback (caught).</summary>
    public long CallbackErrors => Interlocked.Read(ref _callbackErrors);

    /// <inheritdoc />
    public IStreamSink CreateFlowSink(string flowKey)
    {
        ArgumentNullException.ThrowIfNull(flowKey);
        lock (_gate)
        {
            if (_open.TryGetValue(flowKey, out var existing))
            {
                // A key reused without a release: treat it as a new connection on that flow.
                AppLog.Warn("Protocol", $"Flow {flowKey} created twice without release; restarting its stream");
                Retire(existing);
            }

            return Open(flowKey).Sink;
        }
    }

    /// <inheritdoc />
    public void ReleaseFlowSink(string flowKey)
    {
        if (flowKey is null) return;
        lock (_gate)
        {
            if (_open.TryGetValue(flowKey, out var flow)) Retire(flow);
        }
    }

    /// <summary>A session-wide discontinuity: every open flow drops its partial-frame state.</summary>
    public void OnSessionDiscontinuity(DiscontinuityReason reason)
    {
        lock (_gate)
        {
            Notify(null, reason);
            foreach (var flow in _open.Values) flow.Decoder.OnDiscontinuity(reason);
        }
    }

    /// <summary>Per-flow counters: open flows first (creation order), then up to <see cref="ClosedFlowHistory"/> released ones.</summary>
    public IReadOnlyList<FlowProtocolStats> GetFlowStats()
    {
        lock (_gate)
        {
            var list = new List<FlowProtocolStats>(_open.Count + _closed.Count);
            foreach (var f in _open.Values.OrderBy(f => f.Order)) list.Add(f.ToStats(true));
            foreach (var f in _closed) list.Add(f.ToStats(false));
            return list;
        }
    }

    /// <summary>The frame decoder of an open flow (tests/diagnostics), or null.</summary>
    public FrameDecoder? GetFrameDecoder(string flowKey)
    {
        lock (_gate) return _open.TryGetValue(flowKey, out var f) ? f.Decoder : null;
    }

    // ───────────────────────────── internals (all under _gate) ─────────────────────────────

    private FlowState Open(string key)
    {
        var flow = new FlowState(this, key, ++_created,
            new FrameDecoder(PacketDecoder, Opcodes, ProtocolDiagnostics, _frameOptions));
        _open[key] = flow;
        if (_open.Count > _maxConcurrent) _maxConcurrent = _open.Count;
        AppLog.Debug("Protocol", $"Flow {key} opened ({_open.Count} open)");
        return flow;
    }

    private void Retire(FlowState flow)
    {
        flow.Released = true;
        _open.Remove(flow.Key);
        _closed.Enqueue(flow);
        while (_closed.Count > ClosedFlowHistory) _closed.Dequeue();
        AppLog.Debug("Protocol", $"Flow {flow.Key} released ({flow.Frames} frames, {flow.Events} events, {flow.DecodeErrors} decode errors)");
    }

    private FlowState GetOrOpenDefault() => _open.TryGetValue(DefaultFlowKey, out var f) ? f : Open(DefaultFlowKey);

    private void Notify(string? key, DiscontinuityReason reason)
    {
        if (_onDiscontinuity is null) return;
        try
        {
            _onDiscontinuity(key, reason);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _callbackErrors) <= 10)
                AppLog.Error("Protocol", "Discontinuity callback failed", ex);
        }
    }

    private void FlowData(FlowState flow, DateTime timeUtc, ReadOnlySpan<byte> data)
    {
        if (flow.Released || data.IsEmpty) return;
        var d = ProtocolDiagnostics;
        long frames = d.Frames, events = d.EventsEmitted, errors = d.DecodeErrors, resyncs = d.Resyncs, bundles = d.Bundles;
        flow.FirstDataUtc ??= timeUtc;
        flow.LastDataUtc = timeUtc;
        flow.BytesIn += data.Length;
        flow.Decoder.OnData(timeUtc, data);
        flow.Frames += d.Frames - frames;
        flow.Events += d.EventsEmitted - events;
        flow.DecodeErrors += d.DecodeErrors - errors;
        flow.Resyncs += d.Resyncs - resyncs;
        flow.Bundles += d.Bundles - bundles;
    }

    private void FlowDiscontinuity(FlowState flow, DiscontinuityReason reason)
    {
        if (flow.Released) return;
        if (reason is DiscontinuityReason.ReplayStarted or DiscontinuityReason.CaptureRestarted)
        {
            // A session-wide reason sent to one flow's sink still means the whole session restarted.
            OnSessionDiscontinuity(reason);
            return;
        }

        if (reason == DiscontinuityReason.TcpGap) flow.TcpGaps++;
        else if (reason == DiscontinuityReason.NewConnection) flow.NewConnections++;
        long resyncs = ProtocolDiagnostics.Resyncs;
        Notify(flow.Key, reason);
        flow.Decoder.OnDiscontinuity(reason);
        flow.Resyncs += ProtocolDiagnostics.Resyncs - resyncs;
    }

    private sealed class FlowState
    {
        public FlowState(MultiFlowProtocolPipeline owner, string key, int order, FrameDecoder decoder)
        {
            Key = key;
            Order = order;
            Decoder = decoder;
            Sink = new FlowSink(owner, this);
        }

        public string Key { get; }
        public int Order { get; }
        public FrameDecoder Decoder { get; }
        public IStreamSink Sink { get; }
        public bool Released { get; set; }
        public DateTime? FirstDataUtc { get; set; }
        public DateTime? LastDataUtc { get; set; }
        public long BytesIn { get; set; }
        public long Frames { get; set; }
        public long Bundles { get; set; }
        public long Events { get; set; }
        public long DecodeErrors { get; set; }
        public long Resyncs { get; set; }
        public long TcpGaps { get; set; }
        public long NewConnections { get; set; }

        public FlowProtocolStats ToStats(bool open) => new(Key, open, Order, FirstDataUtc, LastDataUtc, BytesIn, Frames, Bundles,
            Events, DecodeErrors, Resyncs, TcpGaps, NewConnections);
    }

    /// <summary>The sink handed out for one flow. Calls after the release are ignored.</summary>
    private sealed class FlowSink(MultiFlowProtocolPipeline owner, FlowState flow) : IStreamSink
    {
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
        {
            lock (owner._gate) owner.FlowData(flow, timeUtc, data);
        }

        public void OnDiscontinuity(DiscontinuityReason reason)
        {
            lock (owner._gate) owner.FlowDiscontinuity(flow, reason);
        }
    }

    /// <summary><see cref="Input"/>: default flow for single-stream callers, session discontinuities, and the factory.</summary>
    private sealed class InputSink(MultiFlowProtocolPipeline owner) : IStreamSink, IStreamSinkFactory
    {
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            lock (owner._gate) owner.FlowData(owner.GetOrOpenDefault(), timeUtc, data);
        }

        public void OnDiscontinuity(DiscontinuityReason reason)
        {
            if (reason is DiscontinuityReason.ReplayStarted or DiscontinuityReason.CaptureRestarted)
            {
                owner.OnSessionDiscontinuity(reason);
                return;
            }

            lock (owner._gate) owner.FlowDiscontinuity(owner.GetOrOpenDefault(), reason);
        }

        public IStreamSink CreateFlowSink(string flowKey) => owner.CreateFlowSink(flowKey);

        public void ReleaseFlowSink(string flowKey) => owner.ReleaseFlowSink(flowKey);
    }
}
