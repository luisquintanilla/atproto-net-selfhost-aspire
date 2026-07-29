using Microsoft.AspNetCore.SignalR;

namespace AtProto.AppView;

/// <summary>
/// The SignalR hub the presence board connects to. Clients are receive-only: the server pushes
/// <c>"stats"</c> (once-per-second windowed activity) and <c>"presence"</c> (batched live entry
/// deltas), both driven by the Rx <see cref="PresenceProjection"/> via <see cref="PresenceBroadcaster"/>.
/// There are no client-callable methods — the browser seeds initial state from the getPresence XRPC
/// query, then applies live deltas from here.
/// </summary>
public sealed class PresenceHub : Hub;
