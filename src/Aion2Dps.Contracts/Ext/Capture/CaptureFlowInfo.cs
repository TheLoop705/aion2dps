namespace Aion2Dps.Contracts;

/// <summary>One game flow (TCP connection to a game server) followed by capture or replay.</summary>
/// <param name="FlowKey">Key passed to <see cref="IStreamSinkFactory.CreateFlowSink"/>.</param>
/// <param name="ServerEndpoint">Game server endpoint, e.g. <c>193.202.112.155:13328</c>.</param>
/// <param name="LocalEndpoint">Local endpoint of the connection.</param>
/// <param name="ByHint">True when the flow was named by the game process's connection table.</param>
/// <param name="LockedAtUtc">Capture time the flow was recognised.</param>
/// <param name="LastPacketUtc">Capture time of its latest packet.</param>
/// <param name="Packets">Packets of the flow (both directions, including the pre-lock buffer).</param>
/// <param name="BytesDelivered">Reassembled server→client bytes delivered to the flow sink.</param>
/// <param name="Gaps">TCP gaps of the flow.</param>
/// <param name="IsOpen">False once the flow ended (FIN/RST or idle) and its sink was released.</param>
/// <param name="EndReason">Why the flow ended, or null while open.</param>
public sealed record CaptureFlowInfo(
    string FlowKey,
    string ServerEndpoint,
    string LocalEndpoint,
    bool ByHint,
    DateTime LockedAtUtc,
    DateTime? LastPacketUtc,
    long Packets,
    long BytesDelivered,
    long Gaps,
    bool IsOpen,
    string? EndReason);

/// <summary>Optional interface of capture services that follow several game flows at once (the
/// <see cref="CaptureStatus"/> record carries only joined endpoint strings).</summary>
public interface ICaptureFlowStatus
{
    /// <summary>Number of game flows currently open.</summary>
    int OpenFlowCount { get; }

    /// <summary>Snapshot of the open flows (and recently ended ones), thread-safe.</summary>
    IReadOnlyList<CaptureFlowInfo> GetFlows();
}
