using System;

namespace YokiFrame
{
    /// <summary>
    /// Immutable connection identity exposed to extensions.
    /// </summary>
    public sealed class CommandBridgeConnectionContext
    {
        public CommandBridgeConnectionContext(
            string transport,
            string hostSessionId,
            string connectionId,
            string clientId,
            DateTime connectedAtUtc)
        {
            Transport = transport ?? string.Empty;
            HostSessionId = hostSessionId ?? string.Empty;
            ConnectionId = connectionId ?? string.Empty;
            ClientId = clientId ?? string.Empty;
            ConnectedAtUtc = connectedAtUtc;
        }

        public string Transport { get; }

        public string HostSessionId { get; }

        public string ConnectionId { get; }

        public string ClientId { get; }

        public DateTime ConnectedAtUtc { get; }
    }
}
