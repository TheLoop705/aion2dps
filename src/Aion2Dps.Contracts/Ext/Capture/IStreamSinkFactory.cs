namespace Aion2Dps.Contracts;

/// <summary>
/// Flow-aware stream sink (LIVE-FINDINGS NEW 1): the game keeps several port-13328 connections open at once (world +
/// dungeon instance), and each one is an independent, separately framed server→client byte stream. Capture and replay
/// ask the factory for one <see cref="IStreamSink"/> per game flow, feed that flow's reassembled bytes only to it, and
/// release it when the flow ends (FIN/RST or idle).
/// <list type="bullet">
/// <item><see cref="DiscontinuityReason.TcpGap"/> and <see cref="DiscontinuityReason.NewConnection"/> are sent to the
/// affected flow's sink only.</item>
/// <item>When the object passed to <see cref="ICaptureService.Start"/> or <see cref="IReplaySource.ReplayAsync"/> is
/// both an <see cref="IStreamSink"/> and an <see cref="IStreamSinkFactory"/>, capture uses the factory for the flows and
/// sends session-wide discontinuities (<see cref="DiscontinuityReason.CaptureRestarted"/>,
/// <see cref="DiscontinuityReason.ReplayStarted"/>) to its <see cref="IStreamSink.OnDiscontinuity"/>. A plain
/// <see cref="IStreamSink"/> still works through a single-sink adapter (one flow at a time).</item>
/// <item>All calls (factory and flow sinks) are made on one thread, in capture order.</item>
/// </list>
/// </summary>
public interface IStreamSinkFactory
{
    /// <summary>Creates the sink of a newly detected game flow. <paramref name="flowKey"/> is unique among the flows
    /// that are currently open (e.g. <c>"193.202.112.155:13328>192.168.178.81:64331"</c>); it may be reused after
    /// <see cref="ReleaseFlowSink"/>.</summary>
    IStreamSink CreateFlowSink(string flowKey);

    /// <summary>The flow ended (FIN/RST, idle timeout, capture stopped): drop its partial-frame state. No further calls
    /// are made to the sink returned for <paramref name="flowKey"/>. Unknown keys are ignored.</summary>
    void ReleaseFlowSink(string flowKey);
}
