using System;
using System.Globalization;
using System.Linq;
using Maple2.Database.Context;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using GuildQuestRewardsMigration = Maple2.Server.World.Migrations.GuildQuestRewards;

namespace Maple2.Server.Tests.Persistence;

[Explicit("Requires MAPLE2_RUN_DB_TESTS=1 and isolated validation metadata.")]
[NonParallelizable]
public class GuildQuestRewardPersistenceTests {
    private MetadataContext metadataContext = null!;
    private DbContextOptions gameOptions = null!;
    private GameStorage storage = null!;
    private QuestMetadataStorage questMetadata = null!;
    private GuildTable guildTable = null!;
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
        questMetadata = new QuestMetadataStorage(metadataContext);
        guildTable = new TableMetadataStorage(metadataContext).GuildTable;

        connection.Database = "maple2_validation_guild_reward_" + Guid.NewGuid().ToString("N")[..8];
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
    public void RewardPersistsExactlyOnceForThePersistedQuestTransition() {
        (Account memberAccount, Character member) = CreatePlayer();
        (Account nonMemberAccount, Character nonMember) = CreatePlayer();
        const int questId = 73000001;
        const long startTime = 1700000000;
        Assert.That(questMetadata.TryGet(questId, out QuestMetadata? metadata), Is.True);
        QuestMetadata definition = metadata!;
        var memberQuest = new Quest(definition) {
            State = QuestState.Started,
            StartTime = startTime,
        };
        var nonMemberQuest = new Quest(definition) {
            State = QuestState.Started,
            StartTime = startTime + 1,
        };
        long memberOwnerId = definition.Basic.Account > 0 ? memberAccount.Id : member.Id;
        long nonMemberOwnerId = definition.Basic.Account > 0 ? nonMemberAccount.Id : nonMember.Id;
        long guildId;
        Guild staleGuild;
        using (GameStorage.Request request = storage.Context()) {
            Guild? guild = request.CreateGuild("Guild" + Guid.NewGuid().ToString("N")[..8], member.Id);
            Assert.That(guild, Is.Not.Null);
            guildId = guild!.Id;
            staleGuild = guild;
            staleGuild.Members[member.Id] = new GuildMember {
                GuildId = guildId,
                Info = Info(member),
                Rank = 0,
            };
            Assert.That(request.CreateGuild(
                "Guild" + Guid.NewGuid().ToString("N")[..8], nonMember.Id), Is.Not.Null);
            Assert.That(request.CreateQuest(memberOwnerId, memberQuest), Is.Not.Null);
            Assert.That(request.CreateQuest(nonMemberOwnerId, nonMemberQuest), Is.Not.Null);
        }

        using (GameStorage.Request request = storage.Context()) {
            GuildQuestRewardResult wrongIdentity = request.AwardGuildQuestReward(
                guildId, member.Id, questId, startTime + 1, 1);
            Assert.That(wrongIdentity.Status, Is.EqualTo(GuildQuestRewardStatus.InvalidRequest));

            GuildQuestRewardResult first = request.AwardGuildQuestReward(
                guildId, member.Id, questId, startTime, 1);
            Assert.That(first.Status, Is.EqualTo(GuildQuestRewardStatus.Applied));
            Assert.That(first.Experience, Is.EqualTo(120));
            Assert.That(first.Funds, Is.EqualTo(20000));
        }
        using (GameStorage.Request request = storage.Context()) {
            staleGuild.Notice = "stale cache save";
            Assert.That(request.SaveGuild(staleGuild), Is.True);
            Guild? persisted = request.GetGuild(guildId);
            Assert.That(persisted?.Experience, Is.EqualTo(120));
            Assert.That(persisted?.Funds, Is.EqualTo(20000));
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.DeleteQuest(memberOwnerId, questId), Is.False);
            Assert.That(request.GetQuests(memberOwnerId)[questId].State, Is.EqualTo(QuestState.Started));
        }
        using (GameStorage.Request request = storage.Context()) {
            memberQuest.State = QuestState.Completed;
            memberQuest.CompletionCount = 1;
            memberQuest.EndTime = startTime + 10;
            Assert.That(request.SaveQuests(memberOwnerId, [memberQuest]), Is.True);
        }
        using (GameStorage.Request request = storage.Context()) {
            GuildQuestRewardResult retry = request.AwardGuildQuestReward(
                guildId, member.Id, questId, startTime, 1);
            Assert.That(retry.Status, Is.EqualTo(GuildQuestRewardStatus.AlreadyApplied));
            Assert.That(retry.Experience, Is.EqualTo(120));
            Assert.That(retry.Funds, Is.EqualTo(20000));

            GuildQuestRewardResult rejected = request.AwardGuildQuestReward(
                guildId, nonMember.Id, questId, nonMemberQuest.StartTime, 1);
            Assert.That(rejected.Status, Is.EqualTo(GuildQuestRewardStatus.NotMember));
        }

        const long secondStartTime = startTime + 100;
        memberQuest.State = QuestState.Started;
        memberQuest.StartTime = secondStartTime;
        memberQuest.EndTime = 0;
        memberQuest.CompletionCount = 0;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SaveQuests(memberOwnerId, [memberQuest]), Is.True);
            GuildQuestRewardResult second = request.AwardGuildQuestReward(
                guildId, member.Id, questId, secondStartTime, 1);
            Assert.That(second.Status, Is.EqualTo(GuildQuestRewardStatus.Applied));
            (int expectedExperience, int expectedFunds) = guildTable.AddProgress(120, 20000, 120, 20000);
            Assert.That(second.Experience, Is.EqualTo(expectedExperience));
            Assert.That(second.Funds, Is.EqualTo(expectedFunds));
        }
        using (GameStorage.Request request = storage.Context()) {
            memberQuest.State = QuestState.Completed;
            memberQuest.CompletionCount = 1;
            memberQuest.EndTime = secondStartTime + 10;
            Assert.That(request.SaveQuests(memberOwnerId, [memberQuest]), Is.True);
        }
        using (GameStorage.Request request = storage.Context()) {
            GuildQuestRewardResult secondRetry = request.AwardGuildQuestReward(
                guildId, member.Id, questId, secondStartTime, 1);
            Assert.That(secondRetry.Status, Is.EqualTo(GuildQuestRewardStatus.AlreadyApplied));
        }
    }

    [Test]
    public void CheckInProgressSurvivesAStaleGuildSave() {
        (_, Character member) = CreatePlayer();
        Guild staleGuild;
        using (GameStorage.Request request = storage.Context()) {
            staleGuild = request.CreateGuild(
                "Guild" + Guid.NewGuid().ToString("N")[..8], member.Id)!;
            staleGuild.Members[member.Id] = new GuildMember {
                GuildId = staleGuild.Id,
                Info = Info(member),
                Rank = 0,
            };
        }

        GuildCheckInResult checkIn;
        using (GameStorage.Request request = storage.Context()) {
            checkIn = request.CheckInGuild(staleGuild.Id, member.Id);
            Assert.That(checkIn.Success, Is.True);
        }
        using (GameStorage.Request request = storage.Context()) {
            staleGuild.Notice = "stale cache save";
            Assert.That(request.SaveGuild(staleGuild), Is.True);
            Guild? persisted = request.GetGuild(staleGuild.Id);
            Assert.That(persisted?.Experience, Is.EqualTo(checkIn.Experience));
            Assert.That(persisted?.Funds, Is.EqualTo(checkIn.Funds));
        }
    }

    private (Account, Character) CreatePlayer() {
        using GameStorage.Request request = storage.Context();
        Account account = request.CreateAccount(new Account {
            Username = "guild" + Guid.NewGuid().ToString("N")[..12],
        }, Guid.NewGuid().ToString("N"));
        var value = new Character {
            AccountId = account.Id,
            Name = "Guild" + Guid.NewGuid().ToString("N")[..7],
            MapId = 2000001,
            Mastery = new Mastery(),
        };
        value.ReturnMaps.Push(value.MapId);
        Character? character = request.CreateCharacter(value);
        if (character == null || !request.InitNewCharacter(character.Id, new Unlock())) {
            throw new InvalidOperationException("Failed to initialize a synthetic test character.");
        }
        return (account, character);
    }

    private static PlayerInfo Info(Character character) {
        return new PlayerInfo(
            new CharacterInfo(character.AccountId, character.Id, character.Name, "", "",
                character.Gender, character.Job, character.Level),
            "Test home", default, []);
    }

    private static string Required(string name) {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Missing persistence-test setting {name}.");
    }
}
