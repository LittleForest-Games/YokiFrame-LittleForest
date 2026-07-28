namespace YokiFrame
{
    /// <summary>
    /// Kit snapshot 发布器接口。实现此接口并注册到 <see cref="KitCommandDispatcher"/>，
    /// 由 Host 在统一节奏下调用 <see cref="TryPublish"/>。
    /// </summary>
    /// <remarks>
    /// 现有内置 Kit 的静态 publisher（如 <c>FsmKitSnapshotPublisher</c>、<c>KitStateSnapshotPublisher</c>）
    /// 不实现此接口；本接口仅供通过扩展 API 注册的 publisher 使用。
    /// 异常隔离由调用方（<see cref="KitCommandDispatcher.PublishAllSnapshots"/>）负责；
    /// 实现方不应抛异常，但即使抛出也会被捕获并记录警告，不影响其他 publisher。
    /// </remarks>
    public interface IKitSnapshotPublisher
    {
        /// <summary>
        /// 发布器所属的引擎标识符。
        /// </summary>
        string EngineId { get; }

        /// <summary>
        /// 发布器对应的 Kit 名称。
        /// </summary>
        string KitName { get; }

        /// <summary>
        /// Snapshot 名称（如 "state"、"metrics"）。
        /// </summary>
        string SnapshotName { get; }

        /// <summary>
        /// 尝试发布 snapshot。
        /// 异常隔离由调用方负责；实现方不应抛异常。
        /// </summary>
        /// <param name="yokiframeRoot">.yokiframe 根目录绝对路径。</param>
        /// <returns>写入的 snapshot 文件路径；失败返回 null。</returns>
        string TryPublish(string yokiframeRoot);
    }
}
