#if UNITY_EDITOR || (GODOT && TOOLS) || YOKIFRAME_TOOLING
using System;
using System.Text;

namespace YokiFrame
{
    /// <summary>
    /// 保留 Little Forest Local IPC v1 的 action envelope 序列化契约。
    /// JSON 扫描复用当前上游 JsonHelper 的顶层字段实现，不恢复旧 FileBridge。
    /// </summary>
    public static partial class JsonHelper
    {
        /// <summary>
        /// 提取顶层字段的完整 JSON 值文本。
        /// </summary>
        public static string ExtractRaw(string json, string fieldName)
        {
            if (!TryFindTopLevelValue(json, fieldName, out int valueStart))
            {
                return null;
            }

            int valueEnd = valueStart;
            if (!TrySkipValue(json, ref valueEnd))
            {
                return null;
            }

            return json.Substring(valueStart, valueEnd - valueStart);
        }

        /// <summary>
        /// 构建 Local IPC action protocol v2 成功响应。
        /// </summary>
        public static string BuildResponse(
            string requestId,
            string kit,
            string action,
            string status,
            string dataJson)
        {
            return BuildResponse(
                requestId,
                kit,
                action,
                status,
                dataJson,
                "base");
        }

        /// <summary>
        /// 构建带 engine correlation 的 Local IPC action protocol v2 成功响应。
        /// </summary>
        public static string BuildResponse(
            string requestId,
            string kit,
            string action,
            string status,
            string dataJson,
            string engineId)
        {
            var builder = new StringBuilder(256);
            string completedAtUtc = DateTime.UtcNow.ToString("O");
            builder.Append("{\"protocolVersion\":2,\"requestId\":\"");
            builder.Append(EscapeString(requestId ?? string.Empty));
            builder.Append("\",\"engineId\":\"");
            builder.Append(EscapeString(string.IsNullOrEmpty(engineId) ? "base" : engineId));
            builder.Append("\",\"status\":\"");
            builder.Append(EscapeString(status ?? string.Empty));
            builder.Append("\",\"kit\":\"");
            builder.Append(EscapeString(kit ?? string.Empty));
            builder.Append("\",\"action\":\"");
            builder.Append(EscapeString(action ?? string.Empty));
            builder.Append("_response\",\"timestamp\":\"");
            builder.Append(completedAtUtc);
            builder.Append("\",\"completedAtUtc\":\"");
            builder.Append(completedAtUtc);
            builder.Append('"');
            if (!string.IsNullOrEmpty(dataJson))
            {
                builder.Append(",\"data\":");
                builder.Append(dataJson);
            }

            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// 构建 Local IPC action protocol v2 错误响应。
        /// </summary>
        public static string BuildError(
            string requestId,
            string kit,
            string action,
            string errorMessage)
        {
            return BuildError(
                requestId,
                kit,
                action,
                errorMessage,
                "base",
                "CommandError",
                false);
        }

        /// <summary>
        /// 构建带稳定错误码、engine correlation 与恢复标记的错误响应。
        /// </summary>
        public static string BuildError(
            string requestId,
            string kit,
            string action,
            string errorMessage,
            string engineId,
            string errorCode,
            bool recoverable)
        {
            var builder = new StringBuilder(256);
            string completedAtUtc = DateTime.UtcNow.ToString("O");
            string safeMessage = errorMessage ?? "Unknown error";
            builder.Append("{\"protocolVersion\":2,\"requestId\":\"");
            builder.Append(EscapeString(requestId ?? string.Empty));
            builder.Append("\",\"engineId\":\"");
            builder.Append(EscapeString(string.IsNullOrEmpty(engineId) ? "base" : engineId));
            builder.Append("\",\"status\":\"error\",\"kit\":\"");
            builder.Append(EscapeString(kit ?? string.Empty));
            builder.Append("\",\"action\":\"");
            builder.Append(EscapeString(action ?? string.Empty));
            builder.Append("_response\",\"timestamp\":\"");
            builder.Append(completedAtUtc);
            builder.Append("\",\"completedAtUtc\":\"");
            builder.Append(completedAtUtc);
            builder.Append("\",\"error\":{\"code\":\"");
            builder.Append(EscapeString(string.IsNullOrEmpty(errorCode) ? "CommandError" : errorCode));
            builder.Append("\",\"message\":\"");
            builder.Append(EscapeString(safeMessage));
            builder.Append("\",\"recoverable\":");
            builder.Append(recoverable ? "true" : "false");
            builder.Append("},\"errorMessage\":\"");
            builder.Append(EscapeString(safeMessage));
            builder.Append("\"}");
            return builder.ToString();
        }
    }
}
#endif
