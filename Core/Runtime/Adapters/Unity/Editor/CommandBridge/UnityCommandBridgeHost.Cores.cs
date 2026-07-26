#if !GODOT
using System.Text;

namespace YokiFrame.Unity
{
    internal static partial class UnityCommandBridgeHost
    {
        private static string BuildActiveBridgeStatusJson(
            bool detail)
        {
            if (sLocalIpcHost != null)
                return sLocalIpcHost.BuildStatusJson();

            var builder = new StringBuilder(256);
            builder.Append(
                "{\"available\":false,\"transport\":\"local-ipc-v1\"," +
                "\"reason\":\"");
            builder.Append(
                JsonHelper.EscapeString(
                    string.IsNullOrEmpty(
                        sTransportInitializationError)
                        ? "host is not initialized"
                        : sTransportInitializationError));
            builder.Append("\"}");
            return builder.ToString();
        }
    }
}
#endif
