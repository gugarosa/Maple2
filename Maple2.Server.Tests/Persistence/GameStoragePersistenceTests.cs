using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Maple2.Database.Context;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Game.Dungeon;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Maple2.Server.Tests.Persistence;

[Explicit("Requires MAPLE2_RUN_DB_TESTS=1 and isolated validation metadata.")]
[NonParallelizable]
public class GameStoragePersistenceTests {
    private MetadataContext metadataContext = null!;
    private DbContextOptions gameOptions = null!;
    private GameStorage storage = null!;
    private ItemMetadata itemMetadata = null!;
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

        connection.Database = "maple2_validation_persistence_" + Guid.NewGuid().ToString("N")[..12];
        gameOptions = new DbContextOptionsBuilder().UseMySql(connection.ConnectionString, version).Options;
        using (var context = new Ms2Context(gameOptions)) {
            if (context.Database.CanConnect()) {
                throw new InvalidOperationException("Refusing to reuse an existing persistence-test database.");
            }
            context.Database.EnsureCreated();
            databaseCreated = true;
        }

        storage = new GameStorage(gameOptions, items,
            new MapMetadataStorage(metadataContext),
            new AchievementMetadataStorage(metadataContext),
            new QuestMetadataStorage(metadataContext),
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
