using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace YokiFrame.Tests
{
    [TestFixture]
    public sealed class LocalIpcEndpointTests
    {
        [TestCase(
            @"G:\Unity\Little Forest",
            "G:/UNITY/LITTLE FOREST",
            "69418c0a59d5c5d08a520f2d500623896cf4c08d536654a517768a7b02a4d39d")]
        [TestCase(
            @"g:\unity\little forest\",
            "G:/UNITY/LITTLE FOREST",
            "69418c0a59d5c5d08a520f2d500623896cf4c08d536654a517768a7b02a4d39d")]
        [TestCase(
            @"C:\Project With 空格\",
            "C:/PROJECT WITH 空格",
            "661649b8309504c6046adb0b63807c67fe3ba37a1827090e80bc420e26a9cd37")]
        [TestCase(
            @"C:\",
            "C:/",
            "8545a81f99f36eda523c5e88e45567d21aaf5ea733e5595596cb07dd7364e8e4")]
        public void GoldenVectors_AgreeAcrossCanonicalPathAndSha256(
            string raw,
            string normalized,
            string sha256)
        {
            Assert.AreEqual(
                normalized,
                LocalIpcEndpoint.NormalizeProjectRoot(raw));
            Assert.AreEqual(
                sha256,
                LocalIpcEndpoint.ComputeProjectRootHash(raw));
            Assert.AreEqual(
                LocalIpcEndpoint.PipeNamePrefix +
                sha256 +
                ".unity-editor",
                LocalIpcEndpoint.BuildPipeName(
                    raw,
                    "unity-editor"));
        }

        [Test]
        public void BuildPipeName_UnsafeEngineId_IsRejected()
        {
            Assert.Throws<ArgumentException>(
                () => LocalIpcEndpoint.BuildPipeName(
                    @"C:\Project",
                    @"unity\editor"));
        }
    }

    [TestFixture]
    public sealed class LocalIpcFrameCodecTests
    {
        [Test]
        public void EncodeAndDecode_UsesLittleEndianUtf8Frame()
        {
            const string Json = "{\"message\":\"森林\"}";
            var frame = LocalIpcFrameCodec.Encode(Json, 1024);
            var expectedLength = Encoding.UTF8.GetByteCount(Json);
            Assert.AreEqual(expectedLength, frame[0]);
            Assert.AreEqual(0, frame[1]);

            string decoded;
            int consumed;
            string error;
            Assert.IsTrue(
                LocalIpcFrameCodec.TryDecode(
                    frame,
                    0,
                    frame.Length,
                    1024,
                    out decoded,
                    out consumed,
                    out error));
            Assert.AreEqual(Json, decoded);
            Assert.AreEqual(frame.Length, consumed);
            Assert.AreEqual(string.Empty, error);
        }

        [Test]
        public void TryDecode_PartialPrefixOrPayload_DoesNotDispatchFrame()
        {
            var frame = LocalIpcFrameCodec.Encode("{\"ok\":true}", 1024);
            AssertIncomplete(frame, 3);
            AssertIncomplete(frame, frame.Length - 1);
        }

        [Test]
        public void TryDecode_OversizePrefix_IsRejectedWithoutPayload()
        {
            var frame = new byte[4];
            LocalIpcFrameCodec.WriteUInt32LittleEndian(frame, 0, 1025);

            string json;
            int consumed;
            string error;
            Assert.IsFalse(
                LocalIpcFrameCodec.TryDecode(
                    frame,
                    0,
                    frame.Length,
                    1024,
                    out json,
                    out consumed,
                    out error));
            Assert.AreEqual("frame-too-large", error);
            Assert.AreEqual(0, consumed);
        }

        [Test]
        public void TryDecode_InvalidUtf8_IsRejected()
        {
            var frame = new byte[] { 2, 0, 0, 0, 0xC3, 0x28 };
            string json;
            int consumed;
            string error;

            Assert.IsFalse(
                LocalIpcFrameCodec.TryDecode(
                    frame,
                    0,
                    frame.Length,
                    1024,
                    out json,
                    out consumed,
                    out error));
            Assert.AreEqual("invalid-utf8", error);
            Assert.AreEqual(0, consumed);
        }

        private static void AssertIncomplete(byte[] frame, int count)
        {
            string json;
            int consumed;
            string error;
            Assert.IsFalse(
                LocalIpcFrameCodec.TryDecode(
                    frame,
                    0,
                    count,
                    1024,
                    out json,
                    out consumed,
                    out error));
            Assert.AreEqual(string.Empty, error);
            Assert.AreEqual(0, consumed);
        }
    }

    [TestFixture]
    public sealed class LocalIpcProtocolTests
    {
        [Test]
        public void HelloAck_ContainsSessionIdentityLimitsAndTransportCapabilities()
        {
            const string Hash =
                "69418c0a59d5c5d08a520f2d500623896cf4c08d536654a517768a7b02a4d39d";
            string clientId;
            string errorCode;
            string errorMessage;
            var hello =
                "{\"type\":\"hello\",\"transportProtocolVersion\":\"1\"," +
                "\"projectRootHash\":\"" + Hash + "\"," +
                "\"engineId\":\"unity-editor\",\"clientId\":\"node-test\"}";

            Assert.IsTrue(
                LocalIpcProtocol.TryValidateHello(
                    hello,
                    Hash,
                    "unity-editor",
                    out clientId,
                    out errorCode,
                    out errorMessage));
            Assert.AreEqual("node-test", clientId);

            var ack = LocalIpcProtocol.BuildHelloAck(
                "session_1",
                "unity-editor",
                Hash,
                42,
                new DateTime(
                    2026,
                    7,
                    26,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc));
            StringAssert.Contains("\"type\":\"helloAck\"", ack);
            StringAssert.Contains("\"hostSessionId\":\"session_1\"", ack);
            StringAssert.Contains("\"maxQueuedRequests\":64", ack);
            StringAssert.Contains("\"handshakeTimeoutMs\":5000", ack);
            StringAssert.Contains("\"connection-lifecycle\"", ack);
        }

        [Test]
        public void Hello_ProjectMismatch_IsRejected()
        {
            string clientId;
            string errorCode;
            string errorMessage;
            var accepted = LocalIpcProtocol.TryValidateHello(
                "{\"type\":\"hello\",\"transportProtocolVersion\":\"1\"," +
                "\"projectRootHash\":\"wrong\",\"engineId\":\"unity-editor\"," +
                "\"clientId\":\"test-client\"}",
                "expected",
                "unity-editor",
                out clientId,
                out errorCode,
                out errorMessage);

            Assert.IsFalse(accepted);
            Assert.AreEqual("project-mismatch", errorCode);
        }

        [Test]
        public void RequestMetadata_DateOverflow_IsRejectedAtTrustBoundary()
        {
            LocalIpcRequestMetadata metadata;
            string errorCode;
            string errorMessage;
            var accepted = LocalIpcProtocol.TryReadRequestMetadata(
                BuildRequest(
                    "overflow",
                    new DateTime(
                        9999,
                        12,
                        31,
                        23,
                        59,
                        59,
                        DateTimeKind.Utc),
                    60000),
                "unity-editor",
                out metadata,
                out errorCode,
                out errorMessage);

            Assert.IsFalse(accepted);
            Assert.AreEqual("invalid-created-at", errorCode);
        }

        [Test]
        public void RequestMetadata_LogicalProtocolMismatch_IsRejected()
        {
            LocalIpcRequestMetadata metadata;
            string errorCode;
            string errorMessage;
            var request = BuildRequest(
                    "wrong_protocol",
                    DateTime.UtcNow,
                    5000)
                .Replace(
                    "\"protocolVersion\":2",
                    "\"protocolVersion\":1");

            Assert.IsFalse(
                LocalIpcProtocol.TryReadRequestMetadata(
                    request,
                    "unity-editor",
                    out metadata,
                    out errorCode,
                    out errorMessage));
            Assert.AreEqual(
                "action-protocol-mismatch",
                errorCode);
        }

        [Test]
        public void RequestMetadata_MalformedJsonWithValidFields_IsRejected()
        {
            LocalIpcRequestMetadata metadata;
            string errorCode;
            string errorMessage;
            var malformed = BuildRequest(
                    "malformed",
                    DateTime.UtcNow,
                    5000)
                .Replace(
                    "\"payload\":{}",
                    "\"payload\":{} BROKEN");

            Assert.IsFalse(
                LocalIpcProtocol.TryReadRequestMetadata(
                    malformed,
                    "unity-editor",
                    out metadata,
                    out errorCode,
                    out errorMessage));
            Assert.AreEqual("invalid-json", errorCode);
        }

        [Test]
        public void DecorateResponse_WhitespaceOnlyObject_RemainsValid()
        {
            var response =
                LocalIpcProtocol.DecorateResponse(
                    "{ }",
                    "session_1");

            Assert.IsTrue(
                LocalIpcProtocol.LooksLikeJsonObject(response));
            Assert.AreEqual(
                "{\"type\":\"response\",\"hostSessionId\":\"session_1\"}",
                response.Replace(" ", string.Empty));
        }

        internal static string BuildRequest(
            string requestId,
            DateTime createdAtUtc,
            int timeoutMs,
            string kit = "ProbeKit",
            string action = "ping")
        {
            return "{\"type\":\"request\",\"protocolVersion\":2," +
                   "\"requestId\":\"" + requestId + "\"," +
                   "\"engineId\":\"unity-editor\",\"source\":\"test\"," +
                   "\"kit\":\"" + kit + "\",\"action\":\"" + action + "\"," +
                   "\"payload\":{},\"createdAtUtc\":\"" +
                   createdAtUtc.ToString("O") +
                   "\",\"timeoutMs\":" + timeoutMs + "}";
        }
    }

    [TestFixture]
    public sealed class LocalIpcRequestBrokerTests
    {
        private const string SessionId = "session_1";
        private KitCommandDispatcher mDispatcher;
        private ProbeHandler mHandler;

        [SetUp]
        public void SetUp()
        {
            mDispatcher = new KitCommandDispatcher
            {
                DefaultEngineId = "unity-editor"
            };
            mHandler = new ProbeHandler();
            mDispatcher.Register(mHandler);
        }

        [Test]
        public void Drain_DispatchesWithImmutableContextAndCorrelatesResponse()
        {
            var now = DateTime.UtcNow;
            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                var context = Context("connection_1", now);
                var admitted = broker.Admit(
                    LocalIpcProtocolTests.BuildRequest(
                        "request_1",
                        now,
                        5000),
                    context,
                    now);
                Assert.IsTrue(admitted.Admitted);

                Assert.AreEqual(
                    1,
                    broker.Drain(
                        mDispatcher,
                        16,
                        TimeSpan.Zero,
                        () => now));

                string response;
                Assert.IsTrue(
                    admitted.Pending.WaitForCompletion(
                        TimeSpan.FromSeconds(1),
                        out response));
                StringAssert.Contains("\"type\":\"response\"", response);
                StringAssert.Contains(
                    "\"hostSessionId\":\"session_1\"",
                    response);
                StringAssert.Contains("\"requestId\":\"request_1\"", response);
                StringAssert.Contains("\"kit\":\"ProbeKit\"", response);
                StringAssert.Contains("\"action\":\"ping_response\"", response);
                Assert.AreSame(context, mHandler.LastCommand.DispatchContext);
                admitted.Pending.Dispose();
            }
        }

        [Test]
        public void DuplicateInflightThenCompleted_NeverRedispatches()
        {
            var now = DateTime.UtcNow;
            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                var request =
                    LocalIpcProtocolTests.BuildRequest(
                        "duplicate_1",
                        now,
                        5000);
                var first = broker.Admit(
                    request,
                    Context("connection_1", now),
                    now);
                var inflightDuplicate = broker.Admit(
                    request,
                    Context("connection_2", now),
                    now);
                Assert.IsTrue(first.Admitted);
                Assert.IsFalse(inflightDuplicate.Admitted);
                Assert.AreEqual(
                    "duplicate-inflight",
                    inflightDuplicate.ErrorCode);

                broker.Drain(
                    mDispatcher,
                    1,
                    TimeSpan.Zero,
                    () => now);
                string response;
                Assert.IsTrue(
                    first.Pending.WaitForCompletion(
                        TimeSpan.FromSeconds(1),
                        out response));

                var completedDuplicate = broker.Admit(
                    request,
                    Context("connection_2", now),
                    now);
                Assert.IsFalse(completedDuplicate.Admitted);
                Assert.AreEqual(
                    "duplicate-completed",
                    completedDuplicate.ErrorCode);
                Assert.AreEqual(1, mHandler.CallCount);
                first.Pending.Dispose();
            }
        }

        [Test]
        public void DeadlineElapsedBeforeDrain_DoesNotInvokeHandler()
        {
            var now = DateTime.UtcNow;
            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                var admitted = broker.Admit(
                    LocalIpcProtocolTests.BuildRequest(
                        "expires_1",
                        now,
                        10),
                    Context("connection_1", now),
                    now);
                Assert.IsTrue(admitted.Admitted);

                broker.Drain(
                    mDispatcher,
                    1,
                    TimeSpan.Zero,
                    () => now.AddMilliseconds(11));
                string response;
                Assert.IsTrue(
                    admitted.Pending.WaitForCompletion(
                        TimeSpan.FromSeconds(1),
                        out response));
                StringAssert.Contains("\"code\":\"command-expired\"", response);
                Assert.AreEqual(0, mHandler.CallCount);
                admitted.Pending.Dispose();
            }
        }

        [Test]
        public void FutureClientClock_CannotExtendHostTimeoutBudget()
        {
            var now = DateTime.UtcNow;
            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                var admitted = broker.Admit(
                    LocalIpcProtocolTests.BuildRequest(
                        "future_clock_1",
                        now.AddDays(1),
                        10),
                    Context("connection_1", now),
                    now);
                Assert.IsTrue(admitted.Admitted);

                broker.Drain(
                    mDispatcher,
                    1,
                    TimeSpan.Zero,
                    () => now.AddMilliseconds(11));
                string response;
                Assert.IsTrue(
                    admitted.Pending.WaitForCompletion(
                        TimeSpan.FromSeconds(1),
                        out response));
                StringAssert.Contains(
                    "\"code\":\"command-expired\"",
                    response);
                Assert.AreEqual(0, mHandler.CallCount);
                admitted.Pending.Dispose();
            }
        }

        [Test]
        public void PerConnectionAndGlobalQueueCaps_ReturnBoundedErrors()
        {
            var now = DateTime.UtcNow;
            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                var pending =
                    new List<LocalIpcPendingRequest>();
                for (var connection = 0;
                     connection < LocalIpcProtocol.MaxActiveClients * 2;
                     connection++)
                {
                    for (var request = 0;
                         request < LocalIpcProtocol.MaxInflightPerConnection;
                         request++)
                    {
                        var id =
                            "queued_" + connection + "_" + request;
                        var result = broker.Admit(
                            LocalIpcProtocolTests.BuildRequest(
                                id,
                                now,
                                60000),
                            Context(
                                "connection_" + connection,
                                now),
                            now);
                        Assert.IsTrue(result.Admitted, id);
                        pending.Add(result.Pending);
                    }
                }

                Assert.AreEqual(
                    LocalIpcProtocol.MaxQueuedRequests,
                    broker.QueuedCount);
                var busy = broker.Admit(
                    LocalIpcProtocolTests.BuildRequest(
                        "busy_1",
                        now,
                        60000),
                    Context("connection_8", now),
                    now);
                Assert.IsFalse(busy.Admitted);
                Assert.AreEqual("busy", busy.ErrorCode);

                broker.Dispose();
                foreach (var item in pending)
                    item.Dispose();
            }

            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                var pending =
                    new List<LocalIpcPendingRequest>();
                for (var index = 0;
                     index < LocalIpcProtocol.MaxInflightPerConnection;
                     index++)
                {
                    var result = broker.Admit(
                        LocalIpcProtocolTests.BuildRequest(
                            "inflight_" + index,
                            now,
                            60000),
                        Context("one_connection", now),
                        now);
                    Assert.IsTrue(result.Admitted);
                    pending.Add(result.Pending);
                }

                var tooMany = broker.Admit(
                    LocalIpcProtocolTests.BuildRequest(
                        "inflight_over",
                        now,
                        60000),
                    Context("one_connection", now),
                    now);
                Assert.IsFalse(tooMany.Admitted);
                Assert.AreEqual(
                    "too-many-inflight",
                    tooMany.ErrorCode);

                broker.Dispose();
                foreach (var item in pending)
                    item.Dispose();
            }
        }

        [Test]
        public void DisconnectCancelsQueuedWorkAndDisposeIsIdempotent()
        {
            var now = DateTime.UtcNow;
            var broker =
                new LocalIpcRequestBroker(
                    "unity-editor",
                    SessionId);
            var admitted = broker.Admit(
                LocalIpcProtocolTests.BuildRequest(
                    "disconnect_1",
                    now,
                    5000),
                Context("connection_1", now),
                now);

            broker.CancelConnection(
                "connection_1",
                "client closed",
                now);
            string response;
            Assert.IsTrue(
                admitted.Pending.WaitForCompletion(
                    TimeSpan.FromSeconds(1),
                    out response));
            StringAssert.Contains(
                "\"code\":\"client-disconnected\"",
                response);
            broker.Drain(
                mDispatcher,
                16,
                TimeSpan.Zero,
                () => now);
            Assert.AreEqual(0, mHandler.CallCount);

            Assert.DoesNotThrow(() => broker.Dispose());
            Assert.DoesNotThrow(() => broker.Dispose());
            admitted.Pending.Dispose();
        }

        [Test]
        public void DisposeWhileDispatching_DefersCompletionReleaseUntilTerminal()
        {
            var now = DateTime.UtcNow;
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                var dispatcher = new KitCommandDispatcher
                {
                    DefaultEngineId = "unity-editor"
                };
                dispatcher.Register(
                    new BlockingProbeHandler(entered, release));
                var admitted = broker.Admit(
                    LocalIpcProtocolTests.BuildRequest(
                        "dispose_during_dispatch_1",
                        now,
                        5000),
                    Context("connection_1", now),
                    now);
                Assert.IsTrue(admitted.Admitted);

                Exception drainError = null;
                var drainThread = new Thread(
                    () =>
                    {
                        try
                        {
                            broker.Drain(
                                dispatcher,
                                1,
                                TimeSpan.Zero,
                                () => now);
                        }
                        catch (Exception exception)
                        {
                            drainError = exception;
                        }
                    })
                {
                    IsBackground = true
                };

                try
                {
                    drainThread.Start();
                    Assert.IsTrue(
                        entered.Wait(TimeSpan.FromSeconds(1)),
                        "Handler did not enter dispatch.");
                    broker.CancelConnection(
                        "connection_1",
                        "response ownership transfer failed",
                        now);
                    admitted.Pending.Dispose();
                }
                finally
                {
                    release.Set();
                    drainThread.Join(1000);
                }

                Assert.IsFalse(
                    drainThread.IsAlive,
                    "Drain thread did not finish.");
                Assert.IsNull(drainError);
                Assert.AreEqual(0, broker.InflightCount);
            }
        }

        [Test]
        public void Tombstones_AreBoundedAndEvictOldestRequestId()
        {
            var now = DateTime.UtcNow;
            using (var broker =
                   new LocalIpcRequestBroker(
                       "unity-editor",
                       SessionId))
            {
                for (var index = 0;
                     index <= LocalIpcProtocol.MaxTombstones;
                     index++)
                {
                    var result = broker.Admit(
                        LocalIpcProtocolTests.BuildRequest(
                            "tombstone_" + index,
                            now,
                            60000),
                        Context("connection_1", now),
                        now);
                    Assert.IsTrue(result.Admitted);
                    broker.Drain(
                        mDispatcher,
                        1,
                        TimeSpan.Zero,
                        () => now);
                    string response;
                    Assert.IsTrue(
                        result.Pending.WaitForCompletion(
                            TimeSpan.FromSeconds(1),
                            out response));
                    result.Pending.Dispose();
                }

                Assert.AreEqual(
                    LocalIpcProtocol.MaxTombstones,
                    broker.TombstoneCount);
                var oldestAgain = broker.Admit(
                    LocalIpcProtocolTests.BuildRequest(
                        "tombstone_0",
                        now,
                        60000),
                    Context("connection_1", now),
                    now);
                Assert.IsTrue(oldestAgain.Admitted);
                broker.Drain(
                    mDispatcher,
                    1,
                    TimeSpan.Zero,
                    () => now);
                string finalResponse;
                Assert.IsTrue(
                    oldestAgain.Pending.WaitForCompletion(
                        TimeSpan.FromSeconds(1),
                        out finalResponse));
                oldestAgain.Pending.Dispose();
            }
        }

        private static CommandBridgeDispatchContext Context(
            string connectionId,
            DateTime receivedAtUtc)
        {
            return new CommandBridgeDispatchContext(
                LocalIpcProtocol.Transport,
                SessionId,
                connectionId,
                receivedAtUtc);
        }

        private sealed class ProbeHandler :
            IContextualKitCommandHandler
        {
            public string KitName => "ProbeKit";

            public string[] SupportedActions { get; } =
                new[] { "ping" };

            public int CallCount { get; private set; }

            public CommandBridgeCommand LastCommand { get; private set; }

            public string HandleCommand(CommandBridgeCommand command)
            {
                CallCount++;
                LastCommand = command;
                return "{\"ok\":true}";
            }

            public string HandleAction(
                string action,
                string payloadJson)
            {
                throw new AssertionException(
                    "Contextual handler must receive HandleCommand.");
            }
        }

        private sealed class BlockingProbeHandler : IKitCommandHandler
        {
            private readonly ManualResetEventSlim mEntered;
            private readonly ManualResetEventSlim mRelease;

            internal BlockingProbeHandler(
                ManualResetEventSlim entered,
                ManualResetEventSlim release)
            {
                mEntered = entered;
                mRelease = release;
            }

            public string KitName => "ProbeKit";

            public string[] SupportedActions { get; } =
                new[] { "ping" };

            public string HandleAction(
                string action,
                string payloadJson)
            {
                mEntered.Set();
                if (!mRelease.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Test release timed out.");
                return "{\"ok\":true}";
            }
        }
    }

    [TestFixture]
    public sealed class CommandBridgeConnectionRegistryTests
    {
        [Test]
        public void ObserverExceptionsAreIsolatedAndTokenStopsNotifications()
        {
            var registry = new CommandBridgeConnectionRegistry();
            var throwing =
                registry.Register(new ThrowingObserver());
            var recordingObserver = new RecordingObserver();
            var recording = registry.Register(recordingObserver);
            var context = new CommandBridgeConnectionContext(
                LocalIpcProtocol.Transport,
                "session_1",
                "connection_1",
                "client_1",
                DateTime.UtcNow);

            Assert.DoesNotThrow(
                () => registry.NotifyConnected(context));
            Assert.AreEqual(1, recordingObserver.ConnectCount);
            Assert.DoesNotThrow(
                () => registry.NotifyDisconnected(
                    context,
                    "test"));
            Assert.AreEqual(1, recordingObserver.DisconnectCount);
            Assert.AreEqual("test", recordingObserver.LastReason);

            recording.Dispose();
            registry.NotifyConnected(context);
            Assert.AreEqual(1, recordingObserver.ConnectCount);
            Assert.DoesNotThrow(() => throwing.Dispose());
            Assert.DoesNotThrow(() => throwing.Dispose());
        }

        [Test]
        public void ExtensionContext_RegistersConnectionObserverToken()
        {
            var dispatcher = new KitCommandDispatcher();
            var registry = new CommandBridgeConnectionRegistry();
            var context =
                new CommandBridgeExtensionContext(
                    dispatcher,
                    registry,
                    LocalIpcProtocol.Transport);
            var connectionContext =
                context as ICommandBridgeConnectionExtensionContext;
            Assert.IsNotNull(connectionContext);
            Assert.AreEqual(
                LocalIpcProtocol.Transport,
                connectionContext.CommandTransport);
            var token =
                connectionContext.RegisterConnectionObserver(
                    new RecordingObserver());

            Assert.AreSame(token, context.Tokens[0]);
            Assert.Throws<InvalidOperationException>(
                () => new CommandBridgeExtensionContext(dispatcher)
                    .RegisterConnectionObserver(
                        new RecordingObserver()));
        }

        private sealed class RecordingObserver :
            ICommandBridgeConnectionObserver
        {
            public int ConnectCount { get; private set; }

            public int DisconnectCount { get; private set; }

            public string LastReason { get; private set; }

            public void OnConnected(
                CommandBridgeConnectionContext connection)
            {
                ConnectCount++;
            }

            public void OnDisconnected(
                CommandBridgeConnectionContext connection,
                string reason)
            {
                DisconnectCount++;
                LastReason = reason;
            }
        }

        private sealed class ThrowingObserver :
            ICommandBridgeConnectionObserver
        {
            public void OnConnected(
                CommandBridgeConnectionContext connection)
            {
                throw new InvalidOperationException("connect");
            }

            public void OnDisconnected(
                CommandBridgeConnectionContext connection,
                string reason)
            {
                throw new InvalidOperationException("disconnect");
            }
        }
    }
}
