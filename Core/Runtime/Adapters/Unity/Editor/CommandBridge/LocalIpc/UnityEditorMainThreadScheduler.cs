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
        private readonly EditorApplication.CallbackFunction mDrainCallback;
        private readonly CallDelayedDelegate mCallDelayed;
        private Action mCancelScheduledDrain;
        private int mScheduled;
        private int mDisposed;

        internal UnityEditorMainThreadScheduler()
        {
            mDrainCallback = DrainOnEditorUpdate;
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

            mCallDelayed =
                (CallDelayedDelegate)Delegate.CreateDelegate(
                    typeof(CallDelayedDelegate),
                    callDelayed);
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

            Action cancel;
            lock (mLifecycleSync)
            {
                cancel = mCancelScheduledDrain;
                mCancelScheduledDrain = null;
            }

            cancel?.Invoke();
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
                // Unity 2022.3 CallDelayed registers a one-shot tick callback
                // and wakes the Editor through its [ThreadSafe] SignalTick.
                cancel = mCallDelayed(mDrainCallback, 0d);
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
                    mCancelScheduledDrain = cancel;
                }
            }
        }

        private void DrainOnEditorUpdate()
        {
            lock (mLifecycleSync)
                mCancelScheduledDrain = null;
            Interlocked.Exchange(ref mScheduled, 0);
            if (Volatile.Read(ref mDisposed) != 0)
                return;

            Action action;
            while (mActions.TryDequeue(out action))
            {
                action();
            }

            if (!mActions.IsEmpty)
                ScheduleOneShotUpdate();
        }

        private delegate Action CallDelayedDelegate(
            EditorApplication.CallbackFunction action,
            double delaySeconds);
    }
}
#endif
