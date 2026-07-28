namespace YokiFrame
{
    /// <summary>
    /// CommandBridge 扩展接口。实现此接口并标记 [YokiFrameCommandBridgeExtension] 属性，
    /// Host 初始化时自动发现并调用 Register。
    /// </summary>
    public interface IYokiFrameCommandBridgeExtension
    {
        /// <summary>
        /// 扩展唯一标识符。Host 加载时去重，重复 ExtensionId 的扩展会被跳过并记录警告。
        /// </summary>
        string ExtensionId { get; }

        /// <summary>
        /// 注册扩展。Host 在初始化时调用，传入 <see cref="ICommandBridgeExtensionContext"/>；
        /// 扩展通过 context 的 RegisterHandler/RegisterPolicy/RegisterSnapshot 注册内容。
        /// 扩展可保存 context 引用以便后续动态注册（"Host 初始化后注册" 唯一路径）。
        /// </summary>
        /// <param name="context">扩展注册上下文。</param>
        void Register(ICommandBridgeExtensionContext context);
    }
}
