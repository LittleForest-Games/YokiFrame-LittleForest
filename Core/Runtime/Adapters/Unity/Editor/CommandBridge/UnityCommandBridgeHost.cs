#if !GODOT
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;

namespace YokiFrame.Unity
{
    /// <summary>
    /// Unity 命令桥驱动壳（仅编辑器）。
    /// </summary>
    [InitializeOnLoad]
    internal static partial class UnityCommandBridgeHost
    {
        private const int HEARTBEAT_INTERVAL_MS = 2000;
        private const int POLL_MIN_INTERVAL_MS = 100;
        private const int POLL_MAX_INTERVAL_MS = 1000;
        private const int POLL_WATCHDOG_INTERVAL_MS = 30000;
        private const int KIT_SNAPSHOT_INTERVAL_MS = 1000;
        private const string ENGINE_ID = "unity-editor";
        private const string BRIDGE_UNAVAILABLE_JSON = "{\"available\":false,\"reason\":\"core is not initialized\"}";
        private const string BUILTIN_KIT_INTEGRATION_PREF_KEY = "YokiFrame.CommandBridge.EnableBuiltinKitIntegration";
        private const string SAVEKIT_COMMAND_HANDLER_TYPE = "YokiFrame.SaveKitCommandHandler, YokiFrame.SaveKit";
        private const string LOCALIZATIONKIT_COMMAND_HANDLER_TYPE = "YokiFrame.LocalizationKitCommandHandler, YokiFrame.LocalizationKit";
        private const string SCENEKIT_COMMAND_HANDLER_TYPE = "YokiFrame.SceneKitCommandHandler, YokiFrame.SceneKit";
        private const string SPATIALKIT_COMMAND_HANDLER_TYPE = "YokiFrame.SpatialKitCommandHandler, YokiFrame.SpatialKit";
        private const string UIKIT_COMMAND_HANDLER_TYPE = "YokiFrame.UnityUIKitCommandHandler, YokiFrame.UIKit.Editor";
        private const string ACTIONKIT_COMMAND_HANDLER_TYPE = "YokiFrame.ActionKitCommandHandler, YokiFrame.ActionKit";

        private static readonly ProfilerMarker sPollCoresProfilerMarker =
            new ProfilerMarker("YokiFrame.CommandBridge.PollCores");
        private static readonly ProfilerMarker sPublishSnapshotsProfilerMarker =
            new ProfilerMarker("YokiFrame.CommandBridge.PublishAllSnapshots");
        private static readonly ProfilerMarker sWriteHeartbeatProfilerMarker =
            new ProfilerMarker("YokiFrame.CommandBridge.WriteHeartbeat");
        private static readonly ProfilerMarker sWriteHeartbeatWorkerProfilerMarker =
            new ProfilerMarker("YokiFrame.CommandBridge.WriteHeartbeatWorker");

        private static YokiCommandBridgeCore sEngineCore;
        private static string sYokiframeRoot;
        private static DateTime sLastHeartbeat;
        private static DateTime? sLastPollUtc;
        private static DateTime? sLastKitSnapshotPublishUtc;
        private static readonly string sStartedAtUtc = DateTime.UtcNow.ToString("O");
        private static readonly CommandBridgePollBackoff sPollBackoff =
            new CommandBridgePollBackoff(POLL_MIN_INTERVAL_MS, POLL_MAX_INTERVAL_MS);
        private static FileSystemWatcher sCommandDirectoryWatcher;
        private static volatile bool sCommandDirectoryChanged;
        private static bool sCommandBridgeNeedsFollowUpPoll;

        /// <summary>
        /// engine-scoped 文件桥使用的共享命令分发器。
        /// </summary>
        public static KitCommandDispatcher Dispatcher { get; private set; }

        static UnityCommandBridgeHost()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            sYokiframeRoot = Path.Combine(projectRoot, ".yokiframe");

            Dispatcher = new KitCommandDispatcher();
            EnsureDefaultLogger();
            sEngineCore = CreateEngineCore(sYokiframeRoot, Dispatcher);

            if (BuiltinKitIntegrationEnabled)
            {
                EnsureRuntimeSettingsStore();
                EnsureDefaultResourceProvider();
                KitStateSnapshotPublisher.RestoreAndPublishPoolMonitorPreferences(sYokiframeRoot);
            }

            RegisterCommandHandlers();
            LoadExtensions();
            ResetCommandDirectoryWatcher();

            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeExtensions;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeCommandDirectoryWatcher;
            AssemblyReloadEvents.beforeAssemblyReload += StopHeartbeatWriter;
            EditorApplication.quitting += DisposeCommandDirectoryWatcher;
            EditorApplication.quitting += StopHeartbeatWriter;
            EditorApplication.quitting += DisposeExtensions;

            WriteEngineRegistry();
            PollCommandBridgeAndRecord(DateTime.UtcNow);
        }

        private static void RegisterCommandHandlers()
        {
            Dispatcher.Register(new SystemCommandHandler(
                () => BuildBridgeStatusJson(sEngineCore),
                OpenCodeLocationWithDefaultEditor,
                () => Dispatcher != null ? Dispatcher.BuildCommandCatalogJson() : BRIDGE_UNAVAILABLE_JSON,
                () => BuildBridgeStatusDetailJson(sEngineCore)));

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
            Dispatcher.Register(new ManagedRuntimeKitCommandHandler());
            Dispatcher.Register(new AudioKitCommandHandler());
            RegisterOptionalToolCommandHandlers();
        }

        private static void RegisterOptionalToolCommandHandlers()
        {
            // Tools Kit 可以独立安装，命令桥只按类型名尝试挂载，避免 Editor Adapter 反向硬依赖每个 Kit。
            OptionalKitCommandHandlerRegistry.TryRegister(Dispatcher, SAVEKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(Dispatcher, LOCALIZATIONKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(Dispatcher, SCENEKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(Dispatcher, SPATIALKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(Dispatcher, UIKIT_COMMAND_HANDLER_TYPE);
            OptionalKitCommandHandlerRegistry.TryRegister(Dispatcher, ACTIONKIT_COMMAND_HANDLER_TYPE);
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
            // UnityRuntimeSettingsBridge 会安装 UnityRuntimeKitSettingsStore，让 Tauri 写入的 Kit 设置持久化到 Runtime Settings 资产。
            UnityRuntimeSettingsBridge.EnsureInstalled();
            UnityLogKitRuntimeInstaller.Install(
                UnityRuntimeSettingsBridge.GetLogKitOptions(UnityLogKitOptions.CreateDefault()),
                LogKit.GetLogger());
        }

        private static void OnEditorUpdate()
        {
            ReportHeartbeatWriteError();
            EnsureDefaultLogger();
            if (BuiltinKitIntegrationEnabled)
                UnityManagedRuntimeBackendRegistration.EnsureRegistered();

            var nowUtc = DateTime.UtcNow;
            if (ShouldPollCommandBridge(nowUtc))
                PollCommandBridgeAndRecord(nowUtc);

            if (ShouldPoll(nowUtc, sLastKitSnapshotPublishUtc, TimeSpan.FromMilliseconds(KIT_SNAPSHOT_INTERVAL_MS)))
            {
                PublishSnapshotsProfiled();
                sLastKitSnapshotPublishUtc = nowUtc;
            }

            if ((nowUtc - sLastHeartbeat).TotalMilliseconds >= HEARTBEAT_INTERVAL_MS)
            {
                sLastHeartbeat = nowUtc;
                WriteHeartbeatProfiled();
            }
        }

        private static void PollCoresProfiled()
        {
            using (sPollCoresProfilerMarker.Auto())
                PollCores();
        }

        private static void PublishSnapshotsProfiled()
        {
            using (sPublishSnapshotsProfilerMarker.Auto())
            {
                if (BuiltinKitIntegrationEnabled)
                    KitStateSnapshotPublisher.TryPublishAll(sYokiframeRoot);

                Dispatcher?.PublishAllSnapshots(sYokiframeRoot);
            }
        }

        private static void WriteHeartbeatProfiled()
        {
            using (sWriteHeartbeatProfilerMarker.Auto())
                WriteHeartbeat();
        }

        internal static bool ShouldPoll(DateTime nowUtc, DateTime? lastPollUtc, TimeSpan interval)
        {
            if (!lastPollUtc.HasValue)
                return true;

            if (interval <= TimeSpan.Zero)
                return true;

            return nowUtc - lastPollUtc.Value >= interval;
        }

        private static bool ShouldPollCommandBridge(DateTime nowUtc)
        {
            if (sCommandDirectoryChanged)
            {
                sPollBackoff.Reset();
                return true;
            }

            if (sCommandBridgeNeedsFollowUpPoll || !IsCommandDirectoryWatcherActive())
            {
                return ShouldPoll(
                    nowUtc,
                    sLastPollUtc,
                    TimeSpan.FromMilliseconds(sPollBackoff.CurrentIntervalMs));
            }

            // FileSystemWatcher 正常时不再每秒扫描空目录。低频 watchdog 只负责补偿漏事件，
            // 并让 processing 中的超时命令仍能被恢复。
            return ShouldPoll(
                nowUtc,
                sLastPollUtc,
                TimeSpan.FromMilliseconds(POLL_WATCHDOG_INTERVAL_MS));
        }

        private static void PollCommandBridgeAndRecord(DateTime nowUtc)
        {
            // 先清标志，轮询期间发生的新文件事件会再次置位，避免丢失竞态窗口。
            sCommandDirectoryChanged = false;
            PollCoresProfiled();

            var hadActivity = sEngineCore != default &&
                (sEngineCore.LastPollHadActivity || sEngineCore.BackpressureActive);
            sCommandBridgeNeedsFollowUpPoll = hadActivity;
            sPollBackoff.RecordPollResult(hadActivity);
            sLastPollUtc = nowUtc;
        }

        private static bool IsCommandDirectoryWatcherActive()
        {
            return sCommandDirectoryWatcher != null && sCommandDirectoryWatcher.EnableRaisingEvents;
        }

        public static void PollNow()
        {
            if (sEngineCore != default)
            {
                PollCommandBridgeAndRecord(DateTime.UtcNow);
                return;
            }

            LogKit.Warning("[YokiCommandBridge] 未初始化，请等待 DomainReload 完成后重试");
        }

        private static bool BuiltinKitIntegrationEnabled =>
            EditorPrefs.GetBool(BUILTIN_KIT_INTEGRATION_PREF_KEY, false);

        private static void ResetCommandDirectoryWatcher()
        {
            DisposeCommandDirectoryWatcher();

            try
            {
                var commandDir = Path.Combine(GetEngineRoot(sYokiframeRoot), "commands");
                Directory.CreateDirectory(commandDir);
                sCommandDirectoryWatcher = new FileSystemWatcher(commandDir, "*.json");
                sCommandDirectoryWatcher.IncludeSubdirectories = false;
                sCommandDirectoryWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime;
                sCommandDirectoryWatcher.Created += OnCommandDirectoryChanged;
                sCommandDirectoryWatcher.Changed += OnCommandDirectoryChanged;
                sCommandDirectoryWatcher.Renamed += OnCommandDirectoryChanged;
                sCommandDirectoryWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                LogKit.Warning("[YokiCommandBridge] 命令目录监听启动失败，回退到自适应轮询: " + e.Message);
            }
        }

        private static void OnCommandDirectoryChanged(object sender, FileSystemEventArgs e)
        {
            sCommandDirectoryChanged = true;
        }

        private static void DisposeCommandDirectoryWatcher()
        {
            if (sCommandDirectoryWatcher == null)
                return;

            sCommandDirectoryWatcher.EnableRaisingEvents = false;
            sCommandDirectoryWatcher.Created -= OnCommandDirectoryChanged;
            sCommandDirectoryWatcher.Changed -= OnCommandDirectoryChanged;
            sCommandDirectoryWatcher.Renamed -= OnCommandDirectoryChanged;
            sCommandDirectoryWatcher.Dispose();
            sCommandDirectoryWatcher = null;
        }
    }
}
#endif
