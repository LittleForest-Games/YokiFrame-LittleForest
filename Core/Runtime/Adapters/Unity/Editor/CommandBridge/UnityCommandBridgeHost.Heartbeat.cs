#if !GODOT
using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace YokiFrame.Unity
{
    internal static partial class UnityCommandBridgeHost
    {
        private static readonly object sHeartbeatWriterLock = new object();
        private static HeartbeatWriteRequest sPendingHeartbeatWrite;
        private static bool sHeartbeatWorkerScheduled;
        private static bool sHeartbeatWriterEnabled = true;
        private static string sPendingHeartbeatWriteError;

        private static void WriteHeartbeat()
        {
            if (string.IsNullOrEmpty(sYokiframeRoot))
                return;

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            QueueHeartbeatWrite(new HeartbeatWriteRequest(
                sYokiframeRoot,
                BuildHeartbeatJson(timestamp)));
        }

        private static void QueueHeartbeatWrite(HeartbeatWriteRequest request)
        {
            lock (sHeartbeatWriterLock)
            {
                if (!sHeartbeatWriterEnabled)
                    return;

                // 心跳只表达最新在线状态。worker 忙碌时直接覆盖尚未开始的旧请求。
                sPendingHeartbeatWrite = request;
                if (sHeartbeatWorkerScheduled)
                    return;

                sHeartbeatWorkerScheduled = true;
            }

            if (!ThreadPool.QueueUserWorkItem(DrainHeartbeatWrites))
            {
                lock (sHeartbeatWriterLock)
                {
                    sHeartbeatWorkerScheduled = false;
                }

                Interlocked.Exchange(ref sPendingHeartbeatWriteError, "无法调度后台心跳写入任务");
            }
        }

        private static void DrainHeartbeatWrites(object state)
        {
            while (true)
            {
                HeartbeatWriteRequest request;
                lock (sHeartbeatWriterLock)
                {
                    if (!sHeartbeatWriterEnabled || sPendingHeartbeatWrite == null)
                    {
                        sHeartbeatWorkerScheduled = false;
                        return;
                    }

                    request = sPendingHeartbeatWrite;
                    sPendingHeartbeatWrite = null;
                }

                try
                {
                    using (sWriteHeartbeatWorkerProfilerMarker.Auto())
                        WriteHeartbeatFile(request.YokiframeRoot, request.Content);
                }
                catch (Exception e)
                {
                    Interlocked.Exchange(ref sPendingHeartbeatWriteError, e.Message);
                }
            }
        }

        private static void ReportHeartbeatWriteError()
        {
            var error = Interlocked.Exchange(ref sPendingHeartbeatWriteError, null);

            if (!string.IsNullOrEmpty(error))
                LogKit.Warning($"[YokiCommandBridge] 写入心跳失败: {error}");
        }

        private static void StopHeartbeatWriter()
        {
            lock (sHeartbeatWriterLock)
            {
                sHeartbeatWriterEnabled = false;
                sPendingHeartbeatWrite = null;
            }
        }

        private static void WriteEngineRegistry()
        {
            if (string.IsNullOrEmpty(sYokiframeRoot))
                return;

            try
            {
                WriteEngineRegistryFile(
                    sYokiframeRoot,
                    Application.unityVersion,
                    Path.GetDirectoryName(Application.dataPath));
            }
            catch (Exception e)
            {
                LogKit.Warning($"[YokiCommandBridge] 写入 engine registry 失败: {e.Message}");
            }
        }

        internal static string BuildHeartbeatJson(long timestamp) =>
            $"{{\"protocolVersion\":2,\"engineId\":\"{ENGINE_ID}\",\"timestamp\":{timestamp},\"createdAtUtc\":\"{DateTime.UtcNow:O}\"}}";

        internal static string BuildEngineRegistryJson(string unityVersion, string projectPath, string startedAtUtc)
        {
#if YOKIFRAME_LUBAN_SUPPORT
            const string LUBAN_AVAILABLE = "true";
#else
            const string LUBAN_AVAILABLE = "false";
#endif
            var implementedKitsJson = CommandBridgeKitRegistry.BuildImplementedKitsJson(CommandBridgeEngineKind.Unity);
            var kitFeaturesJson = CommandBridgeKitRegistry.BuildKitFeaturesJson(CommandBridgeEngineKind.Unity);
            return "{\"protocolVersion\":2,\"engineId\":\"" + ENGINE_ID +
                   "\",\"engine\":\"Unity\",\"version\":\"" + JsonHelper.EscapeString(unityVersion) +
                   "\",\"projectPath\":\"" + JsonHelper.EscapeString(projectPath) +
                   "\",\"adapterVersion\":\"2.0.0\",\"startedAtUtc\":\"" + JsonHelper.EscapeString(startedAtUtc) +
                   "\",\"capabilities\":[\"commands\",\"events\",\"heartbeat\",\"bridge_status\",\"snapshots\",\"telemetry\"]" +
                   ",\"implementedKits\":" + implementedKitsJson +
                   ",\"kitFeatures\":" + kitFeaturesJson +
                   ",\"optionalDependencies\":{\"luban\":{\"available\":" + LUBAN_AVAILABLE +
                   ",\"define\":\"YOKIFRAME_LUBAN_SUPPORT\"" +
                   ",\"packageName\":\"com.code-philosophy.luban\"" +
                   ",\"asmdefName\":\"Luban.Runtime\"" +
                   ",\"typeName\":\"Luban.ByteBuf\"}}}";
        }

        internal static void WriteHeartbeatFile(string yokiframeRoot, long timestamp)
        {
            WriteHeartbeatFile(yokiframeRoot, BuildHeartbeatJson(timestamp));
        }

        private static void WriteHeartbeatFile(string yokiframeRoot, string content)
        {
            var engineRoot = Path.Combine(yokiframeRoot, "engines", ENGINE_ID);
            var engineHeartbeatPath = Path.Combine(engineRoot, "status", "heartbeat.json");
            FileBridgeFileSystem.AtomicWriteVolatileTextInRoot(yokiframeRoot, engineHeartbeatPath, content);
        }

        internal static void WriteEngineRegistryFile(string yokiframeRoot, string unityVersion, string projectPath)
        {
            var engineRoot = Path.Combine(yokiframeRoot, "engines", ENGINE_ID);
            var enginePath = Path.Combine(engineRoot, "engine.json");
            FileBridgeFileSystem.AtomicWriteAllTextInRoot(
                yokiframeRoot,
                enginePath,
                BuildEngineRegistryJson(unityVersion, projectPath, sStartedAtUtc));
        }

        private sealed class HeartbeatWriteRequest
        {
            public readonly string YokiframeRoot;
            public readonly string Content;

            public HeartbeatWriteRequest(string yokiframeRoot, string content)
            {
                YokiframeRoot = yokiframeRoot;
                Content = content;
            }
        }
    }
}
#endif
