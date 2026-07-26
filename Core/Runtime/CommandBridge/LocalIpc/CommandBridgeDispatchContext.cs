using System;

namespace YokiFrame
{
    /// <summary>
    /// Transport-neutral metadata attached to one logical command dispatch.
    /// It intentionally exposes no Named Pipe or engine-specific type.
    /// </summary>
    public sealed class CommandBridgeDispatchContext
    {
        /// <summary>
        /// Context used by the legacy FileBridge transport.
        /// </summary>
        public static CommandBridgeDispatchContext FileBridge { get; } =
            new CommandBridgeDispatchContext(
                "filebridge",
                string.Empty,
                string.Empty,
                DateTime.MinValue);

        /// <summary>
        /// Creates immutable dispatch metadata.
        /// </summary>
        public CommandBridgeDispatchContext(
            string transport,
            string hostSessionId,
            string connectionId,
            DateTime receivedAtUtc)
        {
            Transport = transport ?? string.Empty;
            HostSessionId = hostSessionId ?? string.Empty;
            ConnectionId = connectionId ?? string.Empty;
            ReceivedAtUtc = receivedAtUtc;
        }

        /// <summary>
        /// Stable transport identifier, such as <c>local-ipc-v1</c>.
        /// </summary>
        public string Transport { get; }

        /// <summary>
        /// Host session that admitted the request, or empty for an unscoped transport.
        /// </summary>
        public string HostSessionId { get; }

        /// <summary>
        /// Connection that owns side-car state for the request, or empty when unscoped.
        /// </summary>
        public string ConnectionId { get; }

        /// <summary>
        /// UTC time at which the transport accepted the complete request frame.
        /// </summary>
        public DateTime ReceivedAtUtc { get; }

        /// <summary>
        /// Whether the request belongs to a concrete Host connection.
        /// </summary>
        public bool IsConnectionScoped =>
            !string.IsNullOrEmpty(HostSessionId) &&
            !string.IsNullOrEmpty(ConnectionId);
    }
}
