using System;
using System.Globalization;
using System.Text;

namespace YokiFrame
{
    /// <summary>
    /// Versioned wire constants and JSON envelope validation for Local IPC v1.
    /// Framing I/O stays in the platform adapter.
    /// </summary>
    public static class LocalIpcProtocol
    {
        public const string Transport = "local-ipc-v1";
        public const string TransportProtocolVersion = "1";
        public const int MaxRequestFrameBytes = 256 * 1024;
        public const int MaxResponseFrameBytes = 4 * 1024 * 1024;
        public const int MaxActiveClients = 4;
        public const int MaxInflightPerConnection = 8;
        public const int MaxQueuedRequests = 64;
        public const int HandshakeTimeoutMs = 5000;
        public const int MinRequestTimeoutMs = 1;
        public const int MaxRequestTimeoutMs = 60000;
        public const int MaxTombstones = 256;
        public static readonly TimeSpan TombstoneTtl =
            TimeSpan.FromMinutes(5);

        public static bool TryValidateHello(
            string helloJson,
            string expectedProjectRootHash,
            string expectedEngineId,
            out string clientId,
            out string errorCode,
            out string errorMessage)
        {
            clientId = string.Empty;
            errorCode = string.Empty;
            errorMessage = string.Empty;

            if (!LooksLikeJsonObject(helloJson))
                return Reject(
                    "invalid-hello",
                    "Handshake frame must be a JSON object.",
                    out errorCode,
                    out errorMessage);

            var type = JsonHelper.ExtractString(helloJson, "type");
            if (!string.Equals(type, "hello", StringComparison.Ordinal))
                return Reject(
                    "invalid-hello",
                    "First frame must have type 'hello'.",
                    out errorCode,
                    out errorMessage);

            var version =
                JsonHelper.ExtractString(
                    helloJson,
                    "transportProtocolVersion");
            if (!string.Equals(
                    version,
                    TransportProtocolVersion,
                    StringComparison.Ordinal))
            {
                return Reject(
                    "protocol-mismatch",
                    "Unsupported transport protocol version.",
                    out errorCode,
                    out errorMessage);
            }

            var projectHash =
                JsonHelper.ExtractString(helloJson, "projectRootHash");
            if (!string.Equals(
                    projectHash,
                    expectedProjectRootHash,
                    StringComparison.Ordinal))
            {
                return Reject(
                    "project-mismatch",
                    "Project root hash does not match this Host.",
                    out errorCode,
                    out errorMessage);
            }

            var engineId = JsonHelper.ExtractString(helloJson, "engineId");
            if (!string.Equals(
                    engineId,
                    expectedEngineId,
                    StringComparison.Ordinal))
            {
                return Reject(
                    "engine-mismatch",
                    "Engine id does not match this Host.",
                    out errorCode,
                    out errorMessage);
            }

            clientId =
                JsonHelper.ExtractString(helloJson, "clientId") ??
                string.Empty;
            if (!CommandBridgeProtocol.IsSafeIdentifier(clientId))
            {
                return Reject(
                    "invalid-client",
                    "clientId must be a safe identifier.",
                    out errorCode,
                    out errorMessage);
            }

            return true;
        }

        public static string BuildHelloAck(
            string hostSessionId,
            string engineId,
            string projectRootHash,
            int processId,
            DateTime startedAtUtc)
        {
            var builder = new StringBuilder(512);
            builder.Append("{\"type\":\"helloAck\",");
            builder.Append("\"transportProtocolVersion\":\"");
            builder.Append(TransportProtocolVersion);
            builder.Append("\",\"hostSessionId\":\"");
            builder.Append(JsonHelper.EscapeString(hostSessionId));
            builder.Append("\",\"engineId\":\"");
            builder.Append(JsonHelper.EscapeString(engineId));
            builder.Append("\",\"projectRootHash\":\"");
            builder.Append(JsonHelper.EscapeString(projectRootHash));
            builder.Append("\",\"processId\":");
            builder.Append(processId);
            builder.Append(",\"startedAtUtc\":\"");
            builder.Append(startedAtUtc.ToString("O"));
            builder.Append("\",\"limits\":{");
            builder.Append("\"maxRequestFrameBytes\":");
            builder.Append(MaxRequestFrameBytes);
            builder.Append(",\"maxResponseFrameBytes\":");
            builder.Append(MaxResponseFrameBytes);
            builder.Append(",\"maxActiveClients\":");
            builder.Append(MaxActiveClients);
            builder.Append(",\"maxInflightPerConnection\":");
            builder.Append(MaxInflightPerConnection);
            builder.Append(",\"maxQueuedRequests\":");
            builder.Append(MaxQueuedRequests);
            builder.Append(",\"handshakeTimeoutMs\":");
            builder.Append(HandshakeTimeoutMs);
            builder.Append("},\"capabilities\":[");
            builder.Append(
                "\"request-response\",\"connection-lifecycle\"," +
                "\"bounded-main-thread-dispatch\"]}");
            return builder.ToString();
        }

        public static bool TryReadRequestMetadata(
            string commandJson,
            string expectedEngineId,
            out LocalIpcRequestMetadata metadata,
            out string errorCode,
            out string errorMessage)
        {
            metadata = null;
            errorCode = string.Empty;
            errorMessage = string.Empty;

            if (!LooksLikeJsonObject(commandJson))
            {
                return Reject(
                    "invalid-json",
                    "Request frame must be a JSON object.",
                    out errorCode,
                    out errorMessage);
            }

            var type = JsonHelper.ExtractString(commandJson, "type");
            if (!string.Equals(type, "request", StringComparison.Ordinal))
            {
                return Reject(
                    "invalid-request",
                    "Command frame must have type 'request'.",
                    out errorCode,
                    out errorMessage);
            }

            var requestId =
                JsonHelper.ExtractString(commandJson, "requestId");
            if (!CommandBridgeProtocol.IsSafeIdentifier(requestId))
            {
                return Reject(
                    "invalid-request-id",
                    "requestId must be a safe identifier.",
                    out errorCode,
                    out errorMessage);
            }

            int logicalProtocolVersion;
            if (!JsonHelper.TryExtractInt(
                    commandJson,
                    "protocolVersion",
                    out logicalProtocolVersion) ||
                logicalProtocolVersion != 2)
            {
                return Reject(
                    "action-protocol-mismatch",
                    "Local IPC v1 requires action protocolVersion 2.",
                    out errorCode,
                    out errorMessage);
            }

            var engineId = JsonHelper.ExtractString(commandJson, "engineId");
            if (!string.Equals(
                    engineId,
                    expectedEngineId,
                    StringComparison.Ordinal))
            {
                return Reject(
                    "engine-mismatch",
                    "Request engineId does not match this Host.",
                    out errorCode,
                    out errorMessage);
            }

            var source =
                JsonHelper.ExtractString(commandJson, "source") ??
                string.Empty;
            if (!string.IsNullOrEmpty(source) &&
                !CommandBridgeProtocol.IsSafeIdentifier(source))
            {
                return Reject(
                    "invalid-source",
                    "source must be a safe identifier when present.",
                    out errorCode,
                    out errorMessage);
            }

            var kit = JsonHelper.ExtractString(commandJson, "kit");
            var action = JsonHelper.ExtractString(commandJson, "action");
            if (!CommandBridgeProtocol.IsSafeIdentifier(kit) ||
                !CommandBridgeProtocol.IsSafeIdentifier(action))
            {
                return Reject(
                    "invalid-route",
                    "kit and action must be safe identifiers.",
                    out errorCode,
                    out errorMessage);
            }

            int timeoutMs;
            if (!JsonHelper.TryExtractInt(
                    commandJson,
                    "timeoutMs",
                    out timeoutMs) ||
                timeoutMs < MinRequestTimeoutMs ||
                timeoutMs > MaxRequestTimeoutMs)
            {
                return Reject(
                    "invalid-timeout",
                    "timeoutMs must be in 1..60000.",
                    out errorCode,
                    out errorMessage);
            }

            var createdAtText =
                JsonHelper.ExtractString(commandJson, "createdAtUtc");
            DateTime createdAtUtc;
            if (string.IsNullOrEmpty(createdAtText) ||
                !DateTime.TryParse(
                    createdAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out createdAtUtc))
            {
                return Reject(
                    "invalid-created-at",
                    "createdAtUtc must be an ISO-8601 timestamp.",
                    out errorCode,
                    out errorMessage);
            }

            DateTime deadlineUtc;
            try
            {
                createdAtUtc = createdAtUtc.ToUniversalTime();
                deadlineUtc =
                    createdAtUtc.AddMilliseconds(timeoutMs);
            }
            catch (ArgumentException)
            {
                return Reject(
                    "invalid-created-at",
                    "createdAtUtc and timeoutMs exceed the supported date range.",
                    out errorCode,
                    out errorMessage);
            }

            metadata = new LocalIpcRequestMetadata(
                requestId,
                kit,
                action,
                createdAtUtc,
                timeoutMs,
                deadlineUtc);
            return true;
        }

        public static string BuildRequestError(
            string requestId,
            string kit,
            string action,
            string engineId,
            string hostSessionId,
            string errorCode,
            string errorMessage,
            bool recoverable)
        {
            var response = JsonHelper.BuildError(
                requestId ?? string.Empty,
                string.IsNullOrEmpty(kit) ? "System" : kit,
                string.IsNullOrEmpty(action) ? "dispatch" : action,
                errorMessage,
                engineId,
                errorCode,
                recoverable);
            return DecorateResponse(response, hostSessionId);
        }

        public static string BuildTransportError(
            string errorCode,
            string errorMessage,
            string hostSessionId)
        {
            var builder = new StringBuilder(256);
            builder.Append("{\"type\":\"error\",\"code\":\"");
            builder.Append(JsonHelper.EscapeString(errorCode));
            builder.Append("\",\"message\":\"");
            builder.Append(JsonHelper.EscapeString(errorMessage));
            builder.Append("\",\"hostSessionId\":\"");
            builder.Append(JsonHelper.EscapeString(hostSessionId));
            builder.Append("\"}");
            return builder.ToString();
        }

        public static string DecorateResponse(
            string responseJson,
            string hostSessionId)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                responseJson =
                    "{\"status\":\"error\",\"error\":{\"code\":\"invalid-response\"," +
                    "\"message\":\"Dispatcher returned an empty response.\"}}";
            }

            var trimmed = responseJson.Trim();
            if (!LooksLikeJsonObject(trimmed))
            {
                trimmed =
                    "{\"status\":\"error\",\"error\":{\"code\":\"invalid-response\"," +
                    "\"message\":\"Dispatcher response was not a JSON object.\"}}";
            }

            var builder = new StringBuilder(trimmed.Length + 96);
            builder.Append(trimmed, 0, trimmed.Length - 1);
            var hasMembers = false;
            for (var index = 1;
                 index < trimmed.Length - 1;
                 index++)
            {
                if (!char.IsWhiteSpace(trimmed[index]))
                {
                    hasMembers = true;
                    break;
                }
            }

            if (hasMembers)
                builder.Append(',');

            builder.Append("\"type\":\"response\",\"hostSessionId\":\"");
            builder.Append(JsonHelper.EscapeString(hostSessionId));
            builder.Append("\"}");
            return builder.ToString();
        }

        public static bool LooksLikeJsonObject(string value)
        {
            return LocalIpcJsonSyntax.IsObject(value);
        }

        private static bool Reject(
            string code,
            string message,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = code;
            errorMessage = message;
            return false;
        }
    }

    /// <summary>
    /// Validated request correlation and deadline metadata.
    /// </summary>
    public sealed class LocalIpcRequestMetadata
    {
        public LocalIpcRequestMetadata(
            string requestId,
            string kit,
            string action,
            DateTime createdAtUtc,
            int timeoutMs,
            DateTime deadlineUtc)
        {
            RequestId = requestId;
            Kit = kit;
            Action = action;
            CreatedAtUtc = createdAtUtc;
            TimeoutMs = timeoutMs;
            DeadlineUtc = deadlineUtc;
        }

        public string RequestId { get; }

        public string Kit { get; }

        public string Action { get; }

        public DateTime CreatedAtUtc { get; }

        public int TimeoutMs { get; }

        public DateTime DeadlineUtc { get; }
    }
}
