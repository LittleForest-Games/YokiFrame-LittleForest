namespace YokiFrame
{
    /// <summary>
    /// Workbench 声明式 Kit descriptor 的 manifest 提供器接口（YF1b 预留，方法暂不实现）。
    /// 扩展实现此接口并向 Host 注册，由 Host 在 WriteEngineRegistry 时扫描并写入 engine registry，
    /// Workbench 前端通过 System/get_engine_registry 读取并渲染白名单组件。
    /// </summary>
    /// <remarks>
    /// 当前状态：仅接口定义。实际加载机制等取得可构建 Tauri 前端源码后再实现（阻塞 YF4，不阻塞 YF2/YF3）。
    /// schema 详见 fork 内 <c>Docs/workbench-manifest-schema.md</c>。
    /// </remarks>
    public interface IKitManifestProvider
    {
        /// <summary>
        /// 扩展唯一标识符；必须与 [YokiFrameCommandBridgeExtension] 属性值一致。
        /// </summary>
        string ExtensionId { get; }

        /// <summary>
        /// 构建 manifest JSON 字符串（符合 Docs/workbench-manifest-schema.md 定义的 schema）。
        /// Host 在 WriteEngineRegistry 时调用，将结果写入 engine registry 的 extensions 字段。
        /// </summary>
        /// <returns>manifest JSON 字符串。</returns>
        string BuildManifestJson();
    }
}
