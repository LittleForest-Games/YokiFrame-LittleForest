using System;
using System.Collections.Generic;

namespace YokiFrame
{
    /// <summary>
    /// <see cref="ICommandBridgeExtensionContext"/> 的默认实现。
    /// 放在 YokiFrame asmdef（非 Editor），无 Unity Editor 依赖；
    /// public 可见性让测试程序集可直接构造，无需 InternalsVisibleTo 暴露 Editor internals。
    /// Host 加载扩展时构造此 context，扩展通过它注册 handler/policy/snapshot；
    /// 所有 token 收集到 <see cref="Tokens"/>，Host 卸载时统一 dispose。
    /// </summary>
    public sealed class CommandBridgeExtensionContext : ICommandBridgeExtensionContext
    {
        private readonly KitCommandDispatcher mDispatcher;

        /// <summary>
        /// 扩展通过 RegisterPolicy/RegisterSnapshot 返回的所有 token；
        /// Host 卸载时遍历 dispose，实现扩展生命周期与 Domain Reload 同步。
        /// </summary>
        public List<IDisposable> Tokens { get; } = new();

        /// <param name="dispatcher">目标分发器；null 抛 ArgumentNullException。</param>
        public CommandBridgeExtensionContext(KitCommandDispatcher dispatcher)
        {
            mDispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <inheritdoc/>
        public void RegisterHandler(IKitCommandHandler handler) => mDispatcher.Register(handler);

        /// <inheritdoc/>
        public IDisposable RegisterPolicy(Func<CommandBridgeCommand, CommandBridgePolicyResult> policy)
        {
            var token = mDispatcher.RegisterPolicy(policy);
            Tokens.Add(token);
            return token;
        }

        /// <inheritdoc/>
        public IDisposable RegisterSnapshot(IKitSnapshotPublisher publisher)
        {
            var token = mDispatcher.RegisterSnapshot(publisher);
            Tokens.Add(token);
            return token;
        }
    }
}
