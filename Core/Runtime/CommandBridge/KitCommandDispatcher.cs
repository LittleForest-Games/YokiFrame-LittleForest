using System;
using System.Collections.Generic;
using System.Text;

namespace YokiFrame
{
    /// <summary>
    /// Kit 命令分发器（纯 C#，跨引擎）。
    /// 根据 kit 字段把命令路由到匹配的 IKitCommandHandler。
    /// </summary>
    public sealed class KitCommandDispatcher
    {
        private readonly Dictionary<string, IKitCommandHandler> mHandlers = new();
        private readonly List<Func<CommandBridgeCommand, CommandBridgePolicyResult>> mPolicies = new();

        /// <summary>
        /// 命令缺少 engineId 时使用的默认宿主引擎标识。
        /// </summary>
        public string DefaultEngineId { get; set; } = "base";

        /// <summary>
        /// 可选命令策略钩子，用于在分发前拒绝或允许命令。
        /// 保留为 [Obsolete] 兼容属性，内部转换为单元素组合链；新代码应使用 <see cref="RegisterPolicy"/>。
        /// </summary>
        [Obsolete("Use RegisterPolicy instead. This property will be removed in a future version.")]
        public Func<CommandBridgeCommand, CommandBridgePolicyResult> CommandPolicy
        {
            get => mPolicies.Count > 0 ? mPolicies[0] : null;
            set
            {
                mPolicies.Clear();
                if (value != null) mPolicies.Add(value);
            }
        }

        /// <summary>
        /// 注册命令策略到组合链，返回 token 用于注销。
        /// 组合链语义：任一策略拒绝即拒绝，不能由后注册策略重新放行；
        /// 策略返回 null 视为 Deny（错误码 PolicyDenied），不静默放行。
        /// </summary>
        /// <param name="policy">命令策略委托。</param>
        /// <returns>用于注销策略的 token；dispose 后从组合链移除。</returns>
        public IDisposable RegisterPolicy(Func<CommandBridgeCommand, CommandBridgePolicyResult> policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            mPolicies.Add(policy);
            return new PolicyToken(this, policy);
        }

        /// <summary>
        /// 注册一个 Kit 命令处理器。
        /// 重复 KitName 抛 InvalidOperationException（破坏性变更，YokiFrame 2.0 preview 阶段可接受）。
        /// </summary>
        public void Register(IKitCommandHandler handler)
        {
            if (handler == default) throw new ArgumentNullException(nameof(handler));
            if (mHandlers.ContainsKey(handler.KitName))
                throw new InvalidOperationException(
                    $"A handler for kit '{handler.KitName}' is already registered. " +
                    $"Use Unregister first or check existing registration.");
            mHandlers[handler.KitName] = handler;
        }

        /// <summary>
        /// 尝试注册一个 Kit 命令处理器，重复 KitName 时返回 false（不抛异常）。
        /// </summary>
        /// <param name="handler">要注册的处理器。</param>
        /// <returns>成功注册时返回 true；handler 为 null 或 KitName 已存在时返回 false。</returns>
        public bool TryRegister(IKitCommandHandler handler)
        {
            if (handler == null || mHandlers.ContainsKey(handler.KitName))
                return false;
            mHandlers[handler.KitName] = handler;
            return true;
        }

        /// <summary>注销一个 Kit 命令处理器。</summary>
        public void Unregister(string kitName) => mHandlers.Remove(kitName);

        /// <summary>
        /// 构建当前命令桥已注册 Kit/action 目录，供 Tauri 下拉框按真实宿主能力展示。
        /// </summary>
        public string BuildCommandCatalogJson()
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"kits\":[");
            var firstKit = true;
            foreach (var pair in mHandlers)
            {
                var handler = pair.Value;
                if (handler == null)
                    continue;

                var kitName = string.IsNullOrEmpty(handler.KitName) ? pair.Key : handler.KitName;
                if (!CommandBridgeProtocol.IsSafeIdentifier(kitName))
                    continue;

                if (!firstKit)
                    sb.Append(',');

                firstKit = false;
                sb.Append("{\"kit\":\"");
                sb.Append(JsonHelper.EscapeString(kitName));
                sb.Append("\",\"actions\":[");

                var actions = handler.SupportedActions;
                var firstAction = true;
                if (actions != null)
                {
                    for (var i = 0; i < actions.Length; i++)
                    {
                        var action = actions[i];
                        if (!CommandBridgeProtocol.IsSafeIdentifier(action))
                            continue;

                        if (!firstAction)
                            sb.Append(',');

                        firstAction = false;
                        sb.Append("{\"action\":\"");
                        sb.Append(JsonHelper.EscapeString(action));
                        sb.Append("\"}");
                    }
                }

                sb.Append("]}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 分发命令 JSON 并返回响应 JSON。
        /// 未知 kit 或 action 会转成标准错误响应，避免调用方无限等待。
        /// </summary>
        public string Dispatch(string commandJson)
        {
            // 提取路由字段。
            var kit = JsonHelper.ExtractString(commandJson, "kit");
            var action = JsonHelper.ExtractString(commandJson, "action");
            var requestId = JsonHelper.ExtractString(commandJson, "requestId") ?? string.Empty;
            var source = JsonHelper.ExtractString(commandJson, "source") ?? string.Empty;
            var engineId = ResolveEngineId(commandJson);

            if (string.IsNullOrEmpty(kit))
                return JsonHelper.BuildError(requestId, "System", "dispatch", "Missing 'kit' field in command JSON", engineId, "InvalidCommand", false);

            if (string.IsNullOrEmpty(action))
                return JsonHelper.BuildError(requestId, kit, "dispatch", "Missing 'action' field in command JSON", engineId, "InvalidCommand", false);

            if (!string.IsNullOrEmpty(source) && !CommandBridgeProtocol.IsSafeIdentifier(source))
                return JsonHelper.BuildError(requestId, kit, action, "Invalid source identifier '" + source + "'", engineId, "InvalidSource", false);

            if (!CommandBridgeProtocol.IsSafeIdentifier(kit))
                return JsonHelper.BuildError(requestId, kit, action, "Invalid kit identifier '" + kit + "'", engineId, "InvalidKit", false);

            if (!CommandBridgeProtocol.IsSafeIdentifier(action))
                return JsonHelper.BuildError(requestId, kit, action, "Invalid action identifier '" + action + "'", engineId, "InvalidAction", false);

            var payloadJson = JsonHelper.ExtractRaw(commandJson, "payload") ?? "{}";

            // 解析命令 envelope 字段（protocolVersion/createdAtUtc/timeoutMs）。
            // 缺字段时使用默认值，兼容旧命令：
            // - protocolVersion 缺省为 null
            // - createdAtUtc 缺省为 DateTime.MinValue（IsExpired 永远为 false）
            // - timeoutMs 缺省为 0（无超时，DeadlineUtc = DateTime.MaxValue）
            var protocolVersion = JsonHelper.ExtractString(commandJson, "protocolVersion");
            var createdAtStr = JsonHelper.ExtractString(commandJson, "createdAtUtc");
            DateTime createdAtUtc = DateTime.MinValue;
            if (!string.IsNullOrEmpty(createdAtStr)
                && DateTime.TryParse(createdAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                createdAtUtc = parsed;
            int timeoutMs = 0;
            JsonHelper.TryExtractInt(commandJson, "timeoutMs", out timeoutMs);

            var command = new CommandBridgeCommand(requestId, engineId, source, kit, action, payloadJson,
                protocolVersion, createdAtUtc, timeoutMs);

            // 过期检查在 Policy 之前：过期命令不调用 Policy 链，直接返回 CommandExpired 错误。
            if (command.IsExpired(DateTime.UtcNow))
                return JsonHelper.BuildError(requestId, kit, action,
                    $"Command expired: deadline {command.DeadlineUtc:O} has passed",
                    engineId, "CommandExpired", false);

            // 组合链：任一拒绝即拒绝；policy 返回 null 视为 Deny（与原 CommandPolicy 语义一致，不静默放行）
            CommandBridgePolicyResult policyResult;
            if (mPolicies.Count == 0)
            {
                policyResult = CommandBridgePolicyResult.Allow();  // 无策略默认 Allow
            }
            else
            {
                policyResult = null;
                foreach (var policy in mPolicies)
                {
                    policyResult = policy(command);
                    if (policyResult == null || !policyResult.Allowed)
                        break;  // null 或 Deny 都停止链；后注册策略不能重新放行
                }
                // policy 返回 null 视为 Deny（与原 CommandPolicy 处理一致），不静默放行
                if (policyResult == null)
                    policyResult = CommandBridgePolicyResult.Deny("PolicyDenied", "Command policy returned null");
            }
            if (!policyResult.Allowed)
            {
                return JsonHelper.BuildError(requestId, kit, action, policyResult.Message, engineId, policyResult.ErrorCode, policyResult.Recoverable);
            }

            if (kit == "System" && action == "list_commands")
                return JsonHelper.BuildResponse(requestId, kit, action, "success", BuildCommandCatalogJson(), engineId);

            // 路由到已注册的处理器（包含 System）。
            if (!mHandlers.TryGetValue(kit, out var handler))
                return JsonHelper.BuildError(requestId, kit, action, $"No handler registered for kit '{kit}'", engineId, "UnknownKit", false);

            try
            {
                var resultData = handler.HandleAction(action, payloadJson);
                return JsonHelper.BuildResponse(requestId, kit, action, "success", resultData, engineId);
            }
            catch (Exception ex)
            {
                return JsonHelper.BuildError(requestId, kit, action, ex.Message, engineId, "HandlerException", false);
            }
        }

        private string ResolveEngineId(string commandJson)
        {
            var engineId = JsonHelper.ExtractString(commandJson, "engineId");
            return string.IsNullOrEmpty(engineId) ? DefaultEngineId : engineId;
        }

        /// <summary>
        /// RegisterPolicy 返回的注销 token；dispose 后从组合链移除对应策略。
        /// </summary>
        private sealed class PolicyToken : IDisposable
        {
            private readonly KitCommandDispatcher mDispatcher;
            private readonly Func<CommandBridgeCommand, CommandBridgePolicyResult> mPolicy;
            private bool mDisposed;

            public PolicyToken(KitCommandDispatcher dispatcher, Func<CommandBridgeCommand, CommandBridgePolicyResult> policy)
            {
                mDispatcher = dispatcher;
                mPolicy = policy;
            }

            public void Dispose()
            {
                if (mDisposed) return;
                mDisposed = true;
                mDispatcher.mPolicies.Remove(mPolicy);
            }
        }
    }
}
