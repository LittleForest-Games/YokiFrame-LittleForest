using System;

namespace YokiFrame
{
    /// <summary>
    /// 标记实现 <see cref="IYokiFrameCommandBridgeExtension"/> 的类型，使其被 Host 自动发现。
    /// Host 初始化时扫描所有程序集中带此属性的类型，实例化并调用 Register。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class YokiFrameCommandBridgeExtensionAttribute : Attribute
    {
        /// <summary>
        /// 扩展唯一标识符。Host 加载时去重，重复 ExtensionId 的扩展会被跳过并记录警告。
        /// </summary>
        public string ExtensionId { get; }

        /// <param name="extensionId">扩展唯一标识符；null 抛 ArgumentNullException。</param>
        public YokiFrameCommandBridgeExtensionAttribute(string extensionId)
        {
            ExtensionId = extensionId ?? throw new ArgumentNullException(nameof(extensionId));
        }
    }
}
