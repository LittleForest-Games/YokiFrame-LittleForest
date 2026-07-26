#if !GODOT
using System;
using System.IO;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace YokiFrame.Unity
{
    /// <summary>
    /// Unity 命令桥驱动壳（仅编辑器）。
    /// Local IPC v1 是唯一正式 command/liveness transport。
    /// </summary>
    [InitializeOnLoad]
    internal static partial class UnityCommandBridgeHost
    {
        private const int KIT_SNAPSHOT_INTERVAL_MS = 1000;
        private const string ENGINE_ID = "unity-editor";
        private const string BRIDGE_UNAVAILABLE_JSON =
            "{\"available\":false,\"reason\":\"host is not initialized\"}";
        private const string BUILTIN_KIT_INTEGRATION_PREF_KEY =
            "YokiFrame.CommandBridge.EnableBuiltinKitIntegration";
        private const string LOCAL_IPC_TRANSPORT_MODE = "local-ipc-v1";
        private const string SAVEKIT_COMMAND_HANDLER_TYPE =
            "YokiFrame.SaveKitCommandHandler, YokiFrame.SaveKit";
        private const string LOCALIZATIONKIT_COMMAND_HANDLER_TYPE =
            "YokiFrame.LocalizationKitCommandHandler, YokiFrame.LocalizationKit";
        private const string SCENEKIT_COMMAND_HANDLER_TYPE =
            "YokiFrame.SceneKitCommandHandler, YokiFrame.SceneKit";
        private const string SPATIALKIT_COMMAND_HANDLER_TYPE =
            "YokiFrame.SpatialKitCommandHandler, YokiFrame.SpatialKit";
        private const string UIKIT_COMMAND_HANDLER_TYPE =
            "YokiFrame.UnityUIKitCommandHandler, YokiFrame.UIKit.Editor";
        private const string ACTIONKIT_COMMAND_HANDLER_TYPE =
            "YokiFrame.ActionKitCommandHandler, YokiFrame.ActionKit";

        private static readonly ProfilerMarker sPublishSnapshotsProfilerMarker =
            new ProfilerMarker(
                "YokiFrame.CommandBridge.PublishAllSnapshots");

        private static Win32NamedPipeHost sLocalIpcHost;
        private static UnityEditorMainThreadScheduler sMainThreadScheduler;
        private static CommandBridgeConnectionRegistry sConnectionRegistry;
        private static string sYokiframeRoot;
        private static string sProjectRoot;
        private static readonly string sCommandTransportMode =
            LOCAL_IPC_TRANSPORT_MODE;
        private static string sTransportInitializationError;
        private static DateTime? sLastKitSnapshotPublishUtc;
        private static bool sSnapshotPublishingActive;

        /// <summary>
        /// engine-scoped Local IPC Host 使用的共享命令分发器。
        /// </summary>
        public static KitCommandDispatcher Dispatcher { get; private set; }

        static UnityCommandBridgeHost()
        {
            sProjectRoot =
                Path.GetDirectoryName(Application.dataPath);
            sYokiframeRoot =
                Path.Combine(sProjectRoot, ".yokiframe");

            Dispatcher = new KitCommandDispatcher
            {
                DefaultEngineId = ENGINE_ID
            };
            sConnectionRegistry =
                new CommandBridgeConnectionRegistry();
            EnsureDefaultLogger();

            if (BuiltinKitIntegrationEnabled)
            {
                EnsureRuntimeSettingsStore();
                EnsureDefaultResourceProvider();
                KitStateSnapshotPublisher
                    .RestoreAndPublishPoolMonitorPreferences(
                        sYokiframeRoot);
            }

            RegisterCommandHandlers();
            LoadExtensions();
            StartLocalIpcTransport();
            StartSnapshotPublishingIfRequired();

            AssemblyReloadEvents.beforeAssemblyReload +=
                DisposeCommandTransport;
            AssemblyReloadEvents.beforeAssemblyReload +=
                DisposeSnapshotPublishing;
            AssemblyReloadEvents.beforeAssemblyReload +=
                DisposeExtensions;
            EditorApplication.quitting +=
                DisposeCommandTransport;
            EditorApplication.quitting +=
                DisposeSnapshotPublishing;
            EditorApplication.quitting +=
                DisposeExtensions;

            WriteEngineRegistry();
        }

        private static void RegisterCommandHandlers()
        {
            Dispatcher.Register(
                new SystemCommandHandler(
                    () => BuildActiveBridgeStatusJson(false),
                    OpenCodeLocationWithDefaultEditor,
                    () => Dispatcher != null
                        ? Dispatcher.BuildCommandCatalogJson()
                        : BRIDGE_UNAVAILABLE_JSON,
                    () => BuildActiveBridgeStatusJson(true)));

            if (!BuiltinKitIntegrationEnabled)
                return;

            Dispatcher.Register(new FsmKitCommandHandler());
            Dispatcher.Register(new UnityPoolKitCommandHandler());
            Dispatcher.Register(new UnityLogKitCommandHandler());
            Dispatcher.Register(new ResKitCommandHandler());
            Dispatcher.Register(new EventKitCommandHandler());
            Dispatcher.Register(new SingletonKitCommandHandler());
            Dispatcher.Register(new ArchitectureCommandHandler());
            UnityManagedRuntimeBackendRegistration.EnsureRegistered();
            Dispatcher.Register(
                new ManagedRuntimeKitCommandHandler());
            Dispatcher.Register(new AudioKitCommandHandler());
            RegisterOptionalToolCommandHandlers();
        }

        private static void RegisterOptionalToolCommandHandlers()
        {
            OptionalKitCommandHandlerRegistry.TryRegister(
                Dispatcher,
                SAVEKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(
                Dispatcher,
                LOCALIZATIONKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(
                Dispatcher,
                SCENEKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(
                Dispatcher,
                SPATIALKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(
                Dispatcher,
                UIKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(
                Dispatcher,
                ACTIONKIT_COMMAND_HANDLER_TYPE);
        }

        private static void EnsureDefaultResourceProvider()
        {
            if (ResKit.GetProvider() != default)
                return;

            ResKit.SetProvider(new UnityResourceProvider());
        }

        private static void EnsureDefaultLogger()
        {
            if (LogKit.HasLogger)
                return;

            LogKit.SetLogger(new UnityEngineLogger());
        }

        private static void EnsureRuntimeSettingsStore()
        {
            UnityRuntimeSettingsBridge.EnsureInstalled();
            UnityLogKitRuntimeInstaller.Install(
                UnityRuntimeSettingsBridge.GetLogKitOptions(
                    UnityLogKitOptions.CreateDefault()),
                LogKit.GetLogger());
        }

        private static void StartSnapshotPublishingIfRequired()
        {
            if (!BuiltinKitIntegrationEnabled &&
                (Dispatcher == null ||
                 Dispatcher.SnapshotPublishers.Count == 0))
            {
                return;
            }

            EditorApplication.update += OnEditorUpdate;
            sSnapshotPublishingActive = true;
        }

        private static void DisposeSnapshotPublishing()
        {
            if (!sSnapshotPublishingActive)
                return;

            EditorApplication.update -= OnEditorUpdate;
            sSnapshotPublishingActive = false;
            sLastKitSnapshotPublishUtc = null;
        }

        private static void OnEditorUpdate()
        {
            EnsureDefaultLogger();
            if (BuiltinKitIntegrationEnabled)
            {
                UnityManagedRuntimeBackendRegistration
                    .EnsureRegistered();
            }

            var nowUtc = DateTime.UtcNow;
            if (!sLastKitSnapshotPublishUtc.HasValue ||
                nowUtc - sLastKitSnapshotPublishUtc.Value >=
                TimeSpan.FromMilliseconds(
                    KIT_SNAPSHOT_INTERVAL_MS))
            {
                PublishSnapshotsProfiled();
                sLastKitSnapshotPublishUtc = nowUtc;
            }
        }

        private static void PublishSnapshotsProfiled()
        {
            using (sPublishSnapshotsProfilerMarker.Auto())
            {
                if (BuiltinKitIntegrationEnabled)
                {
                    KitStateSnapshotPublisher.TryPublishAll(
                        sYokiframeRoot);
                }

                Dispatcher?.PublishAllSnapshots(sYokiframeRoot);
            }
        }

        public static void PollNow()
        {
            LogKit.Info(
                "[YokiCommandBridge] Local IPC 使用连接驱动的有界主线程 drain，无文件命令轮询");
        }

        private static bool BuiltinKitIntegrationEnabled =>
            EditorPrefs.GetBool(
                BUILTIN_KIT_INTEGRATION_PREF_KEY,
                false);

        private static void StartLocalIpcTransport()
        {
            try
            {
                var scheduler =
                    new UnityEditorMainThreadScheduler();
                var host = new Win32NamedPipeHost(
                    sProjectRoot,
                    ENGINE_ID,
                    Dispatcher,
                    sConnectionRegistry,
                    scheduler.Post,
                    message => LogKit.Warning(
                        "[YokiCommandBridge] " + message));
                sMainThreadScheduler = scheduler;
                sLocalIpcHost = host;
                host.Start();
            }
            catch (Exception exception)
            {
                sTransportInitializationError =
                    exception.Message;
                sLocalIpcHost?.Dispose();
                sLocalIpcHost = null;
                sMainThreadScheduler?.Dispose();
                sMainThreadScheduler = null;
                LogKit.Error(
                    "[YokiCommandBridge] Local IPC 初始化失败；不会自动回退 FileBridge: " +
                    exception.Message);
            }
        }

        private static void DisposeCommandTransport()
        {
            var host = sLocalIpcHost;
            sLocalIpcHost = null;
            host?.Dispose();

            var scheduler = sMainThreadScheduler;
            sMainThreadScheduler = null;
            scheduler?.Dispose();
        }
    }
}
#endif
