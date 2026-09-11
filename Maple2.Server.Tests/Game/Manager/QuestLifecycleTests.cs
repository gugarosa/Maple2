using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Session;
using Serilog;
using NetworkSession = Maple2.Server.Core.Network.Session;

namespace Maple2.Server.Tests.Game.Manager;

public class QuestLifecycleTests {
    [Test]
    public void ClientExpiryCannotDeleteCompletedUntimedOrTimedProgress() {
        Quest completed = QuestManager.CreateStartedQuest(Metadata(1), null, 1);
        completed.State = QuestState.Completed;
        completed.CompletionCount = 2;
        completed.EndTime = 10;
        Quest untimed = QuestManager.CreateStartedQuest(Metadata(2), null, 1);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Quest timed = QuestManager.CreateStartedQuest(Metadata(3, repeatable: 2, period: "5", account: 1), null, now);
        (QuestManager manager, var packets) = CreateManager(completed, untimed, timed);
        using (packets) {
            manager.ReconcileExpiry([1, 2, 3, 1, int.MaxValue]);

            foreach (Quest quest in new[] { completed, untimed, timed }) {
                Assert.That(manager.TryGetQuest(quest.Id, out Quest? preserved), Is.True);
                Assert.That(preserved, Is.SameAs(quest));
            }
            Assert.That(completed.State, Is.EqualTo(QuestState.Completed));
            Assert.That(completed.CompletionCount, Is.EqualTo(2));
            Assert.That(completed.EndTime, Is.EqualTo(10));
            Assert.That(timed.StartTime, Is.EqualTo(now));
            Assert.That(untimed.State, Is.EqualTo(QuestState.Started));
            Assert.That(packets.Count, Is.EqualTo(2));
            byte[][] sent = packets.Select(entry => entry.Packet).ToArray();
            Assert.That(sent[0][2], Is.EqualTo(22)); // Authoritative quest states
            Assert.That(BitConverter.ToInt32(sent[0], 3), Is.EqualTo(3));
            Assert.That(sent[1][2], Is.EqualTo(7)); // No confirmed expirations
            Assert.That(BitConverter.ToInt32(sent[1], 3), Is.Zero);
        }
    }

    [Test]
    public void AbandonCannotRemoveCompletedProgress() {
        Quest quest = QuestManager.CreateStartedQuest(Metadata(1), null, 1);
        quest.State = QuestState.Completed;
        (QuestManager manager, var packets) = CreateManager(quest);
        using (packets) {
            Assert.That(manager.Abandon(quest), Is.False);
            Assert.That(manager.TryGetQuest(quest.Id, out Quest? preserved), Is.True);
            Assert.That(preserved, Is.SameAs(quest));
        }
    }

    [TestCase(0, "10080")]
    [TestCase(1, "")]
    [TestCase(2, "5")]
    [TestCase(2, "1440")]
    [TestCase(3, "fri")]
    public void UnverifiedSchedulesDoNotAuthorizePlayerReplays(int repeatable, string period) {
        Quest quest = QuestManager.CreateStartedQuest(Metadata(1, repeatable, period), null, 1);
        quest.State = QuestState.Completed;
        quest.CompletionCount = 1;
        quest.EndTime = 2;
        (QuestManager manager, var packets) = CreateManager(quest);
        using (packets) {
            Assert.That(manager.CanStart(quest.Metadata), Is.False);
            Assert.That(manager.Start(quest.Id), Is.EqualTo(QuestError.s_quest_error_accept_fail));
            Assert.That(quest.State, Is.EqualTo(QuestState.Completed));
            Assert.That(quest.CompletionCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void FactionGradeRequirementsAreNotSilentlySkipped() {
        (QuestManager manager, var packets) = CreateManager();
        using (packets) {
            Assert.That(manager.CanStart(Metadata(1, fameGrade: 2)), Is.False);
        }
    }

    [Test]
    public void RestartPreparationPreservesHistoryWithoutMutatingThePreviousQuest() {
        QuestMetadata metadata = Metadata(1, repeatable: 2, period: "5") with {
            SummonPortal = new QuestSummonPortal(2000043, 1),
        };
        Quest previous = QuestManager.CreateStartedQuest(metadata, null, 1);
        previous.State = QuestState.Completed;
        previous.CompletionCount = 4;
        previous.Track = false;
        previous.EndTime = 10;

        Quest restarted = QuestManager.CreateStartedQuest(metadata, previous, 20);
        Assert.That(restarted.Metadata, Is.SameAs(metadata));
        Assert.That(restarted.State, Is.EqualTo(QuestState.Started));
        Assert.That(restarted.StartTime, Is.EqualTo(20));
        Assert.That(restarted.EndTime, Is.Zero);
        Assert.That(restarted.CompletionCount, Is.EqualTo(4));
        Assert.That(restarted.Track, Is.False);
        Assert.That(previous.State, Is.EqualTo(QuestState.Completed));
        Assert.That(previous.EndTime, Is.EqualTo(10));
    }

    [TestCase(0, "5", 299, false)]
    [TestCase(2, "5", 300, true)]
    [TestCase(2, "5", -1, false)]
    [TestCase(0, "1440", 86399, false)]
    [TestCase(0, "1440", 86400, true)]
    [TestCase(2, "1440", 86400, true)]
    [TestCase(0, "10080", 604799, false)]
    [TestCase(0, "10080", 604800, true)]
    [TestCase(2, "", 604800, false)]
    [TestCase(2, "0", 604800, false)]
    [TestCase(2, "-5", 604800, false)]
    [TestCase(2, "unknown", 604800, false)]
    [TestCase(2, "9223372036854775807", 604800, false)]
    public void NumericPeriodsExpireStartedQuestsFromAcceptance(int repeatable, string period, long age, bool expected) {
        const long start = 1789086077;
        Quest quest = QuestManager.CreateStartedQuest(Metadata(1, repeatable, period), null, start);
        quest.EndTime = start + 10000000;
        Assert.That(quest.IsExpired(start + age), Is.EqualTo(expected));
    }

    [TestCase("2026-09-10T23:59:00Z", "2026-09-10T23:59:59Z", false)]
    [TestCase("2026-09-10T23:59:00Z", "2026-09-11T00:00:00Z", true)]
    [TestCase("2026-09-11T00:00:00Z", "2026-09-11T00:00:00Z", false)]
    [TestCase("2026-09-11T00:00:00Z", "2026-09-18T00:00:00Z", true)]
    public void WeeklyPeriodsUseTheNextFridayUtcBoundary(string accepted, string current, bool expected) {
        Quest quest = QuestManager.CreateStartedQuest(Metadata(1, 3, "fri"), null,
            DateTimeOffset.Parse(accepted).ToUnixTimeSeconds());
        Assert.That(quest.IsExpired(DateTimeOffset.Parse(current).ToUnixTimeSeconds()), Is.EqualTo(expected));
    }

    [TestCase(QuestState.Completed)]
    [TestCase(QuestState.None)]
    public void InactiveAndCompletedRecordsNeverExpire(QuestState state) {
        Quest quest = QuestManager.CreateStartedQuest(Metadata(1, 2, "5"), null, 1);
        quest.State = state;
        Assert.That(quest.IsExpired(10000000), Is.False);
    }

    [Test]
    public void ExpiredRecordsKeepHistoryWithoutAppearingActive() {
        Quest quest = QuestManager.CreateStartedQuest(Metadata(1, 2, "5"), null, 1);
        quest.State = QuestState.None;
        quest.CompletionCount = 4;
        (QuestManager manager, var packets) = CreateManager(quest);
        using (packets) {
            Assert.That(manager.TryGetQuest(quest.Id, out _), Is.False);
            manager.ReconcileExpiry([quest.Id]);
            Assert.That(quest.CompletionCount, Is.EqualTo(4));
            byte[] packet = packets.Single().Packet;
            Assert.That(packet[2], Is.EqualTo(7));
            Assert.That(BitConverter.ToInt32(packet, 3), Is.EqualTo(1));
            Assert.That(BitConverter.ToInt32(packet, 7), Is.EqualTo(quest.Id));
        }
    }

    private static QuestMetadata Metadata(int id, int repeatable = 0, string period = "", int account = 0, int fameGrade = 0) {
        var reward = new QuestMetadataReward(0, 0, ExpType.none, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0);
        return new QuestMetadata(id, "Quest",
            new QuestMetadataBasic(0, QuestType.GuildQuest, account, 0, true, "", false, false, false, 0, 0,
                [], [], repeatable, period, "", ""),
            new QuestMetadataRequire(0, 0, [], [], [], 0, (0, 0), 0, "", fameGrade),
            reward, reward, new QuestRemoteAccept(QuestRemoteType.None, 0),
            new QuestRemoteComplete(QuestRemoteType.None, 0, false),
            new QuestMetadataGoToNpc(false, 0, 0), new QuestMetadataGoToDungeon(QuestState.None, 0, 0),
            null, null, null, QuestEventMissionType.none, []);
    }

    private static (QuestManager, BlockingCollection<(byte[] Packet, int Length)>) CreateManager(params Quest[] quests) {
        var session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        GC.SuppressFinalize(session);
        var packets = new BlockingCollection<(byte[], int)>();
        Set<NetworkSession>(session, "sendQueue", packets);
        Set<NetworkSession>(session, "lastSentPackets", new ConcurrentDictionary<SendOp, byte[]>());
        Set<NetworkSession>(session, "Logger", Log.Logger);
        session.Item = (ItemManager) RuntimeHelpers.GetUninitializedObject(typeof(ItemManager));

        var manager = (QuestManager) RuntimeHelpers.GetUninitializedObject(typeof(QuestManager));
        Set<QuestManager>(manager, "session", session);
        Set<QuestManager>(manager, "logger", Log.Logger);
        Set<QuestManager>(manager, "accountValues", quests.Where(quest => quest.Metadata.Basic.Account > 0).ToDictionary(quest => quest.Id));
        Set<QuestManager>(manager, "characterValues", quests.Where(quest => quest.Metadata.Basic.Account == 0).ToDictionary(quest => quest.Id));
        session.Quest = manager;
        return (manager, packets);
    }

    private static void Set<T>(object target, string name, object value) {
        FieldInfo field = typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field {typeof(T).Name}.{name}.");
        field.SetValue(target, value);
    }
}
