using System;

namespace YokiFrame
{
    /// <summary>
    /// Optional extension-context capability for Host connection lifecycle.
    /// Keeping it separate preserves existing ICommandBridgeExtensionContext
    /// implementations and test doubles.
    /// </summary>
    public interface ICommandBridgeConnectionExtensionContext
    {
        /// <summary>
        /// Active command transport selected for this Host session.
        /// </summary>
        string CommandTransport { get; }

        IDisposable RegisterConnectionObserver(
            ICommandBridgeConnectionObserver observer);
    }
}
