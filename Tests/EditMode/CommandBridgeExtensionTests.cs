using System;
using NUnit.Framework;

namespace YokiFrame.Tests
{
    /// <summary>
    /// CommandBridge 扩展 API 测试：Register/TryRegister、RegisterSnapshot、PublishAllSnapshots、
    /// CommandBridgeExtensionContext、IYokiFrameCommandBridgeExtension 模拟扩展。
    /// 仅测 YokiFrame asmdef public 类型，不测 UnityCommandBridgeHost 内部状态。
    /// 覆盖主 plan 验证步骤 §3。
    /// </summary>
    [TestFixture]
    public class CommandBridgeExtensionTests
    {
        // ---- Mock 辅助 ----

        internal sealed class MockHandler : IKitCommandHandler
        {
            public string KitName { get; }
            public string[] SupportedActions { get; } = new[] { "ping" };
            public int HandleCallCount;

            public MockHandler(string kitName = "MockKit")
            {
                KitName = kitName;
            }

            public string HandleAction(string action, string payloadJson)
            {
                HandleCallCount++;
                return "{\"ok\":true}";
            }
        }

        internal sealed class MockSnapshotPublisher : IKitSnapshotPublisher
        {
            public string EngineId { get; set; } = "test-engine";
            public string KitName { get; set; } = "MockKit";
            public string SnapshotName { get; set; } = "state";
            public int PublishCallCount;
            public bool ThrowOnPublish;
            public string LastRoot;

            public string TryPublish(string yokiframeRoot)
            {
                PublishCallCount++;
                LastRoot = yokiframeRoot;
                if (ThrowOnPublish)
                    throw new InvalidOperationException("mock publish failure");
                return yokiframeRoot + "/snapshot.json";
            }
        }

        // 用于测试"Host 初始化后注册"路径：扩展在 Register 中保存 context 引用
        [YokiFrameCommandBridgeExtension("test-ext")]
        internal sealed class MockExtension : IYokiFrameCommandBridgeExtension
        {
            public static ICommandBridgeExtensionContext SavedContext;

            public string ExtensionId => "test-ext";

            public void Register(ICommandBridgeExtensionContext context)
            {
                SavedContext = context;
            }
        }

        // ---- Register / TryRegister ----

        [Test]
        public void Register_DuplicateKitName_ThrowsInvalidOperationException()
        {
            var d = new KitCommandDispatcher();
            d.Register(new MockHandler("DupKit"));

            Assert.Throws<InvalidOperationException>(() => d.Register(new MockHandler("DupKit")));
        }

        [Test]
        public void Register_NullHandler_ThrowsArgumentNullException()
        {
            var d = new KitCommandDispatcher();
            Assert.Throws<ArgumentNullException>(() => d.Register(null));
        }

        [Test]
        public void TryRegister_Duplicate_ReturnsFalse()
        {
            var d = new KitCommandDispatcher();
            Assert.IsTrue(d.TryRegister(new MockHandler("DupKit")));
            Assert.IsFalse(d.TryRegister(new MockHandler("DupKit")));
        }

        [Test]
        public void TryRegister_NullHandler_ReturnsFalse()
        {
            var d = new KitCommandDispatcher();
            Assert.IsFalse(d.TryRegister(null));
        }

        [Test]
        public void TryRegister_FirstCall_ReturnsTrue()
        {
            var d = new KitCommandDispatcher();
            Assert.IsTrue(d.TryRegister(new MockHandler("FirstKit")));
        }

        // ---- RegisterSnapshot ----

        [Test]
        public void RegisterSnapshot_NullPublisher_ThrowsArgumentNullException()
        {
            var d = new KitCommandDispatcher();
            Assert.Throws<ArgumentNullException>(() => d.RegisterSnapshot(null));
        }

        [Test]
        public void RegisterSnapshot_ReturnsToken_DisposeRemovesFromList()
        {
            var d = new KitCommandDispatcher();
            var publisher = new MockSnapshotPublisher();
            var token = d.RegisterSnapshot(publisher);

            Assert.IsNotNull(token);
            Assert.AreEqual(1, d.SnapshotPublishers.Count);

            token.Dispose();
            Assert.AreEqual(0, d.SnapshotPublishers.Count);
        }

        [Test]
        public void SnapshotPublishers_ReturnsReadOnlyList()
        {
            var d = new KitCommandDispatcher();
            var publishers = d.SnapshotPublishers;
            Assert.IsInstanceOf<System.Collections.Generic.IReadOnlyList<IKitSnapshotPublisher>>(publishers);
            Assert.AreEqual(0, publishers.Count);
        }

        [Test]
        public void RegisterPolicy_NullPolicy_ThrowsArgumentNullException()
        {
            var d = new KitCommandDispatcher();
            Assert.Throws<ArgumentNullException>(() => d.RegisterPolicy(null));
        }

        // ---- PublishAllSnapshots ----

        [Test]
        public void PublishAllSnapshots_CallsAllPublishers()
        {
            var d = new KitCommandDispatcher();
            var p1 = new MockSnapshotPublisher { SnapshotName = "s1" };
            var p2 = new MockSnapshotPublisher { SnapshotName = "s2" };
            var p3 = new MockSnapshotPublisher { SnapshotName = "s3" };
            d.RegisterSnapshot(p1);
            d.RegisterSnapshot(p2);
            d.RegisterSnapshot(p3);

            d.PublishAllSnapshots("/fake/root");

            Assert.AreEqual(1, p1.PublishCallCount);
            Assert.AreEqual(1, p2.PublishCallCount);
            Assert.AreEqual(1, p3.PublishCallCount);
            Assert.AreEqual("/fake/root", p1.LastRoot);
        }

        [Test]
        public void PublishAllSnapshots_PublisherThrows_DoesNotBreakOthers()
        {
            var d = new KitCommandDispatcher();
            var p1 = new MockSnapshotPublisher { SnapshotName = "s1" };
            var p2 = new MockSnapshotPublisher { SnapshotName = "s2", ThrowOnPublish = true };
            var p3 = new MockSnapshotPublisher { SnapshotName = "s3" };
            d.RegisterSnapshot(p1);
            d.RegisterSnapshot(p2);
            d.RegisterSnapshot(p3);

            // 中间 publisher 抛异常，不传播，其他 publisher 仍被调用
            Assert.DoesNotThrow(() => d.PublishAllSnapshots("/fake/root"));

            Assert.AreEqual(1, p1.PublishCallCount);
            Assert.AreEqual(1, p2.PublishCallCount);
            Assert.AreEqual(1, p3.PublishCallCount);
        }

        [Test]
        public void PublishAllSnapshots_EmptyList_DoesNotThrow()
        {
            var d = new KitCommandDispatcher();
            Assert.DoesNotThrow(() => d.PublishAllSnapshots("/fake/root"));
        }

        // ---- CommandBridgeExtensionContext ----

        [Test]
        public void ContextCtor_NullDispatcher_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new CommandBridgeExtensionContext(null));
        }

        [Test]
        public void Context_RegisterHandler_DelegatesToDispatcher_DuplicateThrows()
        {
            var d = new KitCommandDispatcher();
            var ctx = new CommandBridgeExtensionContext(d);

            ctx.RegisterHandler(new MockHandler("CtxKit"));
            Assert.Throws<InvalidOperationException>(() => ctx.RegisterHandler(new MockHandler("CtxKit")));
        }

        [Test]
        public void Context_RegisterPolicy_ReturnsToken_AddsToTokens()
        {
            var d = new KitCommandDispatcher();
            var ctx = new CommandBridgeExtensionContext(d);

            var token = ctx.RegisterPolicy(c => CommandBridgePolicyResult.Allow());
            Assert.IsNotNull(token);
            Assert.AreEqual(1, ctx.Tokens.Count);
            Assert.AreSame(token, ctx.Tokens[0]);
        }

        [Test]
        public void Context_RegisterSnapshot_ReturnsToken_AddsToTokens()
        {
            var d = new KitCommandDispatcher();
            var ctx = new CommandBridgeExtensionContext(d);

            var token = ctx.RegisterSnapshot(new MockSnapshotPublisher());
            Assert.IsNotNull(token);
            Assert.AreEqual(1, ctx.Tokens.Count);
            Assert.AreSame(token, ctx.Tokens[0]);
            Assert.AreEqual(1, d.SnapshotPublishers.Count);
        }

        // ---- 扩展发现属性 ----

        [Test]
        public void ExtensionAttribute_NullExtensionId_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new YokiFrameCommandBridgeExtensionAttribute(null));
        }

        [Test]
        public void ExtensionAttribute_StoresExtensionId()
        {
            var attr = new YokiFrameCommandBridgeExtensionAttribute("my-ext");
            Assert.AreEqual("my-ext", attr.ExtensionId);
        }

        [Test]
        public void AttributeUsage_OnlyClass()
        {
            var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
                typeof(YokiFrameCommandBridgeExtensionAttribute), typeof(AttributeUsageAttribute));
            Assert.IsNotNull(usage);
            Assert.AreEqual(AttributeTargets.Class, usage.ValidOn);
            Assert.IsFalse(usage.Inherited);
        }

        // ---- "Host 初始化后注册" 路径：扩展保存 context 后动态注册 ----

        [Test]
        public void Extension_SavesContext_CanDynamicallyRegisterAfterInit()
        {
            var d = new KitCommandDispatcher();
            var ctx = new CommandBridgeExtensionContext(d);
            var ext = new MockExtension();

            // 模拟 Host.LoadExtensions 调用 ext.Register(ctx)
            ext.Register(ctx);

            // 扩展保存了 context，后续可通过它动态注册（"初始化后注册"路径）
            Assert.AreSame(ctx, MockExtension.SavedContext);

            var handler = new MockHandler("DynamicKit");
            var policyToken = ctx.RegisterPolicy(c => CommandBridgePolicyResult.Allow());
            var snapToken = ctx.RegisterSnapshot(new MockSnapshotPublisher());

            // 验证三项都注册到 dispatcher
            Assert.AreEqual(1, d.SnapshotPublishers.Count);
            // handler 路由验证
            var resp = d.Dispatch("{\"kit\":\"DynamicKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
            Assert.AreEqual(1, handler.HandleCallCount);

            // dispose token 后从 dispatcher 移除
            policyToken.Dispose();
            snapToken.Dispose();
            Assert.AreEqual(0, d.SnapshotPublishers.Count);
        }

        [Test]
        public void Extension_AllTokensDisposed_DispatcherCleared()
        {
            var d = new KitCommandDispatcher();
            var ctx = new CommandBridgeExtensionContext(d);

            ctx.RegisterPolicy(c => CommandBridgePolicyResult.Deny("X", "x"));
            ctx.RegisterSnapshot(new MockSnapshotPublisher());
            ctx.RegisterSnapshot(new MockSnapshotPublisher { SnapshotName = "s2" });

            Assert.AreEqual(2, d.SnapshotPublishers.Count);
            // 有 policy → Dispatch 被拒绝
            var resp = d.Dispatch("{\"kit\":\"AnyKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"code\":\"X\""));

            // dispose 所有 token（模拟 Host.DisposeExtensions 行为，但不通过 Host）
            foreach (var token in ctx.Tokens)
                token.Dispose();

            // dispatcher 恢复无策略、无 snapshot
            Assert.AreEqual(0, d.SnapshotPublishers.Count);
            // 无 policy → Allow（注册 handler 才能路由成功，这里只验 policy 链为空不拒绝）
            var resp2 = d.Dispatch("{\"kit\":\"AnyKit\",\"action\":\"ping\"}");
            Assert.IsFalse(resp2.Contains("\"code\":\"X\""));
            // AnyKit 无 handler → UnknownKit，但不是 policy 拒绝
            Assert.IsTrue(resp2.Contains("\"code\":\"UnknownKit\""));
        }

        [TearDown]
        public void TearDown()
        {
            MockExtension.SavedContext = null;
        }
    }
}
