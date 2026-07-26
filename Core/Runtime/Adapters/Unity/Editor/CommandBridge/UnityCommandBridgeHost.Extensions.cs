#if !GODOT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YokiFrame.Unity
{
    /// <summary>
    /// Unity 命令桥驱动壳的扩展加载 partial。
    /// 仅放 LoadExtensions/DisposeExtensions/SafeGetTypes；context 实现类在 YokiFrame asmdef。
    /// </summary>
    internal static partial class UnityCommandBridgeHost
    {
        private static readonly List<IYokiFrameCommandBridgeExtension> sLoadedExtensions = new();
        private static readonly List<IDisposable> sExtensionTokens = new();

        /// <summary>
        /// 约定式扫描所有程序集中带 [YokiFrameCommandBridgeExtension] 属性的 IYokiFrameCommandBridgeExtension 类型，
        /// 实例化并调用 Register。重复 ExtensionId 跳过并记录警告；单个扩展加载失败不中断其他扩展。
        /// 不提供 public RegisterExtension 显式 API：Host 是 internal static partial class，
        /// 外部程序集无法访问其 public 成员；扩展如需初始化后动态注册，应在 Register(context) 中保存 context 引用。
        /// </summary>
        private static void LoadExtensions()
        {
            var extensionTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => SafeGetTypes(a))
                .Where(t => typeof(IYokiFrameCommandBridgeExtension).IsAssignableFrom(t)
                            && t.GetCustomAttribute<YokiFrameCommandBridgeExtensionAttribute>() != null
                            && !t.IsAbstract && !t.IsInterface);

            var seenIds = new HashSet<string>();
            foreach (var type in extensionTypes)
            {
                var attr = type.GetCustomAttribute<YokiFrameCommandBridgeExtensionAttribute>();
                if (attr == null || !seenIds.Add(attr.ExtensionId))
                {
                    LogKit.Warning("[YokiCommandBridge] 跳过重复 ExtensionId: " + (attr?.ExtensionId ?? type.FullName));
                    continue;
                }

                try
                {
                    var extension = (IYokiFrameCommandBridgeExtension)Activator.CreateInstance(type, true);
                    var context = new CommandBridgeExtensionContext(
                        Dispatcher,
                        sConnectionRegistry,
                        sCommandTransportMode);
                    extension.Register(context);
                    sLoadedExtensions.Add(extension);
                    sExtensionTokens.AddRange(context.Tokens);
                }
                catch (Exception e)
                {
                    LogKit.Warning("[YokiCommandBridge] 扩展 " + type.FullName + " 加载失败: " + e.Message);
                }
            }
        }

        /// <summary>
        /// 卸载所有扩展注册的 token（policy/snapshot），清空已加载扩展列表。
        /// 在 AssemblyReloadEvents.beforeAssemblyReload 和 EditorApplication.quitting 时调用。
        /// </summary>
        private static void DisposeExtensions()
        {
            foreach (var token in sExtensionTokens)
            {
                try { token?.Dispose(); } catch { /* 忽略 dispose 异常 */ }
            }
            sExtensionTokens.Clear();
            sLoadedExtensions.Clear();
        }

        /// <summary>
        /// 安全获取程序集的所有类型；GetTypes 抛 ReflectionTypeLoadException 时返回空数组（兜底）。
        /// </summary>
        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch { return Type.EmptyTypes; }
        }
    }
}
#endif
