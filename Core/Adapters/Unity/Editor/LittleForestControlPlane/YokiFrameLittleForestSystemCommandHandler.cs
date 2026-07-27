#if UNITY_EDITOR && !GODOT
using System;

namespace YokiFrame.Unity
{
    /// <summary>
    /// Control-plane infrastructure commands only. This is not a native
    /// gameplay Kit and does not register any YokiFrame Kit lifecycle.
    /// </summary>
    internal sealed class YokiFrameLittleForestSystemCommandHandler :
        IKitCommandHandler
    {
        private static readonly string[] sSupportedActions =
        {
            "ping",
            "bridge_status",
            "list_commands"
        };

        private readonly Func<string> mBuildStatusJson;
        private readonly Func<string> mBuildCatalogJson;

        internal YokiFrameLittleForestSystemCommandHandler(
            Func<string> buildStatusJson,
            Func<string> buildCatalogJson)
        {
            mBuildStatusJson = buildStatusJson
                ?? throw new ArgumentNullException(nameof(buildStatusJson));
            mBuildCatalogJson = buildCatalogJson
                ?? throw new ArgumentNullException(nameof(buildCatalogJson));
        }

        public string KitName => "System";

        public string[] SupportedActions => (string[])sSupportedActions.Clone();

        public string HandleAction(string action, string payloadJson)
        {
            switch (action)
            {
                case "ping":
                    return "{\"pong\":true}";
                case "bridge_status":
                    return mBuildStatusJson();
                case "list_commands":
                    return mBuildCatalogJson();
                default:
                    throw new NotSupportedException(
                        "Unknown System action '" + action + "'.");
            }
        }
    }
}
#endif
