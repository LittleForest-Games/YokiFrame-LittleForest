using System;
using System.Collections;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YokiFrame.Unity;

namespace YokiFrame.Tests
{
    /// <summary>
    /// Exercises the final Little Forest Host through its real Windows Named
    /// Pipe instead of invoking the dispatcher directly.
    /// </summary>
    public sealed class LittleForestLocalIpcHostIntegrationTests
    {
        private static readonly string[] sNativeKitNames =
        {
            "Architecture",
            "EventKit",
            "FsmKit",
            "LogKit",
            "PoolKit",
            "ResKit",
            "SingletonKit",
            "ManagedRuntimeKit",
            "ActionKit",
            "AudioKit",
            "LocalizationKit",
            "SaveKit",
            "SceneKit",
            "SpatialKit",
            "TableKit",
            "UIKit"
        };

        [UnityTest]
        public IEnumerator HostCompletesHelloAndSystemPing()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore(
                    "Local IPC Host integration currently targets Windows Editor.");
            }

            RuntimeHelpers.RunClassConstructor(
                typeof(YokiFrameLittleForestControlPlaneHost).TypeHandle);

            var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(10));
            Task<RoundTripResult> roundTrip = ExecuteRoundTripAsync(
                cancellation.Token);

            while (!roundTrip.IsCompleted)
            {
                yield return null;
            }

            cancellation.Dispose();
            if (roundTrip.IsFaulted)
            {
                Assert.Fail(
                    roundTrip.Exception?.Flatten().InnerException?.ToString()
                    ?? "Local IPC round trip failed without an exception.");
            }

            RoundTripResult result = roundTrip.Result;
            StringAssert.Contains("\"type\":\"helloAck\"", result.HelloAck);
            StringAssert.Contains(
                "\"transportProtocolVersion\":\"1\"",
                result.HelloAck);
            StringAssert.Contains("\"type\":\"response\"", result.Response);
            StringAssert.Contains(
                "\"requestId\":\"fork-live-ping\"",
                result.Response);
            StringAssert.Contains("\"kit\":\"System\"", result.Response);
            StringAssert.Contains(
                "\"action\":\"ping_response\"",
                result.Response);
            StringAssert.Contains("\"status\":\"success\"", result.Response);
            StringAssert.Contains("\"pong\":true", result.Response);
            StringAssert.Contains("\"hostSessionId\":", result.Response);
            StringAssert.Contains(
                "\"kit\":\"System\"",
                result.CommandCatalog);
            foreach (string nativeKitName in sNativeKitNames)
            {
                Assert.IsFalse(
                    result.CommandCatalog.Contains(
                        "\"kit\":\"" + nativeKitName + "\""),
                    "隔离 Host 命令目录不得注册原生 Kit: "
                    + nativeKitName);
            }
        }

        private static async Task<RoundTripResult> ExecuteRoundTripAsync(
            CancellationToken cancellationToken)
        {
            string projectRoot =
                Path.GetDirectoryName(Application.dataPath)
                ?? Application.dataPath;
            string projectRootHash =
                LocalIpcEndpoint.ComputeProjectRootHash(projectRoot);
            string pipeName = LocalIpcEndpoint.BuildPipeName(
                projectRoot,
                "unity-editor");

            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await Task.Run(
                () => client.Connect(3000),
                cancellationToken);

            string hello =
                "{\"type\":\"hello\",\"transportProtocolVersion\":\"1\","
                + "\"projectRootHash\":\""
                + projectRootHash
                + "\",\"engineId\":\"unity-editor\","
                + "\"clientId\":\"fork-live-test\"}";
            await WriteFrameAsync(
                client,
                hello,
                LocalIpcProtocol.MaxRequestFrameBytes,
                cancellationToken);
            string helloAck = await ReadFrameAsync(
                client,
                LocalIpcProtocol.MaxResponseFrameBytes,
                cancellationToken);

            string request = BuildRequest(
                "fork-live-ping",
                "ping");
            await WriteFrameAsync(
                client,
                request,
                LocalIpcProtocol.MaxRequestFrameBytes,
                cancellationToken);
            string response = await ReadFrameAsync(
                client,
                LocalIpcProtocol.MaxResponseFrameBytes,
                cancellationToken);

            await WriteFrameAsync(
                client,
                BuildRequest(
                    "fork-live-command-catalog",
                    "list_commands"),
                LocalIpcProtocol.MaxRequestFrameBytes,
                cancellationToken);
            string commandCatalog = await ReadFrameAsync(
                client,
                LocalIpcProtocol.MaxResponseFrameBytes,
                cancellationToken);
            return new RoundTripResult(
                helloAck,
                response,
                commandCatalog);
        }

        private static string BuildRequest(
            string requestId,
            string action)
        {
            return "{\"type\":\"request\",\"protocolVersion\":2,"
                   + "\"requestId\":\""
                   + requestId
                   + "\",\"engineId\":\"unity-editor\","
                   + "\"source\":\"fork-live-test\","
                   + "\"kit\":\"System\",\"action\":\""
                   + action
                   + "\",\"payload\":{},\"createdAtUtc\":\""
                   + DateTime.UtcNow.ToString("O")
                   + "\",\"timeoutMs\":5000}";
        }

        private static async Task WriteFrameAsync(
            Stream stream,
            string json,
            int maxFrameBytes,
            CancellationToken cancellationToken)
        {
            byte[] frame = LocalIpcFrameCodec.Encode(json, maxFrameBytes);
            await stream.WriteAsync(
                frame,
                0,
                frame.Length,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task<string> ReadFrameAsync(
            Stream stream,
            int maxFrameBytes,
            CancellationToken cancellationToken)
        {
            var prefix = new byte[4];
            await ReadExactAsync(
                stream,
                prefix,
                prefix.Length,
                cancellationToken);
            uint payloadLength =
                LocalIpcFrameCodec.ReadUInt32LittleEndian(prefix, 0);
            if (payloadLength == 0 || payloadLength > maxFrameBytes)
            {
                throw new InvalidDataException(
                    "Local IPC response frame length is invalid: "
                    + payloadLength);
            }

            int payloadByteCount = checked((int)payloadLength);
            var payload = new byte[payloadByteCount];
            await ReadExactAsync(
                stream,
                payload,
                payload.Length,
                cancellationToken);
            return new UTF8Encoding(false, true).GetString(payload);
        }

        private static async Task ReadExactAsync(
            Stream stream,
            byte[] buffer,
            int length,
            CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(
                    buffer,
                    offset,
                    length - offset,
                    cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "Local IPC pipe closed before a complete frame arrived.");
                }

                offset += read;
            }
        }

        private sealed class RoundTripResult
        {
            internal RoundTripResult(
                string helloAck,
                string response,
                string commandCatalog)
            {
                HelloAck = helloAck;
                Response = response;
                CommandCatalog = commandCatalog;
            }

            internal string HelloAck { get; }

            internal string Response { get; }

            internal string CommandCatalog { get; }
        }
    }
}
