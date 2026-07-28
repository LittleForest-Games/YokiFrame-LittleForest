using System;

namespace YokiFrame
{
    /// <summary>
    /// 分发器接收到的标准命令上下文。
    /// </summary>
    public sealed class CommandBridgeCommand
    {
        /// <summary>
        /// 创建标准命令上下文。
        /// </summary>
        /// <param name="requestId">请求标识符。</param>
        /// <param name="engineId">目标引擎实例标识符。</param>
        /// <param name="source">命令来源。</param>
        /// <param name="kit">目标 Kit 名称。</param>
        /// <param name="action">目标动作名称。</param>
        /// <param name="payloadJson">命令载荷 JSON。</param>
        public CommandBridgeCommand(string requestId, string engineId, string source, string kit, string action, string payloadJson)
            : this(
                requestId,
                engineId,
                source,
                kit,
                action,
                payloadJson,
                CommandBridgeDispatchContext.FileBridge)
        {
        }

        /// <summary>
        /// 创建带 transport-neutral dispatch context 的标准命令上下文。
        /// </summary>
        public CommandBridgeCommand(
            string requestId,
            string engineId,
            string source,
            string kit,
            string action,
            string payloadJson,
            CommandBridgeDispatchContext dispatchContext)
        {
            RequestId = requestId ?? string.Empty;
            EngineId = engineId ?? string.Empty;
            Source = source ?? string.Empty;
            Kit = kit ?? string.Empty;
            Action = action ?? string.Empty;
            PayloadJson = string.IsNullOrEmpty(payloadJson) ? "{}" : payloadJson;

            // 缺省 envelope：兼容旧命令路径（无 protocolVersion/createdAtUtc/timeoutMs）。
            ProtocolVersion = null;
            CreatedAtUtc = DateTime.MinValue;
            TimeoutMs = 0;
            DeadlineUtc = DateTime.MaxValue;
            DispatchContext =
                dispatchContext ?? CommandBridgeDispatchContext.FileBridge;
        }

        /// <summary>
        /// 创建带 envelope 字段的标准命令上下文。
        /// </summary>
        /// <param name="requestId">请求标识符。</param>
        /// <param name="engineId">目标引擎实例标识符。</param>
        /// <param name="source">命令来源。</param>
        /// <param name="kit">目标 Kit 名称。</param>
        /// <param name="action">目标动作名称。</param>
        /// <param name="payloadJson">命令载荷 JSON。</param>
        /// <param name="protocolVersion">协议版本（如 "1.0"）；缺省时为 null。</param>
        /// <param name="createdAtUtc">命令创建时间（UTC）。</param>
        /// <param name="timeoutMs">命令执行超时毫秒；0 表示无超时（DeadlineUtc 设为 DateTime.MaxValue）。</param>
        public CommandBridgeCommand(string requestId, string engineId, string source, string kit, string action, string payloadJson,
            string protocolVersion, DateTime createdAtUtc, int timeoutMs)
            : this(
                requestId,
                engineId,
                source,
                kit,
                action,
                payloadJson,
                protocolVersion,
                createdAtUtc,
                timeoutMs,
                CommandBridgeDispatchContext.FileBridge)
        {
        }

        /// <summary>
        /// 创建带 envelope 与 transport-neutral dispatch context 的标准命令上下文。
        /// </summary>
        public CommandBridgeCommand(
            string requestId,
            string engineId,
            string source,
            string kit,
            string action,
            string payloadJson,
            string protocolVersion,
            DateTime createdAtUtc,
            int timeoutMs,
            CommandBridgeDispatchContext dispatchContext)
        {
            RequestId = requestId ?? string.Empty;
            EngineId = engineId ?? string.Empty;
            Source = source ?? string.Empty;
            Kit = kit ?? string.Empty;
            Action = action ?? string.Empty;
            PayloadJson = string.IsNullOrEmpty(payloadJson) ? "{}" : payloadJson;

            ProtocolVersion = protocolVersion;
            CreatedAtUtc = createdAtUtc;
            TimeoutMs = timeoutMs;
            // timeoutMs=0 表示无超时，DeadlineUtc 设为 DateTime.MaxValue，IsExpired 永远为 false。
            DeadlineUtc = timeoutMs > 0 ? createdAtUtc.AddMilliseconds(timeoutMs) : DateTime.MaxValue;
            DispatchContext =
                dispatchContext ?? CommandBridgeDispatchContext.FileBridge;
        }

        /// <summary>
        /// 获取请求标识符。
        /// </summary>
        public string RequestId { get; }

        /// <summary>
        /// 获取目标引擎实例标识符。
        /// </summary>
        public string EngineId { get; }

        /// <summary>
        /// 获取命令来源。
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// 获取目标 Kit 名称。
        /// </summary>
        public string Kit { get; }

        /// <summary>
        /// 获取目标动作名称。
        /// </summary>
        public string Action { get; }

        /// <summary>
        /// 获取命令载荷 JSON。
        /// </summary>
        public string PayloadJson { get; }

        /// <summary>
        /// 获取命令协议版本（如 "1.0"）；缺省时为 null（兼容旧命令）。
        /// </summary>
        public string ProtocolVersion { get; }

        /// <summary>
        /// 获取命令创建时间（UTC）。缺省时为 <see cref="DateTime.MinValue"/>（兼容旧命令）。
        /// </summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>
        /// 获取命令执行超时毫秒；0 表示无超时。
        /// </summary>
        public int TimeoutMs { get; }

        /// <summary>
        /// 获取命令截止时间（UTC）。
        /// <see cref="TimeoutMs"/> 大于 0 时为 <see cref="CreatedAtUtc"/> + <see cref="TimeoutMs"/> 毫秒；
        /// <see cref="TimeoutMs"/> 为 0 时为 <see cref="DateTime.MaxValue"/>（永不超时）。
        /// </summary>
        public DateTime DeadlineUtc { get; }

        /// <summary>
        /// Transport-neutral request/session correlation.
        /// </summary>
        public CommandBridgeDispatchContext DispatchContext { get; }

        /// <summary>
        /// 检查命令是否已过期。
        /// </summary>
        /// <param name="nowUtc">当前 UTC 时间。</param>
        /// <returns>当前时间超过截止时间时返回 true；否则返回 false。<see cref="TimeoutMs"/> 为 0 时永远返回 false。</returns>
        public bool IsExpired(DateTime nowUtc) => nowUtc > DeadlineUtc;
    }
}
