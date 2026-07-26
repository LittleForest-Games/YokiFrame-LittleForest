using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;

namespace YokiFrame.Unity.Editor.Tests
{
    [TestFixture]
    public sealed class UnityEditorMainThreadSchedulerTests
    {
        [Test]
        public void PostWhileDraining_WaitsForNextOneShotUpdate()
        {
            var scheduled =
                new Queue<EditorApplication.CallbackFunction>();
            var calls = new List<string>();
            using (var scheduler =
                   new UnityEditorMainThreadScheduler(
                       (callback, delaySeconds) =>
                       {
                           scheduled.Enqueue(callback);
                           return () => { };
                       }))
            {
                scheduler.Post(
                    () =>
                    {
                        calls.Add("first");
                        scheduler.Post(
                            () => calls.Add("continuation"));
                    });

                Assert.AreEqual(1, scheduled.Count);
                scheduled.Dequeue().Invoke();

                CollectionAssert.AreEqual(
                    new[] { "first" },
                    calls);
                Assert.AreEqual(1, scheduled.Count);

                scheduled.Dequeue().Invoke();

                CollectionAssert.AreEqual(
                    new[] { "first", "continuation" },
                    calls);
                Assert.AreEqual(0, scheduled.Count);
            }
        }
    }
}
