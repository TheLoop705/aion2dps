using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>
/// Adapts a plain (single-stream) <see cref="IStreamSink"/> to <see cref="IStreamSinkFactory"/> for callers that still
/// pass one sink: only one flow at a time can feed a single framer, so the most recently created flow owns the sink
/// (it carries the current connection, e.g. the dungeon instance opened after the world connection). Data of the other
/// flows is dropped and counted. A change of owner emits <see cref="DiscontinuityReason.NewConnection"/> to the sink;
/// when the owner is released, the newest remaining flow takes over. Use a real factory (e.g. the protocol layer's
/// multi-flow pipeline) to decode every flow.
/// </summary>
public sealed class SingleSinkFlowAdapter : IStreamSinkFactory
{
    private readonly IStreamSink _sink;
    private readonly List<string> _flows = new(); // creation order, open flows only
    private string? _owner;
    private bool _everOwned;

    public SingleSinkFlowAdapter(IStreamSink sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>The wrapped sink (also used for session-wide discontinuities).</summary>
    public IStreamSink Sink => _sink;

    /// <summary>Key of the flow currently delivered to the sink.</summary>
    public string? Owner => _owner;

    /// <summary>Bytes of non-owner flows that were not delivered.</summary>
    public long DroppedBytes { get; private set; }

    public IStreamSink CreateFlowSink(string flowKey)
    {
        ArgumentNullException.ThrowIfNull(flowKey);
        _flows.Remove(flowKey);
        _flows.Add(flowKey);
        SetOwner(flowKey);
        return new FlowProxy(this, flowKey);
    }

    public void ReleaseFlowSink(string flowKey)
    {
        if (flowKey is null || !_flows.Remove(flowKey)) return;
        if (_owner != flowKey) return;
        _owner = null;
        if (_flows.Count > 0) SetOwner(_flows[^1]);
    }

    private void SetOwner(string key)
    {
        if (_owner == key) return;
        _owner = key;
        if (_everOwned) _sink.OnDiscontinuity(DiscontinuityReason.NewConnection);
        _everOwned = true;
    }

    private sealed class FlowProxy(SingleSinkFlowAdapter owner, string key) : IStreamSink
    {
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
        {
            if (owner._owner == key) owner._sink.OnData(timeUtc, data);
            else owner.DroppedBytes += data.Length;
        }

        public void OnDiscontinuity(DiscontinuityReason reason)
        {
            if (owner._owner == key) owner._sink.OnDiscontinuity(reason);
        }
    }
}
