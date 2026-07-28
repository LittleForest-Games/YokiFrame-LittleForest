using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace YokiFrame
{
    /// <summary>
    /// Package-internal dispatch boundary instrumentation. The Local IPC core
    /// owns the exact dispatch boundary; engine adapters own their profiler.
    /// </summary>
    internal interface ILocalIpcDispatchProfiler
    {
        void Begin();

        void End();
    }

    /// <summary>
    /// Thread-safe admission, backpressure, duplicate suppression and
    /// main-thread dispatch owner for Local IPC requests.
    /// </summary>
    public sealed class LocalIpcRequestBroker : IDisposable
    {
        private readonly object mSync = new object();
        private readonly Queue<LocalIpcPendingRequest> mQueue =
            new Queue<LocalIpcPendingRequest>();
        private readonly Dictionary<string, LocalIpcPendingRequest> mInflight =
            new Dictionary<string, LocalIpcPendingRequest>(
                StringComparer.Ordinal);
        private readonly Dictionary<string, int> mConnectionInflight =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tombstone> mTombstones =
            new Dictionary<string, Tombstone>(StringComparer.Ordinal);
        private readonly Queue<Tombstone> mTombstoneOrder =
            new Queue<Tombstone>();
        private readonly string mEngineId;
        private readonly string mHostSessionId;
        private int mQueuedCount;
        private bool mDisposed;

        public LocalIpcRequestBroker(
            string engineId,
            string hostSessionId)
        {
            if (!CommandBridgeProtocol.IsSafeIdentifier(engineId))
                throw new ArgumentException(
                    "engineId must be a safe identifier.",
                    nameof(engineId));
            if (!CommandBridgeProtocol.IsSafeIdentifier(hostSessionId))
                throw new ArgumentException(
                    "hostSessionId must be a safe identifier.",
                    nameof(hostSessionId));

            mEngineId = engineId;
            mHostSessionId = hostSessionId;
        }

        public int QueuedCount
        {
            get
            {
                lock (mSync)
                {
                    return mQueuedCount;
                }
            }
        }

        public int InflightCount
        {
            get
            {
                lock (mSync)
                {
                    return mInflight.Count;
                }
            }
        }

        public int TombstoneCount
        {
            get
            {
                lock (mSync)
                {
                    return mTombstones.Count;
                }
            }
        }

        public LocalIpcAdmissionResult Admit(
            string commandJson,
            CommandBridgeDispatchContext dispatchContext,
            DateTime nowUtc)
        {
            if (dispatchContext == null)
                throw new ArgumentNullException(nameof(dispatchContext));
            if (!dispatchContext.IsConnectionScoped)
            {
                throw new ArgumentException(
                    "Local IPC requests require a connection-scoped context.",
                    nameof(dispatchContext));
            }
            if (!string.Equals(
                    dispatchContext.HostSessionId,
                    mHostSessionId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Dispatch context belongs to another Host session.",
                    nameof(dispatchContext));
            }

            LocalIpcRequestMetadata metadata;
            string errorCode;
            string errorMessage;
            if (!LocalIpcProtocol.TryReadRequestMetadata(
                    commandJson,
                    mEngineId,
                    out metadata,
                    out errorCode,
                    out errorMessage))
            {
                var requestId =
                    JsonHelper.ExtractString(
                        commandJson ?? string.Empty,
                        "requestId");
                var kit =
                    JsonHelper.ExtractString(
                        commandJson ?? string.Empty,
                        "kit");
                var action =
                    JsonHelper.ExtractString(
                        commandJson ?? string.Empty,
                        "action");
                return LocalIpcAdmissionResult.Rejected(
                    errorCode,
                    LocalIpcProtocol.BuildRequestError(
                        CommandBridgeProtocol.IsSafeIdentifier(requestId)
                            ? requestId
                            : string.Empty,
                        kit,
                        action,
                        mEngineId,
                        mHostSessionId,
                        errorCode,
                        errorMessage,
                        false));
            }

            DateTime hostDeadlineUtc;
            try
            {
                hostDeadlineUtc =
                    nowUtc.ToUniversalTime().AddMilliseconds(
                        metadata.TimeoutMs);
            }
            catch (ArgumentException)
            {
                return LocalIpcAdmissionResult.Rejected(
                    "invalid-received-at",
                    LocalIpcProtocol.BuildRequestError(
                        metadata.RequestId,
                        metadata.Kit,
                        metadata.Action,
                        mEngineId,
                        mHostSessionId,
                        "invalid-received-at",
                        "Host receive time exceeds the supported date range.",
                        false));
            }

            if (hostDeadlineUtc < metadata.DeadlineUtc)
            {
                metadata = new LocalIpcRequestMetadata(
                    metadata.RequestId,
                    metadata.Kit,
                    metadata.Action,
                    metadata.CreatedAtUtc,
                    metadata.TimeoutMs,
                    hostDeadlineUtc);
            }

            lock (mSync)
            {
                CleanupTombstonesLocked(nowUtc);
                if (mDisposed)
                {
                    return RejectLocked(
                        metadata,
                        "host-restarting",
                        "Host is stopping and no longer admits requests.",
                        true,
                        nowUtc,
                        true);
                }

                if (mInflight.ContainsKey(metadata.RequestId))
                {
                    return RejectLocked(
                        metadata,
                        "duplicate-inflight",
                        "requestId is already inflight in this Host session.",
                        false,
                        nowUtc,
                        false);
                }

                if (mTombstones.ContainsKey(metadata.RequestId))
                {
                    return RejectLocked(
                        metadata,
                        "duplicate-completed",
                        "requestId already has a terminal outcome in this Host session.",
                        false,
                        nowUtc,
                        false);
                }

                if (nowUtc >= metadata.DeadlineUtc)
                {
                    return RejectLocked(
                        metadata,
                        "command-expired",
                        "Request deadline elapsed before admission.",
                        false,
                        nowUtc,
                        true);
                }

                int connectionInflight;
                mConnectionInflight.TryGetValue(
                    dispatchContext.ConnectionId,
                    out connectionInflight);
                if (connectionInflight >=
                    LocalIpcProtocol.MaxInflightPerConnection)
                {
                    return RejectLocked(
                        metadata,
                        "too-many-inflight",
                        "Connection inflight request limit reached.",
                        true,
                        nowUtc,
                        true);
                }

                if (mQueuedCount >= LocalIpcProtocol.MaxQueuedRequests)
                {
                    return RejectLocked(
                        metadata,
                        "busy",
                        "Host main-thread dispatch queue is full.",
                        true,
                        nowUtc,
                        true);
                }

                var pending = new LocalIpcPendingRequest(
                    commandJson,
                    metadata,
                    dispatchContext);
                mQueue.Enqueue(pending);
                mQueuedCount++;
                mInflight.Add(metadata.RequestId, pending);
                mConnectionInflight[dispatchContext.ConnectionId] =
                    connectionInflight + 1;
                return LocalIpcAdmissionResult.Accepted(pending);
            }
        }

        /// <summary>
        /// Drains admitted work on the owning main thread.
        /// </summary>
        public int Drain(
            KitCommandDispatcher dispatcher,
            int maxCount,
            TimeSpan timeBudget,
            Func<DateTime> utcNow = null)
        {
            return DrainCore(
                dispatcher,
                maxCount,
                timeBudget,
                null,
                utcNow);
        }

        /// <summary>
        /// Drains admitted work while exposing only the logical dispatch
        /// boundary to a package-owned engine profiler.
        /// </summary>
        internal int DrainProfiled(
            KitCommandDispatcher dispatcher,
            int maxCount,
            TimeSpan timeBudget,
            ILocalIpcDispatchProfiler dispatchProfiler,
            Func<DateTime> utcNow = null)
        {
            if (dispatchProfiler == null)
            {
                throw new ArgumentNullException(
                    nameof(dispatchProfiler));
            }

            return DrainCore(
                dispatcher,
                maxCount,
                timeBudget,
                dispatchProfiler,
                utcNow);
        }

        private int DrainCore(
            KitCommandDispatcher dispatcher,
            int maxCount,
            TimeSpan timeBudget,
            ILocalIpcDispatchProfiler dispatchProfiler,
            Func<DateTime> utcNow)
        {
            if (dispatcher == null)
                throw new ArgumentNullException(nameof(dispatcher));
            if (maxCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCount));

            var nowProvider = utcNow ?? (() => DateTime.UtcNow);
            var stopwatch = Stopwatch.StartNew();
            var processed = 0;
            while (processed < maxCount &&
                   (timeBudget <= TimeSpan.Zero ||
                    stopwatch.Elapsed < timeBudget))
            {
                LocalIpcPendingRequest pending;
                lock (mSync)
                {
                    if (mQueue.Count == 0)
                        break;

                    pending = mQueue.Dequeue();
                    if (mQueuedCount > 0)
                        mQueuedCount--;
                }

                var nowUtc = nowProvider();
                if (nowUtc >= pending.Metadata.DeadlineUtc)
                {
                    if (pending.TryCompleteBeforeDispatch(
                            BuildError(
                                pending.Metadata,
                                "command-expired",
                                "Request deadline elapsed before dispatch.",
                                false)))
                    {
                        FinalizeRequest(pending, nowUtc);
                    }

                    processed++;
                    continue;
                }

                if (!pending.TryBeginDispatch())
                {
                    processed++;
                    continue;
                }

                string response;
                try
                {
                    dispatchProfiler?.Begin();
                    try
                    {
                        response = dispatcher.Dispatch(
                            pending.CommandJson,
                            pending.DispatchContext);
                    }
                    finally
                    {
                        dispatchProfiler?.End();
                    }

                    response = LocalIpcProtocol.DecorateResponse(
                        response,
                        mHostSessionId);
                }
                catch (Exception exception)
                {
                    response = BuildError(
                        pending.Metadata,
                        "dispatch-exception",
                        exception.Message,
                        false);
                }

                pending.CompleteAfterDispatch(response);
                FinalizeRequest(pending, nowProvider());
                processed++;
            }

            return processed;
        }

        public bool TryExpireBeforeDispatch(
            LocalIpcPendingRequest pending,
            DateTime nowUtc)
        {
            if (pending == null)
                throw new ArgumentNullException(nameof(pending));
            if (nowUtc < pending.Metadata.DeadlineUtc)
                return false;

            if (!pending.TryCompleteBeforeDispatch(
                    BuildError(
                        pending.Metadata,
                        "command-expired",
                        "Request deadline elapsed before dispatch.",
                        false)))
            {
                return false;
            }

            FinalizeRequest(pending, nowUtc);
            return true;
        }

        public void CancelConnection(
            string connectionId,
            string reason,
            DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(connectionId))
                return;

            lock (mSync)
            {
                var requests =
                    new List<LocalIpcPendingRequest>(mInflight.Values);
                foreach (var pending in requests)
                {
                    if (!string.Equals(
                            pending.DispatchContext.ConnectionId,
                            connectionId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!pending.TryCompleteBeforeDispatch(
                            BuildError(
                                pending.Metadata,
                                "client-disconnected",
                                reason ?? "Client disconnected.",
                                false)))
                    {
                        pending.MarkConnectionClosed();
                        continue;
                    }

                    FinalizeRequestLocked(pending, nowUtc);
                }
            }
        }

        public void Dispose()
        {
            lock (mSync)
            {
                if (mDisposed)
                    return;

                mDisposed = true;
                var nowUtc = DateTime.UtcNow;
                var requests =
                    new List<LocalIpcPendingRequest>(mInflight.Values);
                foreach (var pending in requests)
                {
                    if (!pending.TryCompleteBeforeDispatch(
                            BuildError(
                                pending.Metadata,
                                "host-restarting",
                                "Host stopped before dispatch.",
                                true)))
                    {
                        pending.MarkConnectionClosed();
                        continue;
                    }

                    FinalizeRequestLocked(pending, nowUtc);
                }

                mQueue.Clear();
                mQueuedCount = 0;
                mConnectionInflight.Clear();
                mTombstones.Clear();
                mTombstoneOrder.Clear();
            }
        }

        private LocalIpcAdmissionResult RejectLocked(
            LocalIpcRequestMetadata metadata,
            string errorCode,
            string message,
            bool recoverable,
            DateTime nowUtc,
            bool addTombstone)
        {
            if (addTombstone)
                AddTombstoneLocked(metadata.RequestId, errorCode, nowUtc);

            return LocalIpcAdmissionResult.Rejected(
                errorCode,
                BuildError(
                    metadata,
                    errorCode,
                    message,
                    recoverable));
        }

        private string BuildError(
            LocalIpcRequestMetadata metadata,
            string errorCode,
            string message,
            bool recoverable)
        {
            return LocalIpcProtocol.BuildRequestError(
                metadata.RequestId,
                metadata.Kit,
                metadata.Action,
                mEngineId,
                mHostSessionId,
                errorCode,
                message,
                recoverable);
        }

        private void FinalizeRequest(
            LocalIpcPendingRequest pending,
            DateTime nowUtc)
        {
            lock (mSync)
            {
                FinalizeRequestLocked(pending, nowUtc);
            }
        }

        private void FinalizeRequestLocked(
            LocalIpcPendingRequest pending,
            DateTime nowUtc)
        {
            LocalIpcPendingRequest existing;
            if (!mInflight.TryGetValue(
                    pending.Metadata.RequestId,
                    out existing) ||
                !ReferenceEquals(existing, pending))
            {
                return;
            }

            mInflight.Remove(pending.Metadata.RequestId);
            var connectionId = pending.DispatchContext.ConnectionId;
            int connectionInflight;
            if (mConnectionInflight.TryGetValue(
                    connectionId,
                    out connectionInflight))
            {
                if (connectionInflight <= 1)
                    mConnectionInflight.Remove(connectionId);
                else
                    mConnectionInflight[connectionId] =
                        connectionInflight - 1;
            }

            AddTombstoneLocked(
                pending.Metadata.RequestId,
                pending.TerminalOutcome,
                nowUtc);
        }

        private void AddTombstoneLocked(
            string requestId,
            string outcome,
            DateTime nowUtc)
        {
            if (mTombstones.ContainsKey(requestId))
                return;

            var tombstone = new Tombstone(
                requestId,
                outcome ?? string.Empty,
                nowUtc);
            mTombstones.Add(requestId, tombstone);
            mTombstoneOrder.Enqueue(tombstone);
            CleanupTombstonesLocked(nowUtc);
        }

        private void CleanupTombstonesLocked(DateTime nowUtc)
        {
            while (mTombstoneOrder.Count > 0)
            {
                var candidate = mTombstoneOrder.Peek();
                var expired =
                    nowUtc - candidate.CompletedAtUtc >=
                    LocalIpcProtocol.TombstoneTtl;
                var overCapacity =
                    mTombstones.Count > LocalIpcProtocol.MaxTombstones;
                if (!expired && !overCapacity)
                    break;

                mTombstoneOrder.Dequeue();
                Tombstone current;
                if (mTombstones.TryGetValue(
                        candidate.RequestId,
                        out current) &&
                    ReferenceEquals(current, candidate))
                {
                    mTombstones.Remove(candidate.RequestId);
                }
            }
        }

        private sealed class Tombstone
        {
            public Tombstone(
                string requestId,
                string outcome,
                DateTime completedAtUtc)
            {
                RequestId = requestId;
                Outcome = outcome;
                CompletedAtUtc = completedAtUtc;
            }

            public string RequestId { get; }

            public string Outcome { get; }

            public DateTime CompletedAtUtc { get; }
        }
    }

    public sealed class LocalIpcAdmissionResult
    {
        private LocalIpcAdmissionResult(
            LocalIpcPendingRequest pending,
            string errorCode,
            string responseJson)
        {
            Pending = pending;
            ErrorCode = errorCode ?? string.Empty;
            ResponseJson = responseJson ?? string.Empty;
        }

        public bool Admitted => Pending != null;

        public LocalIpcPendingRequest Pending { get; }

        public string ErrorCode { get; }

        public string ResponseJson { get; }

        internal static LocalIpcAdmissionResult Accepted(
            LocalIpcPendingRequest pending)
        {
            return new LocalIpcAdmissionResult(
                pending,
                string.Empty,
                string.Empty);
        }

        internal static LocalIpcAdmissionResult Rejected(
            string errorCode,
            string responseJson)
        {
            return new LocalIpcAdmissionResult(
                null,
                errorCode,
                responseJson);
        }
    }

    public sealed class LocalIpcPendingRequest : IDisposable
    {
        private const int Queued = 0;
        private const int Dispatching = 1;
        private const int Completed = 2;
        private const int CompletedBeforeDispatch = 3;

        private readonly ManualResetEventSlim mCompletion =
            new ManualResetEventSlim(false);
        private readonly object mCompletionLifecycleSync = new object();
        private int mState;
        private string mResponseJson = string.Empty;
        private string mTerminalOutcome = string.Empty;
        private int mConnectionClosed;
        private bool mCompletionSignaled;
        private bool mDisposeRequested;
        private bool mCompletionDisposed;

        internal LocalIpcPendingRequest(
            string commandJson,
            LocalIpcRequestMetadata metadata,
            CommandBridgeDispatchContext dispatchContext)
        {
            CommandJson = commandJson;
            Metadata = metadata;
            DispatchContext = dispatchContext;
        }

        public string CommandJson { get; }

        public LocalIpcRequestMetadata Metadata { get; }

        public CommandBridgeDispatchContext DispatchContext { get; }

        public bool ConnectionClosed =>
            Volatile.Read(ref mConnectionClosed) != 0;

        public string TerminalOutcome => mTerminalOutcome;

        public bool WaitForCompletion(
            TimeSpan timeout,
            out string responseJson)
        {
            var completed = mCompletion.Wait(timeout);
            responseJson = completed ? mResponseJson : string.Empty;
            return completed;
        }

        public void Dispose()
        {
            var disposeCompletion = false;
            lock (mCompletionLifecycleSync)
            {
                mDisposeRequested = true;
                if (mCompletionSignaled && !mCompletionDisposed)
                {
                    mCompletionDisposed = true;
                    disposeCompletion = true;
                }
            }

            if (disposeCompletion)
                mCompletion.Dispose();
        }

        internal bool TryBeginDispatch()
        {
            return Interlocked.CompareExchange(
                       ref mState,
                       Dispatching,
                       Queued) == Queued;
        }

        internal bool TryCompleteBeforeDispatch(string responseJson)
        {
            if (Interlocked.CompareExchange(
                    ref mState,
                    CompletedBeforeDispatch,
                    Queued) != Queued)
            {
                return false;
            }

            mResponseJson = responseJson ?? string.Empty;
            mTerminalOutcome = ExtractOutcome(mResponseJson);
            SignalCompletion();
            return true;
        }

        internal void CompleteAfterDispatch(string responseJson)
        {
            if (Interlocked.CompareExchange(
                    ref mState,
                    Completed,
                    Dispatching) != Dispatching)
            {
                throw new InvalidOperationException(
                    "Request was not in dispatching state.");
            }

            mResponseJson = responseJson ?? string.Empty;
            mTerminalOutcome = ExtractOutcome(mResponseJson);
            SignalCompletion();
        }

        internal void MarkConnectionClosed()
        {
            Interlocked.Exchange(ref mConnectionClosed, 1);
        }

        private void SignalCompletion()
        {
            var disposeCompletion = false;
            lock (mCompletionLifecycleSync)
            {
                mCompletion.Set();
                mCompletionSignaled = true;
                if (mDisposeRequested && !mCompletionDisposed)
                {
                    mCompletionDisposed = true;
                    disposeCompletion = true;
                }
            }

            if (disposeCompletion)
                mCompletion.Dispose();
        }

        private static string ExtractOutcome(string responseJson)
        {
            var errorCode =
                JsonHelper.ExtractString(
                    responseJson ?? string.Empty,
                    "code");
            return string.IsNullOrEmpty(errorCode)
                ? "completed"
                : errorCode;
        }
    }
}
