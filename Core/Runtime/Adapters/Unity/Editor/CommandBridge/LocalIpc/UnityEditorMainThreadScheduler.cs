#if !GODOT
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using UnityEditor;

namespace YokiFrame.Unity
{
    /// <summary>
    /// One-shot Editor update scheduler. A worker request registers a callback
    /// only while work is pending, so an idle Host owns no per-frame poll.
    /// </summary>
    internal sealed class UnityEditorMainThreadScheduler : IDisposable
    {
        private readonly ConcurrentQueue<Action> mActions =
            new ConcurrentQueue<Action>();
        private readonly object mLifecycleSync = new object();
        private readonly EditorApplication.CallbackFunction
            mRegisterDrainCallback;
        private readonly EditorApplication.CallbackFunction mDrainCallback;
        private readonly CallDelayedDelegate mCallDelayed;
        private readonly RegisterUpdateDelegate mRegisterUpdate;
        private Action mCancelScheduledRegistration;
        private Action mCancelRegisteredUpdate;
        private int mScheduled;
        private int mDisposed;

        internal UnityEditorMainThreadScheduler()
            : this(
                CreateCallDelayed(),
                RegisterEditorUpdate)
        {
        }

        internal UnityEditorMainThreadScheduler(
            CallDelayedDelegate callDelayed,
            RegisterUpdateDelegate registerUpdate)
        {
            mCallDelayed = callDelayed ??
                throw new ArgumentNullException(nameof(callDelayed));
            mRegisterUpdate = registerUpdate ??
                throw new ArgumentNullException(nameof(registerUpdate));
            mRegisterDrainCallback = RegisterDrainForNextUpdate;
            mDrainCallback = DrainOnEditorUpdate;
        }

        private static CallDelayedDelegate CreateCallDelayed()
        {
            var callDelayed = typeof(EditorApplication).GetMethod(
                "CallDelayed",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(EditorApplication.CallbackFunction),
                    typeof(double)
                },
                null);
            if (callDelayed == null ||
                callDelayed.ReturnType != typeof(Action))
            {
                throw new MissingMethodException(
                    "Thread-safe EditorApplication.CallDelayed is unavailable.");
            }

            return (CallDelayedDelegate)Delegate.CreateDelegate(
                typeof(CallDelayedDelegate),
                callDelayed);
        }

        private static Action RegisterEditorUpdate(
            EditorApplication.CallbackFunction action)
        {
            EditorApplication.update += action;
            return () => EditorApplication.update -= action;
        }

        internal void Post(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            if (Volatile.Read(ref mDisposed) != 0)
                return;

            // Always cross the one-shot Editor update boundary, including
            // posts made while draining on the main thread. Inline execution
            // would let a continuation recursively drain the whole queue in
            // one Editor frame and bypass the per-drain budget.
            mActions.Enqueue(action);
            ScheduleOneShotUpdate();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref mDisposed, 1) != 0)
                return;

            Action cancelRegistration;
            Action cancelUpdate;
            lock (mLifecycleSync)
            {
                cancelRegistration = mCancelScheduledRegistration;
                mCancelScheduledRegistration = null;
                cancelUpdate = mCancelRegisteredUpdate;
                mCancelRegisteredUpdate = null;
            }

            cancelRegistration?.Invoke();
            cancelUpdate?.Invoke();
            Interlocked.Exchange(ref mScheduled, 0);
            Action ignored;
            while (mActions.TryDequeue(out ignored))
            {
            }
        }

        private void ScheduleOneShotUpdate()
        {
            if (Volatile.Read(ref mDisposed) != 0)
                return;
            if (Interlocked.CompareExchange(
                    ref mScheduled,
                    1,
                    0) != 0)
            {
                return;
            }

            Action cancel;
            try
            {
                // CallDelayed is the thread-safe wake-up edge only. Its
                // callback may repeat inside one Profiler frame, so it must
                // register (not execute) the actual next-update drain.
                cancel = mCallDelayed(
                    mRegisterDrainCallback,
                    0d);
            }
            catch
            {
                Interlocked.Exchange(ref mScheduled, 0);
                throw;
            }

            lock (mLifecycleSync)
            {
                if (Volatile.Read(ref mDisposed) != 0 ||
                    Volatile.Read(ref mScheduled) == 0)
                {
                    cancel?.Invoke();
                }
                else
                {
                    mCancelScheduledRegistration = cancel;
                }
            }
        }

        private void RegisterDrainForNextUpdate()
        {
            lock (mLifecycleSync)
            {
                mCancelScheduledRegistration = null;
                if (Volatile.Read(ref mDisposed) != 0 ||
                    Volatile.Read(ref mScheduled) == 0)
                {
                    return;
                }

                try
                {
                    mCancelRegisteredUpdate =
                        mRegisterUpdate(mDrainCallback) ??
                        throw new InvalidOperationException(
                            "Editor update registration returned no cancellation.");
                }
                catch
                {
                    Interlocked.Exchange(ref mScheduled, 0);
                    throw;
                }
            }
        }

        private void DrainOnEditorUpdate()
        {
            Action cancelUpdate;
            lock (mLifecycleSync)
            {
                cancelUpdate = mCancelRegisteredUpdate;
                mCancelRegisteredUpdate = null;
            }

            // Remove the one-shot update callback before executing user work.
            cancelUpdate?.Invoke();
            Interlocked.Exchange(ref mScheduled, 0);
            if (Volatile.Read(ref mDisposed) != 0)
                return;

            // Drain only the generation visible when this Editor update
            // starts. Actions posted by a running callback belong to the next
            // one-shot update; consuming until empty would collapse every
            // budgeted continuation back into this frame.
            var actionsAtStart = mActions.Count;
            Action action;
            for (var index = 0;
                 index < actionsAtStart &&
                 mActions.TryDequeue(out action);
                 index++)
            {
                action();
            }

            if (!mActions.IsEmpty)
                ScheduleOneShotUpdate();
        }

        internal delegate Action CallDelayedDelegate(
            EditorApplication.CallbackFunction action,
            double delaySeconds);

        internal delegate Action RegisterUpdateDelegate(
            EditorApplication.CallbackFunction action);
    }
}
#endif
