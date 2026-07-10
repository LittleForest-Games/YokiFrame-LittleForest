#if !GODOT
using UnityEditor;

namespace YokiFrame.Unity
{
    // Little Forest fork profile: ManagedRuntime is registered only by an explicit caller.
    public static class UnityManagedRuntimeBackendRegistration
    {
        static UnityManagedRuntimeBackendRegistration()
        {
            EnsureRegistered();
        }

        public static void EnsureRegistered()
        {
            ManagedRuntimeKit.RegisterBackend(new UnityLeanClrManagedRuntimeBackend());
        }
    }
}
#endif
