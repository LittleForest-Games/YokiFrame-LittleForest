#if UNITY_EDITOR && !GODOT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace YokiFrame.Unity
{
    /// <summary>
    /// Little Forest fork 的唯一 Unity 控制面 Host。
    /// Local IPC v1 是 command/session/liveness owner；不创建 FileBridge
    /// heartbeat、command/result mailbox、interest 文件或 IPC fallback。
    /// </summary>
    [InitializeOnLoad]
    internal static class YokiFrameLittleForestControlPlaneHost
    {
        private const string EngineId = "unity-editor";
        private const string CommandTransport = "local-ipc-v1";

        private static readonly List<IYokiFrameCommandBridgeExtension> sLoadedExtensions =
            new List<IYokiFrameCommandBridgeExtension>();
        private static readonly List<IDisposable> sExtensionTokens =
            new List<IDisposable>();

        private static readonly KitCommandDispatcher sDispatcher;
        private static readonly CommandBridgeConnectionRegistry sConnectionRegistry;
        private static readonly string sProjectRoot;
        private static Win32NamedPipeHost sLocalIpcHost;
        private static UnityEditorMainThreadScheduler sMainThreadScheduler;
        private static string sInitializationError = string.Empty;

        static YokiFrameLittleForestControlPlaneHost()
        {
            sProjectRoot = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
            sDispatcher = new KitCommandDispatcher
            {
                DefaultEngineId = EngineId
            };
            sConnectionRegistry = new CommandBridgeConnectionRegistry();

            sDispatcher.Register(
                new YokiFrameLittleForestSystemCommandHandler(
                    () => BuildStatusJson(),
                    () => sDispatcher.BuildCommandCatalogJson()));
            LoadExtensions();
            StartTransport();

            AssemblyReloadEvents.beforeAssemblyReload += DisposeTransport;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeExtensions;
            AssemblyReloadEvents.beforeAssemblyReload += AdapterSharedMemoryTelemetry.ResetForTests;
            EditorApplication.quitting += DisposeTransport;
            EditorApplication.quitting += DisposeExtensions;
            EditorApplication.quitting += AdapterSharedMemoryTelemetry.ResetForTests;
        }

        private static void StartTransport()
        {
            try
            {
                var scheduler = new UnityEditorMainThreadScheduler();
                var host = new Win32NamedPipeHost(
                    sProjectRoot,
                    EngineId,
                    sDispatcher,
                    sConnectionRegistry,
                    scheduler.Post,
                    message => Debug.LogWarning("[YokiFrame] " + message));
                sMainThreadScheduler = scheduler;
                sLocalIpcHost = host;
                host.Start();
                sInitializationError = string.Empty;
            }
            catch (Exception exception)
            {
                sInitializationError = exception.Message;
                sLocalIpcHost?.Dispose();
                sLocalIpcHost = null;
                sMainThreadScheduler?.Dispose();
                sMainThreadScheduler = null;
                Debug.LogError(
                    "[YokiFrame] Local IPC initialization failed; FileBridge fallback is disabled: "
                    + exception.Message);
            }
        }

        private static void DisposeTransport()
        {
            var host = sLocalIpcHost;
            sLocalIpcHost = null;
            host?.Dispose();

            var scheduler = sMainThreadScheduler;
            sMainThreadScheduler = null;
            scheduler?.Dispose();
        }

        private static string BuildStatusJson()
        {
            var host = sLocalIpcHost;
            if (host != null)
            {
                return host.BuildStatusJson();
            }

            return "{\"available\":false,\"transport\":\""
                   + CommandTransport
                   + "\",\"error\":\""
                   + JsonHelper.EscapeString(sInitializationError)
                   + "\"}";
        }

        private static void LoadExtensions()
        {
            IEnumerable<Type> extensionTypes = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(SafeGetTypes)
                .Where(type =>
                    typeof(IYokiFrameCommandBridgeExtension).IsAssignableFrom(type)
                    && type.GetCustomAttribute<YokiFrameCommandBridgeExtensionAttribute>() != null
                    && !type.IsAbstract
                    && !type.IsInterface);

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type type in extensionTypes)
            {
                YokiFrameCommandBridgeExtensionAttribute attribute =
                    type.GetCustomAttribute<YokiFrameCommandBridgeExtensionAttribute>();
                if (attribute == null || !seenIds.Add(attribute.ExtensionId))
                {
                    Debug.LogWarning(
                        "[YokiFrame] Duplicate control-plane ExtensionId ignored: "
                        + (attribute?.ExtensionId ?? type.FullName));
                    continue;
                }

                try
                {
                    var extension = (IYokiFrameCommandBridgeExtension)
                        Activator.CreateInstance(type, true);
                    var context = new CommandBridgeExtensionContext(
                        sDispatcher,
                        sConnectionRegistry,
                        CommandTransport);
                    extension.Register(context);
                    sLoadedExtensions.Add(extension);
                    sExtensionTokens.AddRange(context.Tokens);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "[YokiFrame] Control-plane extension failed to load: "
                        + type.FullName
                        + ": "
                        + exception.Message);
                }
            }
        }

        private static void DisposeExtensions()
        {
            for (int index = 0; index < sExtensionTokens.Count; index++)
            {
                try
                {
                    sExtensionTokens[index]?.Dispose();
                }
                catch
                {
                    // Extension cleanup is isolated; transport shutdown continues.
                }
            }

            sExtensionTokens.Clear();
            sLoadedExtensions.Clear();
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
            catch
            {
                return Type.EmptyTypes;
            }
        }
    }
}
#endif
