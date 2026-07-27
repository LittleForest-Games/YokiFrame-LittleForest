using System;
using System.Collections.Generic;

namespace YokiFrame
{
    /// <summary>
    /// Registration and fan-out owner for transport-neutral connection observers.
    /// The Host decides which thread invokes notifications.
    /// </summary>
    public sealed class CommandBridgeConnectionRegistry
    {
        private readonly object mSync = new object();
        private readonly List<ICommandBridgeConnectionObserver> mObservers =
            new List<ICommandBridgeConnectionObserver>();

        public IDisposable Register(ICommandBridgeConnectionObserver observer)
        {
            if (observer == null)
                throw new ArgumentNullException(nameof(observer));

            lock (mSync)
            {
                mObservers.Add(observer);
            }

            return new Registration(this, observer);
        }

        public void NotifyConnected(CommandBridgeConnectionContext connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            foreach (var observer in Snapshot())
            {
                try
                {
                    observer.OnConnected(connection);
                }
                catch
                {
                    // One extension cannot block other lifecycle observers.
                }
            }
        }

        public void NotifyDisconnected(
            CommandBridgeConnectionContext connection,
            string reason)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            foreach (var observer in Snapshot())
            {
                try
                {
                    observer.OnDisconnected(connection, reason ?? string.Empty);
                }
                catch
                {
                    // One extension cannot block other lifecycle observers.
                }
            }
        }

        private ICommandBridgeConnectionObserver[] Snapshot()
        {
            lock (mSync)
            {
                return mObservers.ToArray();
            }
        }

        private void Unregister(ICommandBridgeConnectionObserver observer)
        {
            lock (mSync)
            {
                mObservers.Remove(observer);
            }
        }

        private sealed class Registration : IDisposable
        {
            private CommandBridgeConnectionRegistry mOwner;
            private ICommandBridgeConnectionObserver mObserver;

            public Registration(
                CommandBridgeConnectionRegistry owner,
                ICommandBridgeConnectionObserver observer)
            {
                mOwner = owner;
                mObserver = observer;
            }

            public void Dispose()
            {
                var owner = mOwner;
                var observer = mObserver;
                if (owner == null || observer == null)
                    return;

                mOwner = null;
                mObserver = null;
                owner.Unregister(observer);
            }
        }
    }
}
