using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maple2.Database.Context;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Game.Dungeon;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Manager.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using GuildQuestRewardsMigration = Maple2.Server.World.Migrations.GuildQuestRewards;

namespace Maple2.Server.Tests.Persistence;

[Explicit("Requires MAPLE2_RUN_DB_TESTS=1 and isolated validation metadata.")]
[NonParallelizable]
public class GameStoragePersistenceTests {
    private MetadataContext metadataContext = null!;
    private DbContextOptions gameOptions = null!;
    private GameStorage storage = null!;
    private ItemMetadata itemMetadata = null!;
    private QuestMetadataStorage questMetadata = null!;
    private bool databaseCreated;

    [OneTimeSetUp]
    public void CreateIsolatedDatabase() {
        if (Environment.GetEnvironmentVariable("MAPLE2_RUN_DB_TESTS") != "1") {
            Assert.Ignore("Set MAPLE2_RUN_DB_TESTS=1 to run isolated persistence tests.");
        }

        string metadataDatabase = Required("DATA_DB_NAME");
        if (!metadataDatabase.StartsWith("maple2_validation_", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Persistence tests require a maple2_validation_ metadata database.");
        }

        var connection = new MySqlConnectionStringBuilder {
            Server = Required("DB_IP"),
            Port = uint.Parse(Required("DB_PORT"), CultureInfo.InvariantCulture),
            UserID = Required("DB_USER"),
            Password = Required("DB_PASSWORD"),
            Database = metadataDatabase,
            OldGuids = true,
        };
        ServerVersion version = ServerVersion.AutoDetect(connection.ConnectionString);
        metadataContext = new MetadataContext(new DbContextOptionsBuilder()
            .UseMySql(connection.ConnectionString, version).Options);
        var items = new ItemMetadataStorage(metadataContext);
        if (!items.TryGet(20000027, out ItemMetadata? potion)) {
            throw new InvalidOperationException("The validation metadata must contain the ordinary white potion.");
        }
        itemMetadata = potion;
        questMetadata = new QuestMetadataStorage(metadataContext);

        connection.Database = "maple2_validation_persistence_" + Guid.NewGuid().ToString("N")[..12];
        gameOptions = new DbContextOptionsBuilder().UseMySql(connection.ConnectionString, version,
            mysql => mysql.MigrationsAssembly(typeof(GuildQuestRewardsMigration).Assembly.GetName().Name)).Options;
        using (var context = new Ms2Context(gameOptions)) {
            if (context.Database.CanConnect()) {
                throw new InvalidOperationException("Refusing to reuse an existing persistence-test database.");
            }
            databaseCreated = true;
            context.Database.Migrate();
        }

        storage = new GameStorage(gameOptions, items,
            new MapMetadataStorage(metadataContext),
            new AchievementMetadataStorage(metadataContext),
            questMetadata,
            new TableMetadataStorage(metadataContext),
            new ServerTableMetadataStorage(metadataContext),
            NullLogger<GameStorage>.Instance,
            new FunctionCubeMetadataStorage(metadataContext));
    }

    [OneTimeTearDown]
    public void RemoveIsolatedDatabase() {
        if (databaseCreated) {
            using var context = new Ms2Context(gameOptions);
            context.Database.EnsureDeleted();
        }
        metadataContext?.Dispose();
    }

    [Test]
    public void AccountMigrationMergesCharacterProgressExactlyOnce() {
        (Account account, Character first, Character second) = CreatePlayers();
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        const int dungeonId = 98765001;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.CreateDungeonRecord(Record(2), first.Id, false), Is.Not.Null);
            Assert.That(request.CreateDungeonRecord(Record(3), second.Id, false), Is.Not.Null);
        }

        using (GameStorage.Request request = storage.Context()) {
            DungeonRecord? merged = request.CreateDungeonRecord(new DungeonRecord(dungeonId, true), account.Id, true);
            Assert.That(merged, Is.Not.Null);
            Assert.That(merged!.UnionClears, Is.EqualTo(5));
            Assert.That(merged.UnionSubClears, Is.EqualTo(5));
            Assert.That(merged.TotalClears, Is.EqualTo(5));
        }

        using (GameStorage.Request request = storage.Context()) {
            DungeonRecord? repeated = request.CreateDungeonRecord(new DungeonRecord(dungeonId, true), account.Id, true);
            Assert.That(repeated!.TotalClears, Is.EqualTo(5));
            Assert.That(request.GetDungeonRecords(first.Id, false), Is.Empty);
            Assert.That(request.GetDungeonRecords(second.Id, false), Is.Empty);
            Assert.That(request.GetDungeonRecords(account.Id, true)[dungeonId].AccountWide, Is.True);
        }

        DungeonRecord Record(byte count) => new(dungeonId) {
            UnionClears = count,
            UnionSubClears = count,
            TotalClears = count,
            UnionCooldownTimestamp = now + 7 * 86400,
            UnionSubCooldownTimestamp = now + 86400,
        };
    }

    [Test]
    public void RankMailClaimPersistsItemsAndDoesNotDuplicateOnRetry() {
        (_, Character character, _) = CreatePlayers();
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var reward = new DungeonRankReward(98765002) { RankClaimed = 3, UpdatedTimestamp = now };
        var mails = new List<(int Rank, Mail Mail)> {
            (1, RewardMail(character.Id)),
            (2, RewardMail(character.Id)),
            (3, RewardMail(character.Id)),
        };
        long week = DungeonRankRewardSelector.GetWeekStartTimestamp(now);
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.ClaimDungeonRankRewards(character.Id, reward, week, mails), Is.Not.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.ClaimDungeonRankRewards(character.Id, reward, week, mails), Is.Not.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            ICollection<Mail> persisted = request.GetAllMail(character.Id);
            Assert.That(persisted, Has.Count.EqualTo(3));
            Assert.That(persisted.All(mail => mail.Items.Count == 1 && mail.Items[0].Id == 20000027), Is.True);
        }
    }

    [Test]
    public void InvalidMailDoesNotConsumeTheRankEntitlement() {
        (_, Character character, _) = CreatePlayers();
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var reward = new DungeonRankReward(98765003) { RankClaimed = 1, UpdatedTimestamp = now };
        long week = DungeonRankRewardSelector.GetWeekStartTimestamp(now);
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.ClaimDungeonRankRewards(character.Id, reward, week,
                [(1, new Mail(30) { ReceiverId = character.Id, Type = MailType.System })]), Is.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.GetAllMail(character.Id), Is.Empty);
            Assert.That(request.ClaimDungeonRankRewards(character.Id, reward, week,
                [(1, RewardMail(character.Id))]), Is.Not.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.GetAllMail(character.Id), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void ClubBuffSelectionSurvivesAFreshStorageRequest() {
        (_, Character first, Character second) = CreatePlayers();
        PlayerInfo[] members = [Info(first), Info(second)];
        var provider = new PlayerInfos(members);
        long clubId;
        using (GameStorage.Request request = storage.Context()) {
            var club = request.CreateClub(provider, "Club" + Guid.NewGuid().ToString("N")[..8], first.Id, members.ToList());
            Assert.That(club, Is.Not.Null);
            clubId = club!.Id;
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SaveClubBuff(clubId, 3), Is.True);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.GetClub(provider, clubId)?.BuffId, Is.EqualTo(3));
        }

        static PlayerInfo Info(Character character) => new(
            new CharacterInfo(character.AccountId, character.Id, character.Name, "", "",
                character.Gender, character.Job, character.Level),
            "Test home", default, []);
    }

    [Test]
    public void ActivationPersistsQuestAndBothInventoryOwnersWithoutDuplicatingRetries() {
        (Account account, Character character, _) = CreatePlayers();
        Quest quest = StartedQuest(93000123, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var items = new ItemMetadataStorage(metadataContext);
        Assert.That(items.TryGet(50200094, out ItemMetadata? furnishing), Is.True);
        Item[] additions = [
            new Item(itemMetadata, amount: 3) { Slot = 2 },
            new Item(furnishing!) { Slot = 0, Group = ItemGroup.Furnishing },
        ];

        using (GameStorage.Request request = storage.Context()) {
            List<Item>? saved = request.ActivateQuest(account.Id, character.Id, quest, null, additions, _ => true);
            Assert.That(saved, Has.Count.EqualTo(2));
            Assert.That(saved!.All(item => item.Uid > 0), Is.True);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.ActivateQuest(account.Id, character.Id, quest, null, additions, _ => true), Is.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            long ownerId = quest.Metadata.Basic.Account > 0 ? account.Id : character.Id;
            Quest persisted = request.GetQuests(ownerId)[quest.Id];
            Assert.That(persisted.State, Is.EqualTo(QuestState.Started));
            Assert.That(persisted.StartTime, Is.EqualTo(quest.StartTime));
            Assert.That(persisted.Conditions.Count, Is.EqualTo(quest.Metadata.Conditions.Length));
            Item bag = request.GetAllItems(character.Id).Single();
            Assert.That(bag.Amount, Is.EqualTo(3));
            Assert.That(bag.Slot, Is.EqualTo(2));
            Item cube = request.GetAllItems(account.Id).Single();
            Assert.That(cube.Group, Is.EqualTo(ItemGroup.Furnishing));
            Assert.That(cube.Id, Is.EqualTo(50200094));
        }
    }

    [Test]
    public void FailedActivationLeavesNeitherQuestNorAcceptanceItems() {
        (Account account, Character character, _) = CreatePlayers();
        Quest quest = StartedQuest(93000123, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Item[] additions = [
            new Item(itemMetadata) { Slot = 0 },
            new Item(itemMetadata) { Slot = 1, Appearance = null },
        ];
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.ActivateQuest(account.Id, character.Id, quest, null, additions, _ => true), Is.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            long ownerId = quest.Metadata.Basic.Account > 0 ? account.Id : character.Id;
            Assert.That(request.GetQuests(ownerId), Is.Empty);
            Assert.That(request.GetAllItems(character.Id), Is.Empty);
        }
    }

    [Test]
    public void RestartCommitsFreshAcceptanceWithoutLosingCompletionHistory() {
        (Account account, Character character, _) = CreatePlayers();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Quest previous = StartedQuest(93000123, now - 600);
        previous.State = QuestState.Completed;
        previous.CompletionCount = 3;
        previous.EndTime = now - 300;
        previous.Track = false;
        foreach (Quest.Condition condition in previous.Conditions.Values) {
            condition.Counter = 1;
        }
        long ownerId = previous.Metadata.Basic.Account > 0 ? account.Id : character.Id;
        Item existing;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.CreateQuest(ownerId, previous), Is.Not.Null);
            existing = request.CreateItem(character.Id, new Item(itemMetadata, amount: 2) { Slot = 0 })!;
        }
        var collection = new ItemCollection(3) { [0] = existing };
        Item[] planned = collection.PlanAdd([new Item(itemMetadata, amount: 3)])!;
        Quest restarted = QuestManager.CreateStartedQuest(previous.Metadata, previous, now);

        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.ActivateQuest(account.Id, character.Id, restarted, previous,
                [.. planned, new Item(itemMetadata) { Slot = 1, Appearance = null }], db => db.SaveItems(character.Id, existing)), Is.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.GetQuests(ownerId)[previous.Id].State, Is.EqualTo(QuestState.Completed));
            Assert.That(request.GetItem(existing.Uid)!.Amount, Is.EqualTo(2));
            List<Item>? saved = request.ActivateQuest(account.Id, character.Id, restarted, previous, planned,
                db => db.SaveItems(character.Id, existing));
            Assert.That(saved, Has.Count.EqualTo(1));
            collection.ApplyAdded(saved![0]);
        }
        using (GameStorage.Request request = storage.Context()) {
            Quest persisted = request.GetQuests(ownerId)[previous.Id];
            Assert.That(persisted.State, Is.EqualTo(QuestState.Started));
            Assert.That(persisted.CompletionCount, Is.EqualTo(3));
            Assert.That(persisted.EndTime, Is.Zero);
            Assert.That(persisted.Track, Is.False);
            Assert.That(persisted.Conditions.Values.All(condition => condition.Counter == 0), Is.True);
            Assert.That(request.GetItem(existing.Uid)!.Amount, Is.EqualTo(5));
        }
        Assert.That(collection.Get(existing.Uid), Is.SameAs(existing));
        Assert.That(existing.Amount, Is.EqualTo(5));
        Assert.That(previous.State, Is.EqualTo(QuestState.Completed));
        Assert.That(previous.Conditions.Values.All(condition => condition.Counter == 1), Is.True);
        Assert.That(restarted.Metadata.SummonPortal, Is.Not.Null);
        Assert.That(restarted.Metadata.AcceptReward.EssentialItem, Is.Not.Empty);
    }

    [Test]
    public void ActivationRejectsACharacterFromAnotherAccount() {
        (Account account, _, _) = CreatePlayers();
        (_, Character other, _) = CreatePlayers();
        Quest quest = StartedQuest(93000123, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using GameStorage.Request request = storage.Context();
        Assert.That(request.ActivateQuest(account.Id, other.Id, quest, null,
            [new Item(itemMetadata) { Slot = 0 }], _ => true), Is.Null);
        Assert.That(request.GetQuests(other.Id), Is.Empty);
        Assert.That(request.GetAllItems(other.Id), Is.Empty);
    }

    [Test]
    public void ActivationCommitsFreedInventorySlotsAndRollsBackPendingSavesOnFailure() {
        (Account account, Character character, _) = CreatePlayers();
        Quest quest = StartedQuest(93000123, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Item discarded;
        using (GameStorage.Request request = storage.Context()) {
            discarded = request.CreateItem(character.Id, new Item(itemMetadata) { Slot = 0 })!;
        }
        Item[] planned = new ItemCollection(1).PlanAdd([new Item(itemMetadata, amount: 3)])!;

        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.ActivateQuest(account.Id, character.Id, quest, null, planned, db => {
                Assert.That(db.SaveItems(0, discarded), Is.True);
                return false;
            }), Is.Null);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.GetAllItems(character.Id).Single().Uid, Is.EqualTo(discarded.Uid));
            Assert.That(request.GetQuests(quest.Metadata.Basic.Account > 0 ? account.Id : character.Id), Is.Empty);
            Assert.That(request.ActivateQuest(account.Id, character.Id, quest, null, planned,
                db => db.SaveItems(0, discarded)), Has.Count.EqualTo(1));
        }
        using (GameStorage.Request request = storage.Context()) {
            Item onlyItem = request.GetAllItems(character.Id).Single();
            Assert.That(onlyItem.Uid, Is.Not.EqualTo(discarded.Uid));
            Assert.That(onlyItem.Slot, Is.Zero);
            Assert.That(onlyItem.Amount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task ConcurrentActivationGrantsOnlyOneAcceptanceBatch() {
        (Account account, Character character, _) = CreatePlayers();
        Quest quest = StartedQuest(93000123, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Task<List<Item>?>[] attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(() => {
            using GameStorage.Request request = storage.Context();
            return request.ActivateQuest(account.Id, character.Id, quest, null,
                [new Item(itemMetadata) { Slot = 0 }], _ => true);
        })).ToArray();

        List<Item>?[] results = await Task.WhenAll(attempts);
        Assert.That(results.Count(result => result != null), Is.EqualTo(1));
        using GameStorage.Request db = storage.Context();
        Assert.That(db.GetAllItems(character.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ActivationUsesTheSameLockOrderAsSessionSaving() {
        (Account account, Character character, _) = CreatePlayers();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Quest previous = StartedQuest(93000123, now - 600);
        previous.State = QuestState.Completed;
        previous.CompletionCount = 1;
        previous.EndTime = now - 300;
        long ownerId = previous.Metadata.Basic.Account > 0 ? account.Id : character.Id;
        Item existing;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.CreateQuest(ownerId, previous), Is.Not.Null);
            existing = request.CreateItem(character.Id, new Item(itemMetadata) { Slot = 0 })!;
        }

        using var itemSaved = new ManualResetEventSlim();
        using var activationFlushing = new ManualResetEventSlim();
        Task save = Task.Run(() => {
            using GameStorage.Request request = storage.Context();
            request.BeginTransaction();
            Assert.That(request.SaveItems(character.Id, existing), Is.True);
            itemSaved.Set();
            Assert.That(activationFlushing.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(request.SaveQuests(ownerId, [previous]), Is.True);
            Assert.That(request.Commit(), Is.True);
        });
        Task<List<Item>?> activate = Task.Run(() => {
            Assert.That(itemSaved.Wait(TimeSpan.FromSeconds(10)), Is.True);
            using GameStorage.Request request = storage.Context();
            return request.ActivateQuest(account.Id, character.Id,
                QuestManager.CreateStartedQuest(previous.Metadata, previous, now), previous,
                [new Item(itemMetadata) { Slot = 1 }], db => {
                    activationFlushing.Set();
                    return db.SaveItems(character.Id, existing);
                });
        });

        await Task.WhenAll(save, activate);
        Assert.That(await activate, Has.Count.EqualTo(1));
    }

    private Quest StartedQuest(int questId, long now) {
        Assert.That(questMetadata.TryGet(questId, out QuestMetadata? metadata), Is.True);
        return QuestManager.CreateStartedQuest(metadata!, null, now);
    }

    private (Account, Character, Character) CreatePlayers() {
        using GameStorage.Request request = storage.Context();
        Account account = request.CreateAccount(new Account {
            Username = "rank" + Guid.NewGuid().ToString("N")[..12],
        }, Guid.NewGuid().ToString("N"));
        Character first = Create("a");
        Character second = Create("b");
        return (account, first, second);

        Character Create(string suffix) {
            var value = new Character {
                AccountId = account.Id,
                Name = "Rank" + Guid.NewGuid().ToString("N")[..6] + suffix,
                MapId = 2000001,
                Mastery = new Mastery(),
            };
            value.ReturnMaps.Push(value.MapId);
            Character? character = request.CreateCharacter(value);
            if (character == null || !request.InitNewCharacter(character.Id, new Unlock())) {
                throw new InvalidOperationException("Failed to initialize a synthetic test character.");
            }
            return character;
        }
    }

    private Mail RewardMail(long characterId) {
        var mail = new Mail(30) { ReceiverId = characterId, Type = MailType.System, Content = "100" };
        mail.Items.Add(new Item(itemMetadata));
        return mail;
    }

    private static string Required(string name) {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Missing persistence-test setting {name}.");
    }

    private sealed class PlayerInfos(IEnumerable<PlayerInfo> players) : IPlayerInfoProvider {
        private readonly Dictionary<long, PlayerInfo> values = players.ToDictionary(player => player.CharacterId);

        public PlayerInfo? GetPlayerInfo(long id) => values.GetValueOrDefault(id);
    }
}
