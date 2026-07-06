using System;
using NUnit.Framework;

namespace YokiFrame.Tests
{
    /// <summary>
    /// CommandBridgeCommand envelope 字段与 IsExpired 行为测试。
    /// 覆盖主 plan 验证步骤 §1：9 字段赋值、DeadlineUtc 计算、IsExpired 边界、timeoutMs=0 永不过期。
    /// </summary>
    [TestFixture]
    public class CommandBridgeCommandEnvelopeTests
    {
        [Test]
        public void NineArgConstructor_AssignsAllFields()
        {
            var createdAt = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            var cmd = new CommandBridgeCommand("req-1", "engine-1", "src", "TestKit", "ping", "{}",
                "1.0", createdAt, 5000);

            Assert.AreEqual("req-1", cmd.RequestId);
            Assert.AreEqual("engine-1", cmd.EngineId);
            Assert.AreEqual("src", cmd.Source);
            Assert.AreEqual("TestKit", cmd.Kit);
            Assert.AreEqual("ping", cmd.Action);
            Assert.AreEqual("{}", cmd.PayloadJson);
            Assert.AreEqual("1.0", cmd.ProtocolVersion);
            Assert.AreEqual(createdAt, cmd.CreatedAtUtc);
            Assert.AreEqual(5000, cmd.TimeoutMs);
        }

        [Test]
        public void NineArgConstructor_NullPayload_DefaultsToEmptyObject()
        {
            var cmd = new CommandBridgeCommand("r", "e", "s", "k", "a", null, "1.0", DateTime.UtcNow, 0);
            Assert.AreEqual("{}", cmd.PayloadJson);
        }

        [Test]
        public void NineArgConstructor_NullStringFields_DefaultsToEmpty()
        {
            var cmd = new CommandBridgeCommand(null, null, null, null, null, null, null, DateTime.MinValue, 0);
            Assert.AreEqual(string.Empty, cmd.RequestId);
            Assert.AreEqual(string.Empty, cmd.EngineId);
            Assert.AreEqual(string.Empty, cmd.Source);
            Assert.AreEqual(string.Empty, cmd.Kit);
            Assert.AreEqual(string.Empty, cmd.Action);
            Assert.IsNull(cmd.ProtocolVersion);
        }

        [Test]
        public void SixArgConstructor_EnvelopesDefaultToCompatValues()
        {
            var cmd = new CommandBridgeCommand("r", "e", "s", "k", "a", "{}");

            Assert.IsNull(cmd.ProtocolVersion);
            Assert.AreEqual(DateTime.MinValue, cmd.CreatedAtUtc);
            Assert.AreEqual(0, cmd.TimeoutMs);
            // 缺省 envelope：DeadlineUtc = MaxValue，IsExpired 永远 false（兼容旧命令）
            Assert.AreEqual(DateTime.MaxValue, cmd.DeadlineUtc);
            Assert.IsFalse(cmd.IsExpired(DateTime.UtcNow));
        }

        [Test]
        public void TimeoutMsPositive_DeadlineIsCreatedAtPlusTimeout()
        {
            var createdAt = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            var cmd = new CommandBridgeCommand("r", "e", "s", "k", "a", "{}", "1.0", createdAt, 5000);

            Assert.AreEqual(createdAt.AddMilliseconds(5000), cmd.DeadlineUtc);
        }

        [Test]
        public void TimeoutMsZero_DeadlineIsMaxValue_NeverExpires()
        {
            var createdAt = DateTime.MinValue;
            var cmd = new CommandBridgeCommand("r", "e", "s", "k", "a", "{}", null, createdAt, 0);

            Assert.AreEqual(DateTime.MaxValue, cmd.DeadlineUtc);
            Assert.IsFalse(cmd.IsExpired(DateTime.UtcNow));
            Assert.IsFalse(cmd.IsExpired(DateTime.MaxValue));
        }

        [Test]
        public void IsExpired_True_WhenNowAfterDeadline()
        {
            var createdAt = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            var cmd = new CommandBridgeCommand("r", "e", "s", "k", "a", "{}", "1.0", createdAt, 1000);

            // deadline = 10:00:01; now = 10:00:02 → expired
            Assert.IsTrue(cmd.IsExpired(new DateTime(2026, 7, 7, 10, 0, 2, DateTimeKind.Utc)));
        }

        [Test]
        public void IsExpired_False_WhenNowEqualsDeadline()
        {
            var createdAt = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            var cmd = new CommandBridgeCommand("r", "e", "s", "k", "a", "{}", "1.0", createdAt, 1000);

            // deadline = 10:00:01; now == deadline → not expired (nowUtc > DeadlineUtc is false)
            Assert.IsFalse(cmd.IsExpired(new DateTime(2026, 7, 7, 10, 0, 1, DateTimeKind.Utc)));
        }

        [Test]
        public void IsExpired_False_WhenNowBeforeDeadline()
        {
            var createdAt = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
            var cmd = new CommandBridgeCommand("r", "e", "s", "k", "a", "{}", "1.0", createdAt, 5000);

            Assert.IsFalse(cmd.IsExpired(new DateTime(2026, 7, 7, 10, 0, 2, DateTimeKind.Utc)));
        }
    }
}
