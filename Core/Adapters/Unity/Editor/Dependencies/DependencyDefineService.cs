#if UNITY_EDITOR

using System;
using UnityEditor;
using UnityEngine;

namespace YokiFrame.Unity
{
    /// <summary>
    /// 显式把七组可选依赖同步为当前构建目标的 YokiFrame 宏。
    /// </summary>
    public static class DependencyDefineService
    {
        /// <summary>
        /// UniTask 可选依赖编译宏。
        /// </summary>
        public const string UNITASK_SUPPORT_DEFINE = DependencyDefineCatalog.UNITASK_SUPPORT_DEFINE;

        /// <summary>
        /// YooAsset 可选依赖编译宏。
        /// </summary>
        public const string YOOASSET_SUPPORT_DEFINE = DependencyDefineCatalog.YOOASSET_SUPPORT_DEFINE;

        /// <summary>
        /// Luban 可选依赖编译宏。
        /// </summary>
        public const string LUBAN_SUPPORT_DEFINE = DependencyDefineCatalog.LUBAN_SUPPORT_DEFINE;

        /// <summary>
        /// ZString 可选依赖编译宏。
        /// </summary>
        public const string ZSTRING_SUPPORT_DEFINE = DependencyDefineCatalog.ZSTRING_SUPPORT_DEFINE;

        /// <summary>
        /// DOTween 可选依赖编译宏。
        /// </summary>
        public const string DOTWEEN_SUPPORT_DEFINE = DependencyDefineCatalog.DOTWEEN_SUPPORT_DEFINE;

        /// <summary>
        /// Nino 可选依赖编译宏。
        /// </summary>
        public const string NINO_SUPPORT_DEFINE = DependencyDefineCatalog.NINO_SUPPORT_DEFINE;

        /// <summary>
        /// Unity Input System 可选依赖编译宏。
        /// </summary>
        public const string INPUT_SYSTEM_SUPPORT_DEFINE = DependencyDefineCatalog.INPUT_SYSTEM_SUPPORT_DEFINE;

        private static readonly UnityDependencyInventoryProvider sInventoryProvider = new();
        private static readonly UnityDependencyDefineStore sDefineStore = new();
        private static readonly DependencyDefineRefreshCoordinator sRefreshCoordinator = new(
            sInventoryProvider.Capture,
            sDefineStore.ReadSymbols,
            sDefineStore.WriteSymbols,
            GetActiveBuildTarget);

        private static bool sRefreshScheduled;

        /// <summary>
        /// 获取 UniTask 可选依赖编译宏。
        /// </summary>
        public static string UniTaskSupportDefine => UNITASK_SUPPORT_DEFINE;

        /// <summary>
        /// 获取 YooAsset 可选依赖编译宏。
        /// </summary>
        public static string YooAssetSupportDefine => YOOASSET_SUPPORT_DEFINE;

        /// <summary>
        /// 获取 Luban 可选依赖编译宏。
        /// </summary>
        public static string LubanSupportDefine => LUBAN_SUPPORT_DEFINE;

        /// <summary>
        /// 获取 ZString 可选依赖编译宏。
        /// </summary>
        public static string ZStringSupportDefine => ZSTRING_SUPPORT_DEFINE;

        /// <summary>
        /// 获取 DOTween 可选依赖编译宏。
        /// </summary>
        public static string DOTweenSupportDefine => DOTWEEN_SUPPORT_DEFINE;

        /// <summary>
        /// 获取 Nino 可选依赖编译宏。
        /// </summary>
        public static string NinoSupportDefine => NINO_SUPPORT_DEFINE;

        /// <summary>
        /// 获取 Unity Input System 可选依赖编译宏。
        /// </summary>
        public static string InputSystemSupportDefine => INPUT_SYSTEM_SUPPORT_DEFINE;

        /// <summary>
        /// 手动刷新当前 Unity 构建目标的 YokiFrame 可选依赖宏。
        /// </summary>
        [MenuItem("YokiFrame/Refresh Dependency Defines")]
        public static void RefreshDefines()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleRefresh();
                return;
            }

            ExecuteRefresh();
        }

        /// <summary>
        /// 将同一帧内的多次依赖变化合并为一次 Editor delayCall。
        /// </summary>
        private static void ScheduleRefresh()
        {
            if (sRefreshScheduled)
            {
                return;
            }

            sRefreshScheduled = true;
            EditorApplication.delayCall += RefreshWhenEditorIsReady;
        }

        /// <summary>
        /// 等待 Unity 完成编译或资源更新后再刷新，避免阻塞主线程和竞争 PlayerSettings。
        /// </summary>
        private static void RefreshWhenEditorIsReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += RefreshWhenEditorIsReady;
                return;
            }

            sRefreshScheduled = false;
            ExecuteRefresh();
        }

        /// <summary>
        /// 执行协调器并把失败或实际写入结果输出到 Unity Console。
        /// </summary>
        private static void ExecuteRefresh()
        {
            var result = sRefreshCoordinator.Refresh();
            if (!result.Succeeded)
            {
                Debug.LogError(
                    "[YokiFrame][DependencyDefineService][target="
                    + result.BuildTarget
                    + "] "
                    + result.ErrorMessage);
                return;
            }

            if (result.Changed)
            {
                Debug.Log(CreateRefreshSummary(result));
            }

            for (var index = 0; index < result.InventoryDiagnostics.Length; index++)
            {
                Debug.LogWarning(
                    "[YokiFrame][DependencyDefineService][target="
                    + result.BuildTarget
                    + "] "
                    + result.InventoryDiagnostics[index]);
            }
        }

        /// <summary>
        /// 读取当前 Unity 构建目标，用于把宏变化与实际 PlayerSettings 写入平台关联。
        /// </summary>
        /// <returns>Unity 当前 activeBuildTarget 的稳定名称。</returns>
        private static string GetActiveBuildTarget()
        {
            return EditorUserBuildSettings.activeBuildTarget.ToString();
        }

        /// <summary>
        /// 根据保留的宏规划和 inventory 生成单条可审计 Console 摘要。
        /// </summary>
        /// <param name="result">已经成功完成的刷新结果。</param>
        /// <returns>包含目标平台、宏差异和原始依赖证据的日志文本。</returns>
        private static string CreateRefreshSummary(DependencyDefineRefreshResult result)
        {
            var plan = result.Plan;
            return "[YokiFrame][DependencyDefineService][target="
                + result.BuildTarget
                + "] 依赖宏已刷新 +["
                + string.Join(", ", plan.AddedSymbols)
                + "] -["
                + string.Join(", ", plan.RemovedSymbols)
                + "] packages=["
                + string.Join(", ", result.Snapshot.PackageNames)
                + "] asmdefs=["
                + string.Join(", ", result.Snapshot.AssemblyDefinitionNames)
                + "] dlls=["
                + string.Join(", ", result.Snapshot.PrecompiledAssemblyNames)
                + "]";
        }

    }
}

#endif
