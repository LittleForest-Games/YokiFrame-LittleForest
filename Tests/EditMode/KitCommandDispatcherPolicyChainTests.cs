using System;
using NUnit.Framework;

namespace YokiFrame.Tests
{
    /// <summary>
    /// KitCommandDispatcher 组合链、Register/TryRegister、Dispatch envelope 解析与过期检查测试。
    /// 覆盖主 plan 验证步骤 §2。
    /// </summary>
    [TestFixture]
    public class KitCommandDispatcherPolicyChainTests
    {
        // ---- Mock 辅助 ----

        internal sealed class MockKitCommandHandler : IKitCommandHandler
        {
            public string KitName { get; }
            public string[] SupportedActions { get; }
            public int HandleCallCount;
            private readonly string fResultJson;

            public MockKitCommandHandler(string kitName = "TestKit", string resultJson = "{\"ok\":true}")
            {
                KitName = kitName;
                SupportedActions = new[] { "ping" };
                fResultJson = resultJson;
            }

            public string HandleAction(string action, string payloadJson)
            {
                HandleCallCount++;
                return fResultJson;
            }
        }

        private static KitCommandDispatcher NewDispatcher()
        {
            return new KitCommandDispatcher();
        }

        private static string Dispatch(KitCommandDispatcher d, string json)
        {
            return d.Dispatch(json);
        }

        // ---- Policy 组合链 ----

        [Test]
        public void RegisterPolicy_ReturnsToken_DisposeRemoves()
        {
            var d = NewDispatcher();
            // policy 注册到内部 mPolicies 链（无公开访问器），通过 Dispatch 行为间接验证
            var token = d.RegisterPolicy(c => CommandBridgePolicyResult.Allow());

            Assert.IsNotNull(token);
            token.Dispose();
            // dispose 后无策略 → Allow，注册 handler 后 Dispatch 成功
            var handler = new MockKitCommandHandler();
            d.Register(handler);
            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
        }

        [Test]
        public void NoPolicy_DefaultAllow()
        {
            var d = NewDispatcher();
            d.Register(new MockKitCommandHandler());

            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
        }

        [Test]
        public void PolicyChain_AnyDeny_StopsChain_NoReallow()
        {
            var d = NewDispatcher();
            var callOrder = new System.Collections.Generic.List<string>();
            d.RegisterPolicy(c => { callOrder.Add("first-deny"); return CommandBridgePolicyResult.Deny("FirstDeny", "first denied"); });
            d.RegisterPolicy(c => { callOrder.Add("second-allow"); return CommandBridgePolicyResult.Allow(); });

            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");

            // 链在第一个 deny 后停止，第二个策略不应被调用
            Assert.AreEqual(1, callOrder.Count);
            Assert.AreEqual("first-deny", callOrder[0]);
            Assert.IsTrue(resp.Contains("\"status\":\"error\""));
            Assert.IsTrue(resp.Contains("\"code\":\"FirstDeny\""));
        }

        [Test]
        public void PolicyChain_AllAllow_RoutesToHandler()
        {
            var d = NewDispatcher();
            var handler = new MockKitCommandHandler();
            d.Register(handler);
            d.RegisterPolicy(c => CommandBridgePolicyResult.Allow());
            d.RegisterPolicy(c => CommandBridgePolicyResult.Allow());

            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
            Assert.AreEqual(1, handler.HandleCallCount);
        }

        [Test]
        public void PolicyChain_NullResult_TreatedAsDeny_PolicyDenied()
        {
            var d = NewDispatcher();
            d.RegisterPolicy(c => null);

            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"status\":\"error\""));
            Assert.IsTrue(resp.Contains("\"code\":\"PolicyDenied\""));
            Assert.IsTrue(resp.Contains("Command policy returned null"));
        }

        [Test]
        public void PolicyChain_NullResult_StopsChain_LaterPolicyNotCalled()
        {
            var d = NewDispatcher();
            bool secondCalled = false;
            d.RegisterPolicy(c => null);
            d.RegisterPolicy(c => { secondCalled = true; return CommandBridgePolicyResult.Allow(); });

            Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsFalse(secondCalled);
        }

        [Test]
        public void ObsoleteCommandPolicy_Setter_ConvertsToSingleElementChain()
        {
            var d = NewDispatcher();
#pragma warning disable CS0618
            d.CommandPolicy = c => CommandBridgePolicyResult.Deny("LegacyDeny", "legacy");
#pragma warning restore CS0618

            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"code\":\"LegacyDeny\""));

            // setter 清空旧链再设单元素：再设一个不同 policy，旧的应被清掉
#pragma warning disable CS0618
            d.CommandPolicy = c => CommandBridgePolicyResult.Deny("NewDeny", "new");
#pragma warning restore CS0618
            var resp2 = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp2.Contains("\"code\":\"NewDeny\""));
            Assert.IsFalse(resp2.Contains("LegacyDeny"));
        }

        [Test]
        public void ObsoleteCommandPolicy_Getter_ReturnsFirstOrNull()
        {
            var d = NewDispatcher();
#pragma warning disable CS0618
            Assert.IsNull(d.CommandPolicy);
            var policy = new Func<CommandBridgeCommand, CommandBridgePolicyResult>(c => CommandBridgePolicyResult.Allow());
            d.CommandPolicy = policy;
            Assert.IsNotNull(d.CommandPolicy);
            d.CommandPolicy = null;
            Assert.IsNull(d.CommandPolicy);
#pragma warning restore CS0618
        }

        // ---- Dispatch envelope 解析 ----

        [Test]
        public void Dispatch_MissingEnvelopeFields_DoesNotReject_OldCommands()
        {
            var d = NewDispatcher();
            var handler = new MockKitCommandHandler();
            d.Register(handler);

            // 无 protocolVersion/createdAtUtc/timeoutMs → 兼容旧命令
            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
            Assert.AreEqual(1, handler.HandleCallCount);
        }

        [Test]
        public void Dispatch_NoCreatedAtUtc_DeadlineMaxValue_NeverExpires()
        {
            var d = NewDispatcher();
            var handler = new MockKitCommandHandler();
            d.Register(handler);

            // 旧命令：无 createdAtUtc、无 timeoutMs →
            //   createdAtUtc=MinValue, timeoutMs=0 → DeadlineUtc=MaxValue → IsExpired 永远 false
            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
            Assert.AreEqual(1, handler.HandleCallCount);
        }

        [Test]
        public void Dispatch_TimeoutMsZero_NeverExpires_EvenWithOldCreatedAt()
        {
            var d = NewDispatcher();
            var handler = new MockKitCommandHandler();
            d.Register(handler);

            // createdAtUtc 在过去 + timeoutMs=0 → DeadlineUtc=MaxValue，不过期
            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\",\"createdAtUtc\":\"2020-01-01T00:00:00.0000000Z\",\"timeoutMs\":0}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
            Assert.AreEqual(1, handler.HandleCallCount);
        }

        [Test]
        public void Dispatch_ExpiredCommand_ReturnsCommandExpired_HandlerNotCalled()
        {
            var d = NewDispatcher();
            var handler = new MockKitCommandHandler();
            d.Register(handler);

            // createdAtUtc 在过去 + timeoutMs=1 → deadline 早已过期
            var past = DateTime.UtcNow.AddMinutes(-1).ToString("O");
            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\",\"createdAtUtc\":\"" + past + "\",\"timeoutMs\":1}");

            Assert.IsTrue(resp.Contains("\"status\":\"error\""));
            Assert.IsTrue(resp.Contains("\"code\":\"CommandExpired\""));
            Assert.IsTrue(resp.Contains("\"recoverable\":false"));
            Assert.AreEqual(0, handler.HandleCallCount);
        }

        [Test]
        public void Dispatch_ExpiredCommand_SkipsPolicyChain()
        {
            var d = NewDispatcher();
            bool policyCalled = false;
            d.RegisterPolicy(c => { policyCalled = true; return CommandBridgePolicyResult.Allow(); });

            var past = DateTime.UtcNow.AddMinutes(-1).ToString("O");
            Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\",\"createdAtUtc\":\"" + past + "\",\"timeoutMs\":1}");

            Assert.IsFalse(policyCalled);
        }

        [Test]
        public void Dispatch_NotExpired_RoutesToHandler()
        {
            var d = NewDispatcher();
            var handler = new MockKitCommandHandler();
            d.Register(handler);

            // createdAtUtc 现在 + timeoutMs=60000 → 60s 后才过期
            var now = DateTime.UtcNow.ToString("O");
            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\",\"createdAtUtc\":\"" + now + "\",\"timeoutMs\":60000}");
            Assert.IsTrue(resp.Contains("\"status\":\"success\""));
            Assert.AreEqual(1, handler.HandleCallCount);
        }

        [Test]
        public void Dispatch_PolicyDeny_HandlerNotCalled()
        {
            var d = NewDispatcher();
            var handler = new MockKitCommandHandler();
            d.Register(handler);
            d.RegisterPolicy(c => CommandBridgePolicyResult.Deny("Blocked", "blocked by policy"));

            var resp = Dispatch(d, "{\"kit\":\"TestKit\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"code\":\"Blocked\""));
            Assert.AreEqual(0, handler.HandleCallCount);
        }

        [Test]
        public void Dispatch_UnknownKit_ReturnsUnknownKit()
        {
            var d = NewDispatcher();

            var resp = Dispatch(d, "{\"kit\":\"Nope\",\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"code\":\"UnknownKit\""));
        }

        [Test]
        public void Dispatch_MissingKit_ReturnsInvalidCommand()
        {
            var d = NewDispatcher();

            var resp = Dispatch(d, "{\"action\":\"ping\"}");
            Assert.IsTrue(resp.Contains("\"code\":\"InvalidCommand\""));
        }

        [Test]
        public void Dispatch_MissingAction_ReturnsInvalidCommand()
        {
            var d = NewDispatcher();

            var resp = Dispatch(d, "{\"kit\":\"TestKit\"}");
            Assert.IsTrue(resp.Contains("\"code\":\"InvalidCommand\""));
        }
    }
}
