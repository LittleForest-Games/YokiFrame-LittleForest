namespace YokiFrame
{
    /// <summary>
    /// Observes admitted transport connections without depending on a concrete IPC API.
    /// Implementations must treat callbacks as lifecycle notifications, not liveness polling.
    /// </summary>
    public interface ICommandBridgeConnectionObserver
    {
        void OnConnected(CommandBridgeConnectionContext connection);

        void OnDisconnected(
            CommandBridgeConnectionContext connection,
            string reason);
    }
}
