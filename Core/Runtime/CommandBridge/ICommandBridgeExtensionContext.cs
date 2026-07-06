using System;

namespace YokiFrame
{
    /// <summary>
    /// 扩展注册上下文接口。Host 加载扩展时传入，扩展通过此 context 注册 handler/policy/snapshot。
    /// </summary>
    public interface ICommandBridgeExtensionContext
    {
        /// <summary>
        /// 注册 Kit 命令处理器。重复 KitName 抛 InvalidOperationException（与 KitCommandDispatcher.Register 一致）。
        /// </summary>
        void RegisterHandler(IKitCommandHandler handler);

        /// <summary>
        /// 注册命令策略到组合链，返回 token 用于注销。
        /// 组合链语义：任一策略拒绝即拒绝；策略返回 null 视为 Deny（错误码 PolicyDenied）。
        /// </summary>
        IDisposable RegisterPolicy(Func<CommandBridgeCommand, CommandBridgePolicyResult> policy);

        /// <summary>
        /// 注册 Kit snapshot 发布器，返回 token 用于注销。
        /// Host 在统一节奏下通过 PublishAllSnapshots 调用所有已注册 publisher。
        /// </summary>
        IDisposable RegisterSnapshot(IKitSnapshotPublisher publisher);
    }
}
