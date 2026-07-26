#if !GODOT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Unity.Profiling;

namespace YokiFrame.Unity
{
    /// <summary>
    /// Windows Named Pipe transport Host. Background threads only perform
    /// framing/admission/I/O; logical dispatch is posted through the
    /// one-shot Unity main-thread scheduler.
    /// </summary>
    internal sealed class Win32NamedPipeHost : IDisposable
    {
        private const int MainThreadDrainMaxCount = 16;
        private const int MainThreadDrainBudgetMs = 4;
        private const int CompletionPollMs = 100;

        private static readonly ProfilerMarker sMainThreadDrainProfilerMarker =
            new ProfilerMarker("LocalIpcBridge.MainThreadDrain");
        private static readonly ProfilerMarker sActionHandlerProfilerMarker =
            new ProfilerMarker("LocalIpcBridge.ActionHandler");
        private static readonly ILocalIpcDispatchProfiler sDispatchProfiler =
            new UnityDispatchProfiler();
        private static readonly UTF8Encoding sStrictUtf8 =
            new UTF8Encoding(false, true);

        private readonly object mLifecycleSync = new object();
        private readonly object mConnectionsSync = new object();
        private readonly object mDiagnosticsSync = new object();
        private readonly Dictionary<string, PipeConnection> mConnections =
            new Dictionary<string, PipeConnection>(
                StringComparer.Ordinal);
        private readonly Queue<string> mDiagnostics =
            new Queue<string>();
        private readonly KitCommandDispatcher mDispatcher;
        private readonly CommandBridgeConnectionRegistry mConnectionRegistry;
        private readonly Action<Action> mScheduleMainThread;
        private readonly Action<string> mMainThreadDiagnosticSink;
        private readonly string mEngineId;
        private readonly string mProjectRootHash;
        private readonly string mPipeName;
        private readonly int mProcessId;

        private LocalIpcRequestBroker mBroker;
        private Thread mListenerThread;
        private IntPtr mListenerHandle =
            Win32NamedPipeNative.InvalidHandleValue;
        private string mHostSessionId = string.Empty;
        private DateTime mStartedAtUtc;
        private int mStopping;
        private int mDrainScheduled;
        private int mAdmissionStopped;

        internal Win32NamedPipeHost(
            string projectRoot,
            string engineId,
            KitCommandDispatcher dispatcher,
            CommandBridgeConnectionRegistry connectionRegistry,
            Action<Action> scheduleMainThread,
            Action<string> mainThreadDiagnosticSink)
        {
            if (dispatcher == null)
                throw new ArgumentNullException(nameof(dispatcher));
            if (connectionRegistry == null)
                throw new ArgumentNullException(nameof(connectionRegistry));
            if (scheduleMainThread == null)
                throw new ArgumentNullException(nameof(scheduleMainThread));

            mEngineId = engineId;
            mDispatcher = dispatcher;
            mConnectionRegistry = connectionRegistry;
            mScheduleMainThread = scheduleMainThread;
            mMainThreadDiagnosticSink =
                mainThreadDiagnosticSink ?? (_ => { });
            mProjectRootHash =
                LocalIpcEndpoint.ComputeProjectRootHash(projectRoot);
            mPipeName =
                LocalIpcEndpoint.BuildPipeName(projectRoot, engineId);
            mProcessId = Process.GetCurrentProcess().Id;
        }

        internal string HostSessionId => mHostSessionId;

        internal string PipeName => mPipeName;

        internal string ProjectRootHash => mProjectRootHash;

        internal int ActiveClientCount
        {
            get
            {
                lock (mConnectionsSync)
                    return mConnections.Count;
            }
        }

        internal int QueuedRequestCount =>
            mBroker != null ? mBroker.QueuedCount : 0;

        internal int InflightRequestCount =>
            mBroker != null ? mBroker.InflightCount : 0;

        internal bool AdmissionStopped =>
            Volatile.Read(ref mAdmissionStopped) != 0;

        internal void Start()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException(
                    "Local IPC v1 currently supports Windows only.");
            }

            lock (mLifecycleSync)
            {
                if (mListenerThread != null)
                    throw new InvalidOperationException(
                        "Local IPC Host is already started.");
                if (Volatile.Read(ref mStopping) != 0)
                    throw new ObjectDisposedException(
                        nameof(Win32NamedPipeHost));

                var initialListener =
                    Win32NamedPipeNative.CreateCurrentUserOnlyPipe(
                        mPipeName,
                        true);
                mHostSessionId = Guid.NewGuid().ToString("N");
                mStartedAtUtc = DateTime.UtcNow;
                mBroker = new LocalIpcRequestBroker(
                    mEngineId,
                    mHostSessionId);
                mListenerHandle = initialListener;
                mListenerThread = new Thread(
                    () => AcceptLoop(initialListener))
                {
                    IsBackground = true,
                    Name = "YokiFrame Local IPC listener"
                };

                try
                {
                    mListenerThread.Start();
                }
                catch
                {
                    mListenerThread = null;
                    mListenerHandle =
                        Win32NamedPipeNative.InvalidHandleValue;
                    Win32NamedPipeNative.CloseIfValid(initialListener);
                    mBroker.Dispose();
                    mBroker = null;
                    mHostSessionId = string.Empty;
                    throw;
                }
            }
        }

        internal string BuildStatusJson()
        {
            var builder = new StringBuilder(384);
            builder.Append("{\"available\":");
            builder.Append(
                string.IsNullOrEmpty(mHostSessionId)
                    ? "false"
                    : "true");
            builder.Append(",\"transport\":\"");
            builder.Append(LocalIpcProtocol.Transport);
            builder.Append("\",\"hostSessionId\":\"");
            builder.Append(JsonHelper.EscapeString(mHostSessionId));
            builder.Append("\",\"projectRootHash\":\"");
            builder.Append(mProjectRootHash);
            builder.Append("\",\"activeClients\":");
            builder.Append(ActiveClientCount);
            builder.Append(",\"queuedRequests\":");
            builder.Append(QueuedRequestCount);
            builder.Append(",\"inflightRequests\":");
            builder.Append(InflightRequestCount);
            builder.Append(",\"admissionStopped\":");
            builder.Append(AdmissionStopped ? "true" : "false");
            builder.Append('}');
            return builder.ToString();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref mStopping, 1) != 0)
                return;

            Interlocked.Exchange(ref mAdmissionStopped, 1);
            IntPtr listener;
            Thread listenerThread;
            lock (mLifecycleSync)
            {
                listener = mListenerHandle;
                mListenerHandle =
                    Win32NamedPipeNative.InvalidHandleValue;
                listenerThread = mListenerThread;
                mListenerThread = null;
            }

            // Wake the listener through its normal ownership path before
            // cancelling the overlapped operation. This keeps shutdown
            // deterministic across the Editor runtimes we support.
            Win32NamedPipeNative.TryWakeListener(mPipeName);
            mBroker?.Dispose();

            PipeConnection[] connections;
            lock (mConnectionsSync)
                connections = new List<PipeConnection>(
                    mConnections.Values).ToArray();

            foreach (var connection in connections)
                CloseConnection(connection, "host-restarting");

            if (listenerThread != null &&
                listenerThread != Thread.CurrentThread)
            {
                if (!listenerThread.Join(1000))
                {
                    Win32NamedPipeNative.CloseIfValid(listener);
                    listener =
                        Win32NamedPipeNative.InvalidHandleValue;
                    listenerThread.Join(250);
                }

                // Dispose took listener ownership under mLifecycleSync.
                // The accept thread must never close that raw handle.
                Win32NamedPipeNative.CloseIfValid(listener);
            }
            else
            {
                Win32NamedPipeNative.CloseIfValid(listener);
            }

            var connectionJoinBudget = Stopwatch.StartNew();
            foreach (var connection in connections)
            {
                var remainingMs =
                    1000 -
                    checked(
                        (int)connectionJoinBudget.ElapsedMilliseconds);
                if (remainingMs <= 0)
                    break;

                connection.Join(remainingMs);
            }

            lock (mConnectionsSync)
                mConnections.Clear();

            mBroker = null;
            mHostSessionId = string.Empty;
        }

        private void AcceptLoop(IntPtr initialListener)
        {
            var listener = initialListener;
            while (Volatile.Read(ref mStopping) == 0)
            {
                try
                {
                    int error;
                    if (!Win32NamedPipeNative.ConnectOverlapped(
                            listener,
                            out error))
                    {
                        if (Volatile.Read(ref mStopping) != 0 ||
                            Win32NamedPipeNative
                                .IsDisconnectError(error))
                        {
                            break;
                        }

                        throw new Win32Exception(
                            error,
                            "ConnectNamedPipe failed.");
                    }

                    if (!ReleaseListenerOwnership(listener))
                    {
                        break;
                    }

                    var acceptedHandle = listener;
                    listener =
                        Win32NamedPipeNative.InvalidHandleValue;
                    try
                    {
                        if (Volatile.Read(ref mStopping) != 0)
                            break;

                        PipeConnection connection;
                        if (!TryRegisterConnection(
                                acceptedHandle,
                                out connection))
                        {
                            if (Volatile.Read(ref mStopping) == 0)
                            {
                                TryWriteStandaloneFrame(
                                    acceptedHandle,
                                    LocalIpcProtocol.BuildTransportError(
                                        "too-many-clients",
                                        "Host active client limit reached.",
                                        mHostSessionId));
                            }

                            Win32NamedPipeNative
                                .DisconnectNamedPipe(acceptedHandle);
                        }
                        else
                        {
                            acceptedHandle =
                                Win32NamedPipeNative.InvalidHandleValue;
                            try
                            {
                                connection.Start();
                            }
                            catch
                            {
                                CloseConnection(
                                    connection,
                                    "connection-thread-start-failed");
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        Win32NamedPipeNative.CloseIfValid(
                            acceptedHandle);
                    }

                    listener =
                        Win32NamedPipeNative
                            .CreateCurrentUserOnlyPipe(
                                mPipeName,
                                false);
                    if (!PublishListener(listener))
                    {
                        Win32NamedPipeNative.CloseIfValid(listener);
                        break;
                    }
                }
                catch (Exception exception)
                {
                    if (ReleaseListenerOwnership(listener))
                        Win32NamedPipeNative.CloseIfValid(listener);
                    if (Volatile.Read(ref mStopping) == 0)
                    {
                        Interlocked.Exchange(
                            ref mAdmissionStopped,
                            1);
                        Report(
                            "Local IPC admission stopped: " +
                            exception.Message);
                    }

                    break;
                }
            }
        }

        private bool PublishListener(IntPtr listener)
        {
            lock (mLifecycleSync)
            {
                if (Volatile.Read(ref mStopping) != 0)
                    return false;

                mListenerHandle = listener;
                return true;
            }
        }

        private bool ReleaseListenerOwnership(IntPtr listener)
        {
            lock (mLifecycleSync)
            {
                if (mListenerHandle != listener)
                    return false;

                mListenerHandle =
                    Win32NamedPipeNative.InvalidHandleValue;
                return true;
            }
        }

        private bool TryRegisterConnection(
            IntPtr handle,
            out PipeConnection connection)
        {
            lock (mConnectionsSync)
            {
                if (Volatile.Read(ref mStopping) != 0 ||
                    mConnections.Count >=
                    LocalIpcProtocol.MaxActiveClients)
                {
                    connection = null;
                    return false;
                }

                var connectionId =
                    Guid.NewGuid().ToString("N");
                PipeConnection createdConnection = null;
                createdConnection = new PipeConnection(
                    handle,
                    connectionId,
                    () => RunConnection(createdConnection),
                    () => RunResponseWriter(createdConnection));
                connection = createdConnection;
                mConnections.Add(connectionId, connection);
                return true;
            }
        }

        private void RunConnection(PipeConnection connection)
        {
            var closeReason = "client-disconnected";
            Timer handshakeTimer = null;
            try
            {
                handshakeTimer = new Timer(
                    _ =>
                    {
                        if (!connection.TryTimeoutHandshake())
                            return;

                        CloseConnection(
                            connection,
                            "handshake-timeout");
                    },
                    null,
                    LocalIpcProtocol.HandshakeTimeoutMs,
                    Timeout.Infinite);

                string helloJson;
                if (!TryReadFrame(
                        connection.Handle,
                        LocalIpcProtocol.MaxRequestFrameBytes,
                        out helloJson))
                {
                    closeReason = "disconnect-before-handshake";
                    return;
                }

                string clientId;
                string errorCode;
                string errorMessage;
                if (!LocalIpcProtocol.TryValidateHello(
                        helloJson,
                        mProjectRootHash,
                        mEngineId,
                        out clientId,
                        out errorCode,
                        out errorMessage))
                {
                    WriteConnectionFrame(
                        connection,
                        LocalIpcProtocol.BuildTransportError(
                            errorCode,
                            errorMessage,
                            mHostSessionId));
                    closeReason = errorCode;
                    return;
                }

                if (!connection.TryCompleteHandshake())
                {
                    closeReason = "handshake-timeout";
                    return;
                }

                handshakeTimer.Dispose();
                handshakeTimer = null;
                var context = new CommandBridgeConnectionContext(
                    LocalIpcProtocol.Transport,
                    mHostSessionId,
                    connection.ConnectionId,
                    clientId,
                    DateTime.UtcNow);
                connection.Context = context;
                PostMainThread(
                    () => mConnectionRegistry.NotifyConnected(context));

                WriteConnectionFrame(
                    connection,
                    LocalIpcProtocol.BuildHelloAck(
                        mHostSessionId,
                        mEngineId,
                        mProjectRootHash,
                        mProcessId,
                        mStartedAtUtc));

                while (Volatile.Read(ref mStopping) == 0 &&
                       !connection.IsClosed)
                {
                    string commandJson;
                    if (!TryReadFrame(
                            connection.Handle,
                            LocalIpcProtocol.MaxRequestFrameBytes,
                            out commandJson))
                    {
                        break;
                    }

                    var dispatchContext =
                        new CommandBridgeDispatchContext(
                            LocalIpcProtocol.Transport,
                            mHostSessionId,
                            connection.ConnectionId,
                            DateTime.UtcNow);
                    var admission = mBroker.Admit(
                        commandJson,
                        dispatchContext,
                        DateTime.UtcNow);
                    if (!admission.Admitted)
                    {
                        WriteConnectionFrame(
                            connection,
                            admission.ResponseJson);
                        continue;
                    }

                    RequestMainThreadDrain();
                    var pending = admission.Pending;
                    if (!connection.EnqueueResponse(pending))
                    {
                        mBroker.CancelConnection(
                            connection.ConnectionId,
                            "Connection closed before response ownership transfer.",
                            DateTime.UtcNow);
                        pending.Dispose();
                        throw new InvalidOperationException(
                            "Unable to transfer response ownership.");
                    }
                }
            }
            catch (LocalIpcFrameException exception)
            {
                closeReason = exception.ErrorCode;
                TryWriteConnectionFrame(
                    connection,
                    LocalIpcProtocol.BuildTransportError(
                        exception.ErrorCode,
                        exception.Message,
                        mHostSessionId));
            }
            catch (Exception exception)
            {
                closeReason = "connection-error";
                if (Volatile.Read(ref mStopping) == 0 &&
                    !connection.IsClosed)
                {
                    Report(
                        "Local IPC connection failed: " +
                        exception.Message);
                }
            }
            finally
            {
                handshakeTimer?.Dispose();
                CloseConnection(connection, closeReason);
                connection.MarkReaderExited();
            }
        }

        private void RunResponseWriter(PipeConnection connection)
        {
            try
            {
                while (true)
                {
                    LocalIpcPendingRequest pending;
                    if (connection.TryDequeueResponse(out pending))
                    {
                        AwaitAndWriteResponse(connection, pending);
                        continue;
                    }

                    if (Volatile.Read(ref mStopping) != 0 ||
                        connection.IsClosed)
                    {
                        return;
                    }

                    connection.WaitForResponse(CompletionPollMs);
                }
            }
            finally
            {
                connection.MarkResponseWriterExited();
            }
        }

        private void AwaitAndWriteResponse(
            PipeConnection connection,
            LocalIpcPendingRequest pending)
        {
            try
            {
                // The waiter owns the normal completion lifetime. On
                // disconnect, LocalIpcPendingRequest defers its requested
                // Dispose until an in-progress dispatch reaches terminal.
                while (true)
                {
                    string responseJson;
                    if (pending.WaitForCompletion(
                            TimeSpan.FromMilliseconds(
                                CompletionPollMs),
                            out responseJson))
                    {
                        if (!pending.ConnectionClosed &&
                            !connection.IsClosed)
                        {
                            responseJson =
                                EnsureResponseWithinLimit(
                                    pending,
                                    responseJson);
                            WriteConnectionFrame(
                                connection,
                                responseJson);
                        }

                        return;
                    }

                    if (pending.ConnectionClosed ||
                        connection.IsClosed)
                    {
                        return;
                    }

                    var nowUtc = DateTime.UtcNow;
                    if (nowUtc >= pending.Metadata.DeadlineUtc)
                    {
                        mBroker.TryExpireBeforeDispatch(
                            pending,
                            nowUtc);
                    }
                }
            }
            catch (Exception exception)
            {
                if (Volatile.Read(ref mStopping) == 0 &&
                    !connection.IsClosed)
                {
                    Report(
                        "Local IPC response write failed: " +
                        exception.Message);
                    CloseConnection(
                        connection,
                        "response-write-failed");
                }
            }
            finally
            {
                pending.Dispose();
            }
        }

        private string EnsureResponseWithinLimit(
            LocalIpcPendingRequest pending,
            string responseJson)
        {
            if (sStrictUtf8.GetByteCount(responseJson) <=
                LocalIpcProtocol.MaxResponseFrameBytes)
            {
                return responseJson;
            }

            return LocalIpcProtocol.BuildRequestError(
                pending.Metadata.RequestId,
                pending.Metadata.Kit,
                pending.Metadata.Action,
                mEngineId,
                mHostSessionId,
                "response-too-large",
                "Dispatcher response exceeded maxResponseFrameBytes.",
                false);
        }

        private void CloseConnection(
            PipeConnection connection,
            string reason)
        {
            if (connection == null ||
                !connection.TryMarkClosed())
            {
                return;
            }

            mBroker?.CancelConnection(
                connection.ConnectionId,
                reason,
                DateTime.UtcNow);
            lock (mConnectionsSync)
                mConnections.Remove(connection.ConnectionId);

            var context = connection.Context;
            if (context != null)
            {
                PostMainThread(
                    () => mConnectionRegistry.NotifyDisconnected(
                        context,
                        reason));
            }

            connection.CancelPendingIo();
        }

        private void RequestMainThreadDrain()
        {
            if (Volatile.Read(ref mStopping) != 0)
                return;
            if (Interlocked.CompareExchange(
                    ref mDrainScheduled,
                    1,
                    0) != 0)
            {
                return;
            }

            PostMainThread(DrainOnMainThread);
        }

        private void DrainOnMainThread()
        {
            using (sMainThreadDrainProfilerMarker.Auto())
            {
                Interlocked.Exchange(ref mDrainScheduled, 0);
                FlushDiagnostics();
                if (Volatile.Read(ref mStopping) != 0 ||
                    mBroker == null)
                {
                    return;
                }

                mBroker.DrainProfiled(
                    mDispatcher,
                    MainThreadDrainMaxCount,
                    TimeSpan.FromMilliseconds(
                        MainThreadDrainBudgetMs),
                    sDispatchProfiler);
                FlushDiagnostics();
                if (mBroker.QueuedCount > 0)
                    RequestMainThreadDrain();
            }
        }

        private void PostMainThread(Action action)
        {
            mScheduleMainThread(
                () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        mMainThreadDiagnosticSink(
                            "Local IPC main-thread callback failed: " +
                            exception.Message);
                    }
                });
        }

        private void Report(string message)
        {
            lock (mDiagnosticsSync)
                mDiagnostics.Enqueue(message);
            RequestMainThreadDrain();
        }

        private void FlushDiagnostics()
        {
            while (true)
            {
                string message;
                lock (mDiagnosticsSync)
                {
                    if (mDiagnostics.Count == 0)
                        return;

                    message = mDiagnostics.Dequeue();
                }

                mMainThreadDiagnosticSink(message);
            }
        }

        private sealed class UnityDispatchProfiler :
            ILocalIpcDispatchProfiler
        {
            public void Begin()
            {
                sActionHandlerProfilerMarker.Begin();
            }

            public void End()
            {
                sActionHandlerProfilerMarker.End();
            }
        }

        private static bool TryReadFrame(
            IntPtr handle,
            int maxPayloadBytes,
            out string json)
        {
            json = string.Empty;
            var prefix = new byte[sizeof(uint)];
            if (!ReadExactly(
                    handle,
                    prefix,
                    true))
            {
                return false;
            }

            var length =
                LocalIpcFrameCodec.ReadUInt32LittleEndian(
                    prefix,
                    0);
            if (length == 0 ||
                length > maxPayloadBytes)
            {
                throw new LocalIpcFrameException(
                    "frame-too-large",
                    "Frame payload length is outside the configured limit.");
            }

            var payload = new byte[checked((int)length)];
            if (!ReadExactly(handle, payload, false))
            {
                throw new LocalIpcFrameException(
                    "truncated-frame",
                    "Pipe disconnected before the frame completed.");
            }

            try
            {
                json = sStrictUtf8.GetString(payload);
                return true;
            }
            catch (DecoderFallbackException)
            {
                throw new LocalIpcFrameException(
                    "invalid-utf8",
                    "Frame payload is not valid UTF-8.");
            }
        }

        private static bool ReadExactly(
            IntPtr handle,
            byte[] buffer,
            bool allowCleanEof)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                uint read;
                int error;
                if (!Win32NamedPipeNative.ReadOverlapped(
                        handle,
                        buffer,
                        offset,
                        checked((uint)(buffer.Length - offset)),
                        out read,
                        out error))
                {
                    if (allowCleanEof &&
                        offset == 0 &&
                        Win32NamedPipeNative.IsDisconnectError(error))
                    {
                        return false;
                    }

                    throw new Win32Exception(
                        error,
                        "ReadFile failed.");
                }

                if (read == 0)
                {
                    if (allowCleanEof && offset == 0)
                        return false;

                    throw new LocalIpcFrameException(
                        "truncated-frame",
                        "Pipe disconnected before the frame completed.");
                }

                offset += checked((int)read);
            }

            return true;
        }

        private static void WriteConnectionFrame(
            PipeConnection connection,
            string json)
        {
            lock (connection.WriteSync)
            {
                if (connection.IsClosed)
                    throw new ObjectDisposedException(
                        nameof(PipeConnection));

                WriteFrame(connection.Handle, json);
            }
        }

        private static bool TryWriteConnectionFrame(
            PipeConnection connection,
            string json)
        {
            try
            {
                WriteConnectionFrame(connection, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteStandaloneFrame(
            IntPtr handle,
            string json)
        {
            try
            {
                WriteFrame(handle, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteFrame(
            IntPtr handle,
            string json)
        {
            var frame = LocalIpcFrameCodec.Encode(
                json,
                LocalIpcProtocol.MaxResponseFrameBytes);
            var offset = 0;
            while (offset < frame.Length)
            {
                uint written;
                int error;
                if (!Win32NamedPipeNative.WriteOverlapped(
                        handle,
                        frame,
                        offset,
                        checked((uint)(frame.Length - offset)),
                        out written,
                        out error))
                {
                    throw new Win32Exception(
                        error,
                        "WriteFile failed.");
                }

                if (written == 0)
                {
                    throw new InvalidOperationException(
                        "Pipe disconnected while writing a frame.");
                }

                offset += checked((int)written);
            }
        }

        private sealed class PipeConnection
        {
            private readonly object mResponseSync = new object();
            private readonly Queue<LocalIpcPendingRequest> mResponses =
                new Queue<LocalIpcPendingRequest>();
            private readonly AutoResetEvent mResponseAvailable =
                new AutoResetEvent(false);
            private IntPtr mHandle;
            private CommandBridgeConnectionContext mContext;
            private int mClosed;
            private int mHandshakeState;
            private int mReaderStarted;
            private int mReaderExited;
            private int mResponseWriterStarted;
            private int mResponseWriterExited;
            private int mStartCompleted;
            private int mResourcesDisposed;

            internal PipeConnection(
                IntPtr handle,
                string connectionId,
                ThreadStart readerThreadStart,
                ThreadStart responseWriterThreadStart)
            {
                mHandle = handle;
                ConnectionId = connectionId;
                ReaderThread = new Thread(readerThreadStart)
                {
                    IsBackground = true,
                    Name =
                        "YokiFrame Local IPC reader " +
                        connectionId
                };
                ResponseWriterThread =
                    new Thread(responseWriterThreadStart)
                {
                    IsBackground = true,
                    Name =
                        "YokiFrame Local IPC writer " +
                        connectionId
                };
            }

            internal object WriteSync { get; } = new object();

            internal string ConnectionId { get; }

            internal Thread ReaderThread { get; }

            internal Thread ResponseWriterThread { get; }

            internal CommandBridgeConnectionContext Context
            {
                get => Volatile.Read(ref mContext);
                set => Volatile.Write(ref mContext, value);
            }

            internal IntPtr Handle => mHandle;

            internal bool IsClosed =>
                Volatile.Read(ref mClosed) != 0;

            internal bool TryMarkClosed()
            {
                var marked = Interlocked.CompareExchange(
                                 ref mClosed,
                                 1,
                                 0) == 0;
                if (marked)
                    mResponseAvailable.Set();
                return marked;
            }

            internal bool TryCompleteHandshake()
            {
                return Interlocked.CompareExchange(
                           ref mHandshakeState,
                           1,
                           0) == 0;
            }

            internal bool TryTimeoutHandshake()
            {
                return Interlocked.CompareExchange(
                           ref mHandshakeState,
                           2,
                           0) == 0;
            }

            internal void Start()
            {
                try
                {
                    ResponseWriterThread.Start();
                    Volatile.Write(
                        ref mResponseWriterStarted,
                        1);
                    ReaderThread.Start();
                    Volatile.Write(
                        ref mReaderStarted,
                        1);
                }
                catch
                {
                    TryMarkClosed();
                    throw;
                }
                finally
                {
                    Volatile.Write(ref mStartCompleted, 1);
                    TryDisposeResources();
                }
            }

            internal bool EnqueueResponse(
                LocalIpcPendingRequest pending)
            {
                lock (mResponseSync)
                {
                    if (IsClosed)
                        return false;

                    mResponses.Enqueue(pending);
                }

                mResponseAvailable.Set();
                return true;
            }

            internal bool TryDequeueResponse(
                out LocalIpcPendingRequest pending)
            {
                lock (mResponseSync)
                {
                    if (mResponses.Count == 0)
                    {
                        pending = null;
                        return false;
                    }

                    pending = mResponses.Dequeue();
                    return true;
                }
            }

            internal void WaitForResponse(int timeoutMs)
            {
                mResponseAvailable.WaitOne(timeoutMs);
            }

            internal void Join(int timeoutMs)
            {
                var stopwatch = Stopwatch.StartNew();
                if (Volatile.Read(ref mReaderStarted) != 0 &&
                    ReaderThread != Thread.CurrentThread)
                {
                    ReaderThread.Join(timeoutMs);
                }

                var remaining =
                    timeoutMs -
                    checked((int)stopwatch.ElapsedMilliseconds);
                if (remaining > 0 &&
                    Volatile.Read(
                        ref mResponseWriterStarted) != 0 &&
                    ResponseWriterThread != Thread.CurrentThread)
                {
                    ResponseWriterThread.Join(remaining);
                }

                if ((Volatile.Read(ref mReaderStarted) == 0 ||
                     !ReaderThread.IsAlive) &&
                    (Volatile.Read(
                         ref mResponseWriterStarted) == 0 ||
                     !ResponseWriterThread.IsAlive))
                {
                    DisposeResources();
                }
            }

            internal void MarkReaderExited()
            {
                Volatile.Write(ref mReaderExited, 1);
                TryDisposeResources();
            }

            internal void MarkResponseWriterExited()
            {
                Volatile.Write(
                    ref mResponseWriterExited,
                    1);
                TryDisposeResources();
            }

            internal void CloseHandle()
            {
                var handle = Interlocked.Exchange(
                    ref mHandle,
                    Win32NamedPipeNative.InvalidHandleValue);
                Win32NamedPipeNative.CloseIfValid(handle);
            }

            internal void CancelPendingIo()
            {
                var handle = Interlocked.CompareExchange(
                    ref mHandle,
                    IntPtr.Zero,
                    IntPtr.Zero);
                Win32NamedPipeNative.CancelIfValid(handle);
            }

            private void TryDisposeResources()
            {
                if (Volatile.Read(ref mStartCompleted) == 0)
                    return;
                if (Volatile.Read(ref mReaderStarted) != 0 &&
                    Volatile.Read(ref mReaderExited) == 0)
                {
                    return;
                }

                if (Volatile.Read(
                        ref mResponseWriterStarted) != 0 &&
                    Volatile.Read(
                        ref mResponseWriterExited) == 0)
                {
                    return;
                }

                DisposeResources();
            }

            private void DisposeResources()
            {
                if (Interlocked.Exchange(
                        ref mResourcesDisposed,
                        1) == 0)
                {
                    CloseHandle();
                    mResponseAvailable.Dispose();
                }
            }
        }

        private sealed class LocalIpcFrameException : Exception
        {
            internal LocalIpcFrameException(
                string errorCode,
                string message)
                : base(message)
            {
                ErrorCode = errorCode;
            }

            internal string ErrorCode { get; }
        }
    }
}
#endif
