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
            var wakeUps =
                new Queue<EditorApplication.CallbackFunction>();
            var updates =
                new Queue<EditorApplication.CallbackFunction>();
            var calls = new List<string>();
            using (var scheduler =
                   new UnityEditorMainThreadScheduler(
                       (callback, delaySeconds) =>
                       {
                           wakeUps.Enqueue(callback);
                           return () => { };
                       },
                       callback =>
                       {
                           updates.Enqueue(callback);
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

                Assert.AreEqual(1, wakeUps.Count);
                Assert.AreEqual(0, updates.Count);
                wakeUps.Dequeue().Invoke();

                Assert.AreEqual(0, calls.Count);
                Assert.AreEqual(1, updates.Count);
                updates.Dequeue().Invoke();

                CollectionAssert.AreEqual(
                    new[] { "first" },
                    calls);
                Assert.AreEqual(1, wakeUps.Count);
                Assert.AreEqual(0, updates.Count);

                wakeUps.Dequeue().Invoke();
                Assert.AreEqual(1, updates.Count);
                updates.Dequeue().Invoke();

                CollectionAssert.AreEqual(
                    new[] { "first", "continuation" },
                    calls);
                Assert.AreEqual(0, wakeUps.Count);
                Assert.AreEqual(0, updates.Count);
            }
        }
    }
}
