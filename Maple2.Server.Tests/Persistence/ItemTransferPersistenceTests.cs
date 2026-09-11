using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Maple2.Database.Context;
using Maple2.Database.Storage;
using Maple2.Model;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Game.Ugc;
using Maple2.Model.Metadata;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.PacketHandlers;
using Maple2.Server.Game.Session;
using Maple2.Tools.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Serilog;
using Migration = Maple2.Server.World.Migrations.GuildQuestRewards;
using NetworkSession = Maple2.Server.Core.Network.Session;
using WorldClient = Maple2.Server.World.Service.World.WorldClient;

namespace Maple2.Server.Tests.Persistence;

[Explicit("Requires MAPLE2_RUN_DB_TESTS=1 and isolated validation metadata.")]
[NonParallelizable]
public class ItemTransferPersistenceTests {
    private MetadataContext metadata = null!;
    private DbContextOptions options = null!;
    private GameStorage storage = null!;
    private ItemMetadataStorage items = null!;
    private ItemMetadata potion = null!;
    private ItemMetadata blueprint = null!;
    private bool created;

    [OneTimeSetUp]
    public void CreateIsolatedDatabase() {
        if (Environment.GetEnvironmentVariable("MAPLE2_RUN_DB_TESTS") != "1") {
            Assert.Ignore("Set MAPLE2_RUN_DB_TESTS=1 to run isolated persistence tests.");
        }
        string metadataDatabase = Required("DATA_DB_NAME");
        if (!metadataDatabase.StartsWith("maple2_validation_", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Item transfer tests require a maple2_validation_ metadata database.");
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
        metadata = new MetadataContext(new DbContextOptionsBuilder().UseMySql(connection.ConnectionString, version).Options);
        items = new ItemMetadataStorage(metadata);
        if (!items.TryGet(20000027, out ItemMetadata? potionMetadata) || potionMetadata.Property.SlotMax < 12 ||
            !items.TryGet(Constant.BlueprintId, out ItemMetadata? blueprintMetadata)) {
            throw new InvalidOperationException("Validation metadata must contain potion 20000027 and blueprint 35200000.");
        }
        potion = potionMetadata;
        blueprint = blueprintMetadata;
        connection.Database = "maple2_validation_transfers_" + Guid.NewGuid().ToString("N")[..12];
        options = new DbContextOptionsBuilder().UseMySql(connection.ConnectionString, version,
            mysql => mysql.MigrationsAssembly(typeof(Migration).Assembly.GetName().Name)).Options;
        using (var context = new Ms2Context(options)) {
            if (context.Database.CanConnect()) {
                throw new InvalidOperationException("Refusing to reuse an existing item-transfer test database.");
            }
            created = true;
            context.Database.Migrate();
        }
        storage = NewStorage(options);
    }

    [OneTimeTearDown]
    public void RemoveIsolatedDatabase() {
        if (created) {
            using var context = new Ms2Context(options);
            context.Database.EnsureDeleted();
        }
        metadata?.Dispose();
    }

    [Test]
    public void RejectedOwnedAdditionDoesNotDiscardTheBankSource() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item existing = Add(session, new Item(potion, amount: potion.Property.SlotMax));
        Item source = Seed(session.AccountId, new Item(potion, amount: 5));

        Assert.That(session.Item.Inventory.Add(source, commit: true), Is.False);
        Assert.That(source.Amount, Is.EqualTo(5));
        Assert.That(session.Item.Inventory.Get(existing.Uid), Is.SameAs(existing));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(source.Uid), Is.EqualTo(session.AccountId));
        Assert.That(verify.GetItem(source.Uid)!.Amount, Is.EqualTo(5));
        Assert.That(verify.GetAllItems(session.CharacterId).Single().Amount, Is.EqualTo(potion.Property.SlotMax));
    }

    [Test]
    public void StaleInventorySavesAndRetirementCannotReclaimAMailAttachment() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item stale = Add(session, new Item(potion, amount: 5));
        Mail mail = SendMail(session, 0, stale);
        Assert.That(stale.OwnerId, Is.EqualTo(session.CharacterId));
        Assert.That(mail.Items.Single().OwnerId, Is.EqualTo(mail.Id));
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SaveItems(session.CharacterId, stale), Is.False);
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SaveItems(0, stale), Is.False);
        }
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(stale.Uid), Is.EqualTo(mail.Id));
        Assert.That(verify.GetMail(mail.Id, session.CharacterId)!.Items.Single().Amount, Is.EqualTo(5));
    }

    [Test]
    public void CurrencySaveRejectsStaleVersionsAndReturnsFreshTokensWithoutMutatingPlayer() {
        using SessionData data = NewSession();
        Player player = data.Session.Player.Value;
        Player stale = Snapshot(player);
        var previous = (player.Account.LastModified, player.Character.LastModified);
        (DateTime AccountLastModified, DateTime CharacterLastModified)? saved;
        using (GameStorage.Request request = storage.Context()) {
            saved = request.SaveCurrency(player, meso: -10);
        }
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.Value.AccountLastModified, Is.GreaterThan(previous.Item1));
        Assert.That(saved.Value.CharacterLastModified, Is.GreaterThan(previous.Item2));
        Assert.That((player.Account.LastModified, player.Character.LastModified), Is.EqualTo(previous));
        Assert.That(player.Currency.Meso, Is.EqualTo(100));
        player.Account.LastModified = saved.Value.AccountLastModified;
        player.Character.LastModified = saved.Value.CharacterLastModified;
        player.Currency.Meso = 90;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SaveCurrency(stale, meso: 20), Is.Null);
        }
        Assert.That(ReadMeso(player.Character.Id), Is.EqualTo(90));
        AssertVersionsCurrent(data.Session);
        using (GameStorage.Request request = storage.Context()) {
            request.BeginTransaction();
            Assert.That(request.SavePlayer(player), Is.True);
            Assert.That(request.Commit(), Is.True);
        }
        Assert.That(ReadMeso(player.Character.Id), Is.EqualTo(90));
    }

    [Test]
    public void RolledBackCurrencySaveLeavesBalancesAndVersionTokensUnchanged() {
        using SessionData data = NewSession();
        Player player = data.Session.Player.Value;
        var original = (player.Account.LastModified, player.Character.LastModified);
        using (GameStorage.Request request = storage.Context()) {
            request.BeginTransaction();
            Assert.That(request.SaveCurrency(player, meso: -10, meret: -5), Is.Not.Null);
            Assert.That((player.Account.LastModified, player.Character.LastModified), Is.EqualTo(original));
        }
        Assert.That(ReadMeso(player.Character.Id), Is.EqualTo(100));
        Assert.That(ReadMeret(player.Account.Id), Is.EqualTo(100));
        AssertVersionsCurrent(data.Session);
    }

    [Test]
    public void CurrencyTransferChecksSavedBalancesAndDoesNotOverwriteOtherCurrencies() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Player player = session.Player.Value;
        player.Currency.Meso = 101;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SaveCurrency(player, meret: -5), Is.Null);
        }
        Assert.That(ReadMeso(session.CharacterId), Is.EqualTo(100));
        Assert.That(ReadMeret(session.AccountId), Is.EqualTo(100));
        AssertVersionsCurrent(session);

        player.Currency.Meso = 100;
        player.Currency.MesoToken = 777;
        player.Currency.Rue = 9;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SaveCurrency(player, meso: -10), Is.Not.Null);
        }
        Assert.That(ReadMeso(session.CharacterId), Is.EqualTo(90));
        Assert.That(Scalar("SELECT COALESCE(JSON_EXTRACT(`Currency`, '$.MesoToken'), 0) FROM `account` WHERE `Id` = @id",
            session.AccountId), Is.Zero);
        Assert.That(Scalar("SELECT COALESCE(JSON_EXTRACT(`Currency`, '$.Rue'), 0) FROM `character` WHERE `Id` = @id",
            session.CharacterId), Is.Zero);
        Assert.That(player.Currency.MesoToken, Is.EqualTo(777));
        Assert.That(player.Currency.Rue, Is.EqualTo(9));
    }

    [Test]
    public void BlueprintExpenditureFirstPersistsTheProgressBehindPendingEarnings() {
        using SessionData data = NewSession(prepareCurrency: session => {
            using GameStorage.Request request = storage.Context();
            request.BeginTransaction();
            return request.SavePlayer(session.Player.Value) && session.Item.Save(request) &&
                   session.Quest.Save(request) && request.Commit();
        });
        GameSession session = data.Session;
        session.Player.Value.Currency.Meret += 50;
        session.Player.Value.Character.Exp = 77;

        Assert.That(RequestCubeHandler.CreateBlueprint(session, new Item(blueprint), Layout(), 25), Is.Not.Null);

        Assert.That(ReadMeret(session.AccountId), Is.EqualTo(125));
        Assert.That(Scalar("SELECT JSON_EXTRACT(`Experience`, '$.Exp') FROM `character` WHERE `Id` = @id",
            session.CharacterId), Is.EqualTo(77));
        AssertVersionsCurrent(session);
    }

    [Test]
    public void FailedBackingSaveCannotCreateBlueprintOrCollectMailCurrency() {
        using SessionData data = NewSession(prepareCurrency: _ => false);
        GameSession session = data.Session;
        Mail mail = SendMail(session, 50, new Item(potion, amount: 3));
        session.Mail = new MailManager(session);

        Assert.That(RequestCubeHandler.CreateBlueprint(session, new Item(blueprint), Layout(), 25), Is.Null);
        Assert.That(session.Mail.Collect(mail.Id), Is.EqualTo(MailError.s_mail_error));

        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetAllItems(session.CharacterId), Is.Empty);
        Assert.That(verify.GetMail(mail.Id, session.CharacterId)!.MesoCollectTime, Is.Zero);
        Assert.That(verify.GetMail(mail.Id, session.CharacterId)!.Items.Single().Amount, Is.EqualTo(3));
        Assert.That(ReadMeso(session.CharacterId), Is.EqualTo(100));
        Assert.That(ReadMeret(session.AccountId), Is.EqualTo(100));
    }

    [Test]
    public void CommittedAbsorptionSavesDestinationAndRetiresSourceWithoutZeroAmountOwnership() {
        using SessionData data = NewSession(2);
        GameSession session = data.Session;
        Item existing = Add(session, new Item(potion, amount: potion.Property.SlotMax - 5));
        Item source = Seed(session.AccountId, new Item(potion, amount: 5));

        Assert.That(session.Item.Inventory.Add(source, out Item? added, commit: true), Is.True);
        Assert.That(added, Is.SameAs(existing));
        Assert.That(source.Amount, Is.EqualTo(5));
        Assert.That(existing.Amount, Is.EqualTo(potion.Property.SlotMax));
        AssertRetired(source.Uid);
        Assert.That(session.Item.Inventory.Add(source, commit: true), Is.False);
        using GameStorage.Request verify = storage.Context();
        Item bag = verify.GetAllItems(session.CharacterId).Single();
        Assert.That((bag.Uid, bag.Amount), Is.EqualTo((existing.Uid, potion.Property.SlotMax)));
        Assert.That(verify.GetAllItems(session.AccountId), Is.Empty);
    }

    [TestCase((short) 2, false)]
    [TestCase((short) 3, true)]
    public void OversizedOwnedAdditionNeverCommitsOnlyAPrefix(short slots, bool fits) {
        using SessionData data = NewSession(slots);
        GameSession session = data.Session;
        int amount = 2 * potion.Property.SlotMax + 3;
        Item source = Seed(session.AccountId, new Item(potion, amount: amount));

        Assert.That(session.Item.Inventory.Add(source, commit: true), Is.EqualTo(fits));
        Assert.That(source.Amount, Is.EqualTo(amount));
        using GameStorage.Request verify = storage.Context();
        List<Item> bag = verify.GetAllItems(session.CharacterId);
        if (!fits) {
            Assert.That(bag, Is.Empty);
            Assert.That(verify.GetItemOwner(source.Uid), Is.EqualTo(session.AccountId));
            Assert.That(verify.GetItem(source.Uid)!.Amount, Is.EqualTo(amount));
            return;
        }
        Assert.That(bag, Has.Count.EqualTo(3));
        Assert.That(bag.Select(item => item.Uid).Distinct().Count(), Is.EqualTo(3));
        Assert.That(bag.Count(item => item.Uid == source.Uid), Is.EqualTo(1));
        Assert.That(bag.Sum(item => item.Amount), Is.EqualTo(amount));
        Assert.That(bag.All(item => item.Amount is > 0 && item.Amount <= potion.Property.SlotMax), Is.True);
        Assert.That(verify.GetAllItems(session.AccountId), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BankAndPetDepositsRetireFullyAbsorbedInventoryUids(bool pet) {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        long ownerId = pet ? session.AccountId + 800000000000000L : session.AccountId;
        Item target = Seed(ownerId, new Item(potion, amount: potion.Property.SlotMax - 5));
        Item source = Add(session, new Item(potion, amount: 5));
        if (pet) {
            PetManager manager = CreatePet(session, ownerId);
            Assert.That(manager.Add(source.Uid, 0, 5), Is.EqualTo(StringCode.s_empty_string));
        } else {
            var manager = new StorageManager(session);
            manager.Deposit(source.Uid, 0, 5);
        }

        Assert.That(session.Item.Inventory.Get(source.Uid), Is.Null);
        AssertRetired(source.Uid);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(target.Uid), Is.EqualTo(ownerId));
        Assert.That(verify.GetItem(target.Uid)!.Amount, Is.EqualTo(potion.Property.SlotMax));
        Assert.That(verify.GetAllItems(session.CharacterId), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FullBagWithdrawalDoesNotSplitOrRemoveBankOrPetSource(bool pet) {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Add(session, new Item(potion, amount: potion.Property.SlotMax));
        long ownerId = pet ? session.AccountId + 800000000000000L : session.AccountId;
        Item source = Seed(ownerId, new Item(potion, amount: 10));
        if (pet) {
            Assert.That(CreatePet(session, ownerId).Remove(source.Uid, 0, 4), Is.EqualTo(StringCode.s_err_inventory));
        } else {
            new StorageManager(session).Withdraw(source.Uid, 0, 4);
        }

        using GameStorage.Request verify = storage.Context();
        List<Item> stored = verify.GetAllItems(ownerId);
        Assert.That(stored, Has.Count.EqualTo(1));
        Assert.That((stored[0].Uid, stored[0].Amount), Is.EqualTo((source.Uid, 10)));
        Assert.That(verify.GetAllItems(session.CharacterId), Has.Count.EqualTo(1));
        Assert.That(session.Item.Inventory.Filter(_ => true).Single().Amount, Is.EqualTo(potion.Property.SlotMax));
    }

    [Test]
    public void PartialDepositFailureRollsBackSourceRemainderAndNewStack() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item source = Add(session, new Item(potion, amount: 10));
        var bank = new StorageManager(session);
        UseFailure(session, new RejectItemChange(source.Uid, amount: 6));

        bank.Deposit(source.Uid, 0, 4);

        Assert.That(session.Item.Inventory.Get(source.Uid), Is.SameAs(source));
        Assert.That(source.Amount, Is.EqualTo(10));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItem(source.Uid)!.Amount, Is.EqualTo(10));
        Assert.That(verify.GetAllItems(session.AccountId), Is.Empty);
        Assert.That(verify.GetAllItems(session.CharacterId), Has.Count.EqualTo(1));
    }

    [Test]
    public void AmbiguousCommitDoesNotReportRejectionAndDuplicateTheItemThroughMailFallback() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        DbContextOptions failing = new DbContextOptionsBuilder(options)
            .AddInterceptors(new RejectCommitAcknowledgement()).Options;
        Set<GameSession>(session, "<GameStorage>k__BackingField", NewStorage(failing));
        var item = new Item(potion, amount: 3);

        Assert.Throws<DbUpdateException>(() => {
            if (!session.Item.Inventory.Add(item)) {
                session.Item.MailItem(item);
            }
        });

        Assert.That(session.PersistenceAborted, Is.True);
        Assert.That(session.Item.Inventory.Add(new Item(potion)), Is.False);
        Assert.That(session.Item.MailItem(new Item(potion)), Is.False);
        using GameStorage.Request verify = storage.Context();
        Assert.That(session.Item.Save(verify), Is.False);
        Assert.That(verify.GetAllItems(session.CharacterId).Single().Amount, Is.EqualTo(3));
        Assert.That(verify.GetAllMail(session.CharacterId), Is.Empty);
    }

    [Test]
    public async Task CommittedMailDoesNotWaitForAWorldCallbackWhileHoldingItem() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        var world = new MailWorldStub(session);
        Set<GameSession>(session, "<World>k__BackingField", world);
        lock (session.Item) {
            Assert.That(session.Item.MailItem(new Item(potion, amount: 3)), Is.True);
            Assert.That(world.Callback, Is.Null);
            session.Scheduler.InvokeAll();
            Assert.That(world.Callback, Is.Null, "The RPC must not start while its caller still owns Item.");
        }
        session.Scheduler.InvokeAll();
        Assert.That(world.Callback, Is.Not.Null);
        await world.Callback!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(world.SynchronousCalls, Is.Zero);
        Assert.That(world.Deadline, Is.Not.Null);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetAllMail(session.CharacterId).Single().Items.Single().Amount, Is.EqualTo(3));
    }

    [Test]
    public void DeferredItemCallbacksWaitForTheOutermostItemLock() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        bool invoked = false;
        lock (session.Item) {
            session.Item.AfterUnlock(() => {
                Assert.That(Monitor.IsEntered(session.Item), Is.False);
                invoked = true;
            });
            session.Scheduler.InvokeAll();
            Assert.That(invoked, Is.False);
        }
        session.Scheduler.InvokeAll();
        Assert.That(invoked, Is.True);
    }

    [Test]
    public void SuppliedAddNotificationsLeaveCommittedItemsVisibleBeforeConditionUpdates() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Quest.Condition condition = TrackCondition(session, ConditionType.item_add);
        var notifications = new List<Action>();
        Item? added;
        lock (session.Item) {
            Assert.That(session.Item.Inventory.Add(new Item(potion, amount: 3), out added,
                commit: true, notifications: notifications), Is.True);
            Assert.That(added!.Amount, Is.EqualTo(3));
            Assert.That(session.Item.Inventory.Add(new Item(potion, amount: 2), commit: true,
                notifications: notifications), Is.True);
            Assert.That(added.Amount, Is.EqualTo(5));
            Assert.That(condition.Counter, Is.Zero);
            Assert.That(session.Scheduler.Queued, Is.Zero);
            using GameStorage.Request verify = storage.Context();
            Assert.That(verify.GetAllItems(session.CharacterId).Single().Amount, Is.EqualTo(5));
        }
        notifications.ForEach(callback => callback());
        Assert.That(condition.Counter, Is.EqualTo(8), "Each callback captures the post-add stack amount at that addition.");
        Assert.That(session.Item.Inventory.Get(added!.Uid)!.Amount, Is.EqualTo(5));
    }

    [Test]
    public void SuppliedComponentNotificationsDoNotDelayPaymentOrDiscardState() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item item = Add(session, new Item(potion, amount: 3));
        session.Scheduler.InvokeAll();
        Quest.Condition condition = TrackCondition(session, ConditionType.item_destroy);
        var notifications = new List<Action>();
        lock (session.Item) {
            Assert.That(session.Item.Inventory.ConsumeItemComponents(
                [new ItemComponent(item.Id, -1, 2, ItemTag.None)], notifications: notifications), Is.True);
            Assert.That(item.Amount, Is.EqualTo(1));
            Assert.That(session.Item.Inventory.ConsumeItemComponents(
                [new ItemComponent(item.Id, -1, 1, ItemTag.None)], notifications: notifications), Is.True);
            Assert.That(session.Item.Inventory.Get(item.Uid), Is.Null);
            Assert.That(condition.Counter, Is.Zero);
            Assert.That(session.Scheduler.Queued, Is.Zero);
            using GameStorage.Request persist = storage.Context();
            Assert.That(session.Item.Inventory.Save(persist), Is.True);
        }
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetAllItems(session.CharacterId), Is.Empty);
            Assert.That(verify.GetItemOwner(item.Uid), Is.Zero);
        }
        notifications.ForEach(callback => callback());
        Assert.That(condition.Counter, Is.EqualTo(1));
    }

    [Test]
    public void SuppliedDiscardNotificationsFollowDurableRetirement() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item item = Seed(session.CharacterId, new Item(potion, amount: 3));
        Quest.Condition condition = TrackCondition(session, ConditionType.item_destroy);
        var notifications = new List<Action>();
        lock (session.Item) {
            session.Item.Inventory.Discard(item, commit: true, notifications: notifications);
            Assert.That(condition.Counter, Is.Zero);
            Assert.That(session.Scheduler.Queued, Is.Zero);
        }
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetItemOwner(item.Uid), Is.Zero);
        }
        notifications.ForEach(callback => callback());
        Assert.That(condition.Counter, Is.EqualTo(1));
    }

    [Test]
    public void FailedDiscardDoesNotEmitCollectedNotificationsOrRetireTheSource() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item item = Seed(session.CharacterId, new Item(potion, amount: 3));
        var notifications = new List<Action>();
        UseFailure(session, new RejectItemChange(item.Uid, ownerId: 0));

        Assert.Throws<InvalidOperationException>(() =>
            session.Item.Inventory.Discard(item, commit: true, notifications: notifications));

        Assert.That(notifications, Is.Empty);
        Assert.That(session.PersistenceAborted, Is.True);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(item.Uid), Is.EqualTo(session.CharacterId));
        Assert.That(verify.GetItem(item.Uid)!.Amount, Is.EqualTo(3));
    }

    [Test]
    public void SuppliedCurrencyNotificationsDoNotDelayTheBalanceChange() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Quest.Condition condition = TrackCondition(session, ConditionType.meso);
        var notifications = new List<Action>();
        var currency = new Item(potion with { Id = 90000001 }, amount: 3);
        lock (session.Item) {
            Assert.That(session.Item.Inventory.Add(currency, notifications: notifications), Is.True);
            Assert.That(session.Currency.Meso, Is.EqualTo(103));
            Assert.That(condition.Counter, Is.Zero);
            Assert.That(session.Scheduler.Queued, Is.Zero);
        }
        notifications.ForEach(callback => callback());
        Assert.That(condition.Counter, Is.EqualTo(3));
    }

    [Test]
    public void PartialWithdrawalCommitsDistinctUidAndSourceRemainderTogether() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item source = Seed(session.AccountId, new Item(potion, amount: 10));

        new StorageManager(session).Withdraw(source.Uid, 0, 4);

        using GameStorage.Request verify = storage.Context();
        Item bag = verify.GetAllItems(session.CharacterId).Single();
        Assert.That(bag.Uid, Is.Not.EqualTo(source.Uid));
        Assert.That(bag.Amount, Is.EqualTo(4));
        Assert.That(verify.GetItemOwner(source.Uid), Is.EqualTo(session.AccountId));
        Assert.That(verify.GetItem(source.Uid)!.Amount, Is.EqualTo(6));
        Assert.That(session.Item.Inventory.Get(bag.Uid)!.Amount, Is.EqualTo(4));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void QuarantinedStorageAndPetTeardownReleaseReferencesWithoutSavingStaleItems(bool pet) {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        long ownerId = pet ? session.AccountId + 800000000000000L : session.AccountId;
        Item persisted = Seed(ownerId, new Item(potion, amount: 5));
        IDisposable manager;
        if (pet) {
            session.Pet = CreatePet(session, ownerId);
            manager = session.Pet;
        } else {
            session.Storage = new StorageManager(session);
            manager = session.Storage;
        }
        var collection = (ItemCollection) manager.GetType()
            .GetField("items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        collection.Get(persisted.Uid)!.Amount = 99;
        lock (session.Item) {
            session.AbortPersistence("Injected uncertain receipt.");
            manager.Dispose();
        }

        Assert.That(session.Storage, Is.Null);
        Assert.That(session.Pet, Is.Null);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(persisted.Uid), Is.EqualTo(ownerId));
        Assert.That(verify.GetItem(persisted.Uid)!.Amount, Is.EqualTo(5));
    }

    [Test]
    public async Task QuarantinedTradeTeardownDoesNotWaitForMutexOrWriteDurableOffers() {
        using SessionData sender = NewSession();
        using SessionData receiver = NewSession();
        GameSession from = sender.Session;
        GameSession to = receiver.Session;
        Item offered = Add(from, new Item(potion, amount: 3));
        TradeManager trade = StartTrade(from, to);
        trade.AddItem(from, offered.Uid, 3, 0);
        object mutex = typeof(TradeManager).GetField("mutex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(trade)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() => {
            lock (mutex) {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        Task? cleanup = null;
        Task? winner = null;
        try {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            cleanup = Task.Run(() => {
                lock (from.Item) {
                    from.AbortPersistence("Injected uncertain receipt.");
                    trade.Dispose();
                }
            });
            winner = await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(2)));
        } finally {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
            if (cleanup != null) {
                await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        Assert.That(winner, Is.SameAs(cleanup));
        Assert.That(from.Trade, Is.Null);
        Assert.That(to.Trade, Is.Null);
        Assert.That(to.PersistenceAborted, Is.True);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(offered.Uid), Is.EqualTo(from.CharacterId));
        Assert.That(verify.GetItem(offered.Uid)!.Amount, Is.EqualTo(3));
        Assert.That(verify.GetAllMail(from.CharacterId), Is.Empty);
        Assert.That(verify.GetAllMail(to.CharacterId), Is.Empty);
    }

    [TestCase(4)]
    [TestCase(10)]
    public void ApplyingCommittedRemovalDoesNotTransferOrSplitTheItemAgain(int amount) {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item source = Add(session, new Item(potion, amount: 10));
        Item snapshot = source.Clone();
        Item moved = source.Clone(amount < source.Amount ? 0 : source.Uid);
        moved.Amount = amount;
        var changes = new List<(long OwnerId, Item Item)> { (session.AccountId, moved) };
        if (amount < source.Amount) {
            Item remainder = source.Clone();
            remainder.Slot = source.Slot;
            remainder.Group = source.Group;
            remainder.Amount -= amount;
            changes.Add((session.CharacterId, remainder));
        }
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.TransferItems([(session.CharacterId, source)], changes), Is.Not.Null);
        }

        lock (session.Item) {
            session.Item.Inventory.ApplyRemoved(snapshot, amount);
        }

        Assert.That(snapshot.Amount, Is.EqualTo(10));
        Assert.That(session.Item.Inventory.Filter(_ => true).Sum(item => item.Amount), Is.EqualTo(10 - amount));
        using GameStorage.Request verify = storage.Context();
        List<Item> bag = verify.GetAllItems(session.CharacterId);
        Assert.That(bag, Has.Count.EqualTo(amount < 10 ? 1 : 0));
        Assert.That(bag.Sum(item => item.Amount), Is.EqualTo(10 - amount));
        Assert.That(verify.GetAllItems(session.AccountId).Single().Amount, Is.EqualTo(amount));
    }

    [Test]
    public void CompetingMailStacksAreRejectedBeforeAnyCurrencyOrItemIsCollected() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item existing = Add(session, new Item(potion, amount: potion.Property.SlotMax - 10));
        Mail mail = SendMail(session, 50, new Item(potion, amount: 6), new Item(potion, amount: 6));
        session.Mail = new MailManager(session);

        Assert.That(session.Mail.Collect(mail.Id), Is.EqualTo(MailError.s_mail_error_receiveitem_to_inven));
        Assert.That(existing.Amount, Is.EqualTo(potion.Property.SlotMax - 10));
        Assert.That(session.Currency.Meso, Is.EqualTo(100));
        using GameStorage.Request verify = storage.Context();
        Mail unchanged = verify.GetMail(mail.Id, session.CharacterId)!;
        Assert.That(unchanged.MesoCollectTime, Is.Zero);
        Assert.That(unchanged.Items.Select(item => item.Amount), Is.EqualTo(new[] { 6, 6 }));
        Assert.That(ReadMeso(session.CharacterId), Is.EqualTo(100));
    }

    [Test]
    public void MailCanFillAnExistingStackInAFullBagAndCreditsExactlyOnce() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item existing = Add(session, new Item(potion, amount: potion.Property.SlotMax - 3));
        Mail mail = SendMail(session, 50, new Item(potion), new Item(potion, amount: 2));
        session.Mail = new MailManager(session);

        Assert.That(session.Mail.Collect(mail.Id), Is.EqualTo(MailError.none));
        Assert.That(session.Mail.Collect(mail.Id), Is.EqualTo(MailError.s_mail_error_already_receive));
        Assert.That(existing.Amount, Is.EqualTo(potion.Property.SlotMax));
        Assert.That(session.Currency.Meso, Is.EqualTo(150));
        foreach (Item attachment in mail.Items) AssertRetired(attachment.Uid);
        using GameStorage.Request verify = storage.Context();
        Mail collected = verify.GetMail(mail.Id, session.CharacterId)!;
        Assert.That(collected.Items, Is.Empty);
        Assert.That(collected.MesoCollectTime, Is.GreaterThan(0));
        Assert.That(verify.GetAllItems(session.CharacterId).Single().Amount, Is.EqualTo(potion.Property.SlotMax));
        Assert.That(ReadMeso(session.CharacterId), Is.EqualTo(150));
        AssertVersionsCurrent(session);
    }

    [Test]
    public void FailedMailTransferRollsBackCurrencyTimestampsAndAllAttachments() {
        using SessionData data = NewSession(2);
        GameSession session = data.Session;
        Mail mail = SendMail(session, 50, new Item(potion, amount: 3), new Item(potion, 2, 4));
        session.Mail = new MailManager(session);
        UseFailure(session, new RejectItemChange(mail.Items[1].Uid, ownerId: session.CharacterId));

        Assert.That(session.Mail.Collect(mail.Id), Is.EqualTo(MailError.s_mail_error));
        Assert.That(session.Currency.Meso, Is.EqualTo(100));
        Assert.That(session.Item.Inventory.Filter(_ => true), Is.Empty);
        using GameStorage.Request verify = storage.Context();
        Mail unchanged = verify.GetMail(mail.Id, session.CharacterId)!;
        Assert.That(unchanged.MesoCollectTime, Is.Zero);
        Assert.That(unchanged.Items.Select(item => item.Amount), Is.EqualTo(new[] { 3, 4 }));
        Assert.That(verify.GetAllItems(session.CharacterId), Is.Empty);
        Assert.That(ReadMeso(session.CharacterId), Is.EqualTo(100));
        AssertVersionsCurrent(session);
    }

    [Test]
    public void MailCreationFailurePreservesExistingAttachmentAndCreatesNoMail() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        Item source = Seed(session.AccountId, new Item(potion, amount: 4));
        var mail = new Mail(30) { ReceiverId = session.CharacterId, Type = MailType.System };
        mail.Items.Add(source);
        mail.Items.Add(new Item(potion) { Appearance = null });
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.CreateMail(mail), Is.Null);
        }
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetAllMail(session.CharacterId), Is.Empty);
        Assert.That(verify.GetItemOwner(source.Uid), Is.EqualTo(session.AccountId));
        Assert.That(verify.GetItem(source.Uid)!.Amount, Is.EqualTo(4));
    }

    [Test]
    public void AccountMailBindingJoinsOuterTransactionWithoutCommittingIt() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        var pending = new Mail(30) { ReceiverId = session.AccountId, Type = MailType.System };
        pending.Items.Add(new Item(potion, amount: 3));
        Mail mail;
        using (GameStorage.Request request = storage.Context()) {
            mail = request.CreateMail(pending)!;
        }
        using (GameStorage.Request request = storage.Context()) {
            request.BeginTransaction();
            request.BindAccountMailsToCharacter(session.AccountId, session.CharacterId);
            Assert.That(request.IsTransaction, Is.True);
            Assert.That(request.GetMail(mail.Id, session.CharacterId)!.Items.Single().Amount, Is.EqualTo(3));
        }
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetMail(mail.Id, session.AccountId)!.Items.Single().Amount, Is.EqualTo(3));
            Assert.That(verify.GetMail(mail.Id, session.CharacterId), Is.Null);
            verify.BindAccountMailsToCharacter(session.AccountId, session.CharacterId);
        }
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetMail(mail.Id, session.AccountId), Is.Null);
            Assert.That(verify.GetMail(mail.Id, session.CharacterId)!.Items.Single().Amount, Is.EqualTo(3));
            verify.BeginTransaction();
            verify.BindAccountMailsToCharacter(session.AccountId, session.CharacterId);
            Assert.That(verify.IsTransaction, Is.True, "An empty binding must not commit the outer transaction either.");
        }
    }

    [Test]
    public void FailedAccountMailBindingPreservesHeaderAndAttachments() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        var pending = new Mail(30) { ReceiverId = session.AccountId, Type = MailType.System };
        pending.Items.Add(new Item(potion, amount: 3));
        Mail mail;
        using (GameStorage.Request request = storage.Context()) {
            mail = request.CreateMail(pending)!;
        }
        DbContextOptions failing = new DbContextOptionsBuilder(options)
            .AddInterceptors(new RejectMailBinding(session.CharacterId)).Options;
        using (var context = new Ms2Context(failing))
        using (var request = new GameStorage.Request(storage, context, NullLogger<GameStorage>.Instance)) {
            Assert.Throws<InvalidOperationException>(() =>
                request.BindAccountMailsToCharacter(session.AccountId, session.CharacterId));
            Assert.That(request.HasFailed, Is.True);
        }
        using GameStorage.Request verify = storage.Context();
        Mail unchanged = verify.GetMail(mail.Id, session.AccountId)!;
        Assert.That(unchanged.Items.Single().Uid, Is.EqualTo(mail.Items.Single().Uid));
        Assert.That(unchanged.Items.Single().Amount, Is.EqualTo(3));
        Assert.That(verify.GetItemOwner(mail.Items.Single().Uid), Is.EqualTo(mail.Id));
        Assert.That(verify.GetMail(mail.Id, session.CharacterId), Is.Null);
    }

    [Test]
    public void FullBagsDuringPendingTradeDoNotLoseItemsAndCancellationUsesDurableMail() {
        using SessionData sender = NewSession();
        using SessionData receiver = NewSession();
        GameSession from = sender.Session;
        GameSession to = receiver.Session;
        Item offered = Add(from, new Item(potion, amount: 5) {
            Transfer = new ItemTransfer(TransferFlag.LimitTrade, 5),
        });
        TradeManager trade = StartTrade(from, to);
        trade.AddItem(from, offered.Uid, 5, 0);
        trade.SetMesos(from, 20);
        Add(from, new Item(potion, 2, potion.Property.SlotMax));
        Add(to, new Item(potion, amount: potion.Property.SlotMax));

        CompleteTrade(trade, from, to);
        Assert.That(from.Trade, Is.SameAs(trade));
        Assert.That(to.Trade, Is.SameAs(trade));
        trade.RemoveItem(from, offered.Uid, 0);
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetItemOwner(offered.Uid), Is.EqualTo(from.CharacterId));
            Assert.That(verify.GetItem(offered.Uid)!.Amount, Is.EqualTo(5));
        }
        Assert.That(from.Currency.Meso, Is.EqualTo(100));

        trade.Dispose();

        Assert.That(from.Trade, Is.Null);
        Assert.That(to.Trade, Is.Null);
        using (GameStorage.Request verify = storage.Context()) {
            Mail returned = verify.GetAllMail(from.CharacterId).Single();
            Item attachment = returned.Items.Single();
            Assert.That((attachment.Uid, attachment.Amount), Is.EqualTo((offered.Uid, 5)));
            Assert.That(attachment.Transfer!.RemainTrades, Is.EqualTo(5));
            Assert.That(verify.GetItemOwner(offered.Uid), Is.EqualTo(returned.Id));
            Assert.That(verify.GetAllItems(from.CharacterId).Single().Rarity, Is.EqualTo(2));
        }
        Assert.That(ReadMeso(from.CharacterId), Is.EqualTo(100));
    }

    [Test]
    public void TradeStagingAbsorptionRetiresTheSecondInventoryUid() {
        using SessionData sender = NewSession();
        using SessionData receiver = NewSession();
        GameSession from = sender.Session;
        Item first = Add(from, new Item(potion, amount: potion.Property.SlotMax - 5));
        TradeManager trade = StartTrade(from, receiver.Session);
        trade.AddItem(from, first.Uid, first.Amount, 0);
        Item second = Add(from, new Item(potion, amount: 5));

        trade.AddItem(from, second.Uid, 5, 0);

        AssertRetired(second.Uid);
        using (GameStorage.Request verify = storage.Context()) {
            Item staged = verify.GetAllItems(from.CharacterId).Single();
            Assert.That((staged.Uid, staged.Amount), Is.EqualTo((first.Uid, potion.Property.SlotMax)));
        }
        trade.Dispose();
        Assert.That(from.Item.Inventory.Get(first.Uid)!.Amount, Is.EqualTo(potion.Property.SlotMax));
    }

    [Test]
    public void FailedCancellationMailRollsBackBothReturnsAndKeepsTheTradeOpen() {
        using SessionData sender = NewSession();
        using SessionData receiver = NewSession();
        GameSession from = sender.Session;
        GameSession to = receiver.Session;
        Item first = Add(from, new Item(potion, amount: 3));
        Item second = Add(to, new Item(potion, amount: 4));
        TradeManager trade = StartTrade(from, to);
        trade.AddItem(from, first.Uid, 3, 0);
        trade.AddItem(to, second.Uid, 4, 0);
        Add(from, new Item(potion, 2, potion.Property.SlotMax));
        Add(to, new Item(potion, 2, potion.Property.SlotMax));
        UseFailure(from, new RejectItemChange(second.Uid, amount: 4));

        Assert.Throws<InvalidOperationException>(() => trade.Dispose());

        Assert.That(from.Trade, Is.SameAs(trade));
        Assert.That(to.Trade, Is.SameAs(trade));
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetAllMail(from.CharacterId), Is.Empty);
            Assert.That(verify.GetAllMail(to.CharacterId), Is.Empty);
            Assert.That(verify.GetItemOwner(first.Uid), Is.EqualTo(from.CharacterId));
            Assert.That(verify.GetItemOwner(second.Uid), Is.EqualTo(to.CharacterId));
        }
        Set<GameSession>(from, "<GameStorage>k__BackingField", storage);
        trade.Dispose();
        using GameStorage.Request returned = storage.Context();
        Assert.That(returned.GetAllMail(from.CharacterId).Single().Items.Single().Amount, Is.EqualTo(3));
        Assert.That(returned.GetAllMail(to.CharacterId).Single().Items.Single().Amount, Is.EqualTo(4));
    }

    [Test]
    public void InventoryReloadPreservesOverlappingStagedSlotsByMailingOverflow() {
        using SessionData sender = NewSession();
        using SessionData receiver = NewSession();
        GameSession from = sender.Session;
        Item offered = Add(from, new Item(potion, amount: 5));
        TradeManager trade = StartTrade(from, receiver.Session);
        trade.AddItem(from, offered.Uid, 5, 0);
        Add(from, new Item(potion, 2, potion.Property.SlotMax));

        using (GameStorage.Request load = storage.Context()) {
            var reloaded = new InventoryManager(load, from);
            Assert.That(reloaded.Filter(_ => true), Has.Count.EqualTo(1));
        }
        using GameStorage.Request verify = storage.Context();
        List<Item> bag = verify.GetAllItems(from.CharacterId);
        Item mailed = verify.GetAllMail(from.CharacterId).Single().Items.Single();
        Assert.That(bag, Has.Count.EqualTo(1));
        Assert.That(bag[0].Uid, Is.Not.EqualTo(mailed.Uid));
        Assert.That(bag[0].Amount + mailed.Amount, Is.EqualTo(potion.Property.SlotMax + 5));
    }

    [Test]
    public void TradeCommitsBothRecipientsAndCurrencyOrRollsBackTheWholeExchange() {
        using SessionData sender = NewSession();
        using SessionData receiver = NewSession();
        GameSession from = sender.Session;
        GameSession to = receiver.Session;
        Item first = Add(from, new Item(potion, amount: 3));
        Item second = Add(to, new Item(potion, 2, 4));
        TradeManager trade = StartTrade(from, to);
        trade.AddItem(from, first.Uid, 3, 0);
        trade.AddItem(to, second.Uid, 4, 0);
        trade.SetMesos(from, 20);
        trade.SetMesos(to, 10);
        UseFailure(from, new RejectItemChange(second.Uid, ownerId: from.CharacterId));

        CompleteTrade(trade, from, to);

        Assert.That(from.Trade, Is.SameAs(trade));
        Assert.That(from.Item.Inventory.Filter(_ => true), Is.Empty);
        Assert.That(to.Item.Inventory.Filter(_ => true), Is.Empty);
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetItemOwner(first.Uid), Is.EqualTo(from.CharacterId));
            Assert.That(verify.GetItemOwner(second.Uid), Is.EqualTo(to.CharacterId));
        }
        Assert.That(ReadMeso(from.CharacterId), Is.EqualTo(100));
        Assert.That(ReadMeso(to.CharacterId), Is.EqualTo(100));
        Set<GameSession>(from, "<GameStorage>k__BackingField", storage);

        CompleteTrade(trade, from, to);

        Assert.That(from.Trade, Is.Null);
        Assert.That(to.Trade, Is.Null);
        Assert.That(from.Item.Inventory.Get(second.Uid)!.Amount, Is.EqualTo(4));
        Assert.That(to.Item.Inventory.Get(first.Uid)!.Amount, Is.EqualTo(3));
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetItemOwner(first.Uid), Is.EqualTo(to.CharacterId));
            Assert.That(verify.GetItemOwner(second.Uid), Is.EqualTo(from.CharacterId));
        }
        Assert.That((from.Currency.Meso, to.Currency.Meso), Is.EqualTo((90L, 109L)));
        Assert.That((ReadMeso(from.CharacterId), ReadMeso(to.CharacterId)), Is.EqualTo((90L, 109L)));
        AssertVersionsCurrent(from);
        AssertVersionsCurrent(to);
    }

    [Test]
    public void BlueprintUsesPersistedInventoryUidAndConfirmationPreservesOwnership() {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        var input = new Item(blueprint);

        Item? actual = RequestCubeHandler.CreateBlueprint(session, input, Layout(), 25);

        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.Uid, Is.GreaterThan(0));
        Assert.That(input.Uid, Is.Zero);
        Assert.That(session.Item.Inventory.Get(actual.Uid), Is.SameAs(actual));
        Assert.That(session.StagedUgcItem, Is.SameAs(actual));
        Assert.That(session.Currency.Meret, Is.EqualTo(75));
        Assert.That(ReadMeret(session.AccountId), Is.EqualTo(75));
        AssertVersionsCurrent(session);
        var resource = new UgcResource { Id = 987654321, Type = UgcType.LayoutBlueprint, Path = "blueprint/test" };
        Item uploaded = actual.Clone();
        uploaded.Slot = actual.Slot;
        uploaded.Group = actual.Group;
        uploaded.Template = new UgcItemLook {
            Id = resource.Id,
            AccountId = session.AccountId,
            CharacterId = session.CharacterId,
            Name = "Test blueprint",
        };
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.UpdateItem(session.CharacterId, uploaded), Is.True);
        }
        actual.Template = uploaded.Template;

        UseFailure(session, new RejectItemChange(actual.Uid, ownerId: session.CharacterId));
        Assert.That(UgcHandler.ConfirmLayoutBlueprint(session, resource), Is.False);
        Assert.That(actual.Template.Url, Is.Empty);
        Assert.That(session.StagedUgcItem, Is.SameAs(actual));
        Set<GameSession>(session, "<GameStorage>k__BackingField", storage);
        Assert.That(UgcHandler.ConfirmLayoutBlueprint(session, resource), Is.True);
        Assert.That(UgcHandler.ConfirmLayoutBlueprint(session,
            new UgcResource { Id = resource.Id + 1, Type = UgcType.LayoutBlueprint, Path = "wrong" }), Is.False);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(actual.Uid), Is.EqualTo(session.CharacterId));
        Item saved = verify.GetItem(actual.Uid)!;
        Assert.That(saved.Template!.Url, Is.EqualTo(resource.Path));
        Assert.That(saved.Slot, Is.EqualTo(actual.Slot));
        Assert.That(saved.Group, Is.EqualTo(ItemGroup.Default));
        Assert.That(verify.GetHomeLayout(saved.Blueprint!.BlueprintUid), Is.Not.Null);
        Assert.That(verify.UpdateItem(session.AccountId, saved), Is.False);
        Assert.That(verify.GetItemOwner(actual.Uid), Is.EqualTo(session.CharacterId));
    }

    [TestCase("capacity")]
    [TestCase("currency")]
    [TestCase("persistence")]
    public void BlueprintFailureDoesNotChargeStageOrLeaveALayout(string failure) {
        using SessionData data = NewSession();
        GameSession session = data.Session;
        if (failure == "capacity") Add(session, new Item(blueprint));
        var input = new Item(blueprint);
        if (failure == "persistence") input.Appearance = null;
        long layouts = Scalar("SELECT COUNT(*) FROM `home-layout`");

        Assert.That(RequestCubeHandler.CreateBlueprint(session, input, Layout(), failure == "currency" ? 101 : 25), Is.Null);

        Assert.That(input.Uid, Is.Zero);
        Assert.That(session.StagedUgcItem, Is.Null);
        Assert.That(session.Currency.Meret, Is.EqualTo(100));
        Assert.That(ReadMeret(session.AccountId), Is.EqualTo(100));
        Assert.That(Scalar("SELECT COUNT(*) FROM `home-layout`"), Is.EqualTo(layouts));
        AssertVersionsCurrent(session);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetAllItems(session.CharacterId), Has.Count.EqualTo(failure == "capacity" ? 1 : 0));
    }

    [Test]
    public void UnclaimedMailCannotBeDeleted() {
        using SessionData data = NewSession();
        Mail mail = SendMail(data.Session, 50, new Item(potion));
        using GameStorage.Request request = storage.Context();
        Assert.That(request.DeleteMail(mail.Id, data.Session.CharacterId), Is.False);
        Assert.That(request.GetMail(mail.Id, data.Session.CharacterId)!.Items, Has.Count.EqualTo(1));
    }

    private SessionData NewSession(short slots = 1, Func<GameSession, bool>? prepareCurrency = null) {
        using GameStorage.Request db = storage.Context();
        Account account = db.CreateAccount(new Account {
            Username = "transfer" + Guid.NewGuid().ToString("N")[..10],
        }, Guid.NewGuid().ToString("N"));
        var character = new Character {
            AccountId = account.Id,
            Name = "Tr" + Guid.NewGuid().ToString("N")[..8],
            MapId = 2000001,
            Mastery = new Mastery(),
        };
        character.ReturnMaps.Push(character.MapId);
        character = db.CreateCharacter(character)!;
        Assert.That(character, Is.Not.Null);
        Assert.That(db.InitNewCharacter(character.Id, new Unlock()), Is.True);
        var session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        GC.SuppressFinalize(session);
        Set<GameSession>(session, "gameDisposeState", 2);
        var packets = new BlockingCollection<(byte[], int)>();
        Set<NetworkSession>(session, "sendQueue", packets);
        Set<NetworkSession>(session, "lastSentPackets", new ConcurrentDictionary<SendOp, byte[]>());
        Set<NetworkSession>(session, "Logger", Log.Logger);
        Set<NetworkSession>(session, "<AccountId>k__BackingField", account.Id);
        Set<NetworkSession>(session, "<CharacterId>k__BackingField", character.Id);
        Set<GameSession>(session, "<GameStorage>k__BackingField", storage);
        Set<GameSession>(session, "<ItemMetadata>k__BackingField", items);
        var scheduler = new EventQueue(Log.Logger);
        scheduler.Start();
        Set<GameSession>(session, "Scheduler", scheduler);
        var serverTables = new ServerTableMetadataStorage(metadata);
        var constants = new ConstantsTable(MailExpiryDays: 30, TradeRequestDuration: 60, TradeMaxMeso: 1000000, TradeFeePercent: 5,
            bagSlotTabGameCount: [slots, 60], bagSlotTabSkinCount: [slots, 60], bagSlotTabSummonCount: [slots, 60],
            bagSlotTabMaterialCount: [slots, 60], bagSlotTabMasteryCount: [slots, 60], bagSlotTabLifeCount: [slots, 60],
            bagSlotTabQuestCount: [slots, 60], bagSlotTabGemCount: [slots, 60], bagSlotTabPetCount: [slots, 60],
            bagSlotTabActiveSkillCount: [slots, 60], bagSlotTabCoinCount: [slots, 60], bagSlotTabBadgeCount: [slots, 60],
            bagSlotTabMiscCount: [slots, 60], bagSlotTabLapenShardCount: [slots, 60], bagSlotTabPieceCount: [slots, 60]);
        Set<ServerTableMetadataStorage>(serverTables, "constantsTable", new Lazy<ConstantsTable>(() => constants));
        Set<GameSession>(session, "<ServerTableMetadata>k__BackingField", serverTables);
        var achievements = new AchievementMetadataStorage(metadata);
        Set<AchievementMetadataStorage>(achievements, "cachedTypes", Enum.GetValues<ConditionType>().ToHashSet());
        Set<GameSession>(session, "<AchievementMetadata>k__BackingField", achievements);
        var player = new Player(account, character, 1) {
            Home = new Home { AccountId = account.Id },
            Currency = new Currency { Meso = 100, Meret = 100 },
            Unlock = new Unlock(),
        };
        var fieldPlayer = (FieldPlayer) RuntimeHelpers.GetUninitializedObject(typeof(FieldPlayer));
        GC.SuppressFinalize(fieldPlayer);
        Set<Actor<Player>>(fieldPlayer, "<Value>k__BackingField", player);
        Set<Actor<Player>>(fieldPlayer, "<ObjectId>k__BackingField", player.ObjectId);
        Set<FieldPlayer>(fieldPlayer, "Session", session);
        Set<GameSession>(session, "<Player>k__BackingField", fieldPlayer);
        session.Currency = new CurrencyManager(session);
        session.Item = new ItemManager(db, session, null!) {
            PrepareCurrencyTransfer = () => prepareCurrency?.Invoke(session) ?? true,
        };
        session.Achievement = new AchievementManager(session);
        session.Quest = new QuestManager(session);
        var initialVersions = db.GetLastModifiedTimestamps(character.Id)!.Value;
        player.Account.LastModified = initialVersions.AccountLastModified;
        player.Character.LastModified = initialVersions.CharacterLastModified;
        player.Unlock.LastModified = initialVersions.UnlockLastModified;
        using (GameStorage.Request seed = storage.Context()) {
            seed.BeginTransaction();
            Assert.That(seed.SavePlayer(player), Is.True);
            Assert.That(seed.Commit(), Is.True);
        }
        return new SessionData(session, packets);
    }

    private GameStorage NewStorage(DbContextOptions settings) => new(settings, items, new MapMetadataStorage(metadata),
        new AchievementMetadataStorage(metadata), new QuestMetadataStorage(metadata), new TableMetadataStorage(metadata),
        new ServerTableMetadataStorage(metadata), NullLogger<GameStorage>.Instance, new FunctionCubeMetadataStorage(metadata));

    private void UseFailure(GameSession session, SaveChangesInterceptor interceptor) {
        DbContextOptions settings = new DbContextOptionsBuilder(options).AddInterceptors(interceptor).Options;
        Set<GameSession>(session, "<GameStorage>k__BackingField", NewStorage(settings));
    }

    private Item Seed(long ownerId, Item item) {
        using GameStorage.Request db = storage.Context();
        Item? result = db.CreateItem(ownerId, item);
        Assert.That(result, Is.Not.Null);
        return result!;
    }

    private static Item Add(GameSession session, Item item) {
        Assert.That(session.Item.Inventory.Add(item, out Item? added, commit: true), Is.True);
        Assert.That(added, Is.Not.Null);
        return added!;
    }

    private Mail SendMail(GameSession session, long mesos, params Item[] attachments) {
        var mail = new Mail(30) { ReceiverId = session.CharacterId, Type = MailType.System, Meso = mesos };
        foreach (Item item in attachments) mail.Items.Add(item);
        using GameStorage.Request db = storage.Context();
        return db.CreateMail(mail) ?? throw new InvalidOperationException("Failed to create test mail.");
    }

    private PetManager CreatePet(GameSession session, long ownerId) {
        var pet = (FieldPet) RuntimeHelpers.GetUninitializedObject(typeof(FieldPet));
        GC.SuppressFinalize(pet);
        Set<FieldPet>(pet, "Pet", new Item(potion) { Uid = ownerId });
        var manager = (PetManager) RuntimeHelpers.GetUninitializedObject(typeof(PetManager));
        Set<PetManager>(manager, "session", session);
        Set<PetManager>(manager, "pet", pet);
        var collection = new ItemCollection(2);
        using GameStorage.Request db = storage.Context();
        foreach (Item item in db.GetStorage(ownerId)) collection.Add(item);
        Set<PetManager>(manager, "items", collection);
        return manager;
    }

    private static TradeManager StartTrade(GameSession sender, GameSession receiver) {
        var trade = new TradeManager(sender, receiver);
        sender.Trade = receiver.Trade = trade;
        trade.Acknowledge(receiver);
        trade.Accept(receiver);
        return trade;
    }

    private static void CompleteTrade(TradeManager trade, GameSession sender, GameSession receiver) {
        trade.Finalize(sender);
        trade.Finalize(receiver);
        trade.Complete(sender);
        trade.Complete(receiver);
    }

    private void AssertRetired(long uid) {
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItemOwner(uid), Is.Zero);
        Assert.That(verify.GetItem(uid)!.Amount, Is.Zero);
    }

    private void AssertVersionsCurrent(GameSession session) {
        using GameStorage.Request verify = storage.Context();
        var versions = verify.GetLastModifiedTimestamps(session.CharacterId)!.Value;
        Assert.That(session.Player.Value.Account.LastModified, Is.EqualTo(versions.AccountLastModified));
        Assert.That(session.Player.Value.Character.LastModified, Is.EqualTo(versions.CharacterLastModified));
    }

    private static Quest.Condition TrackCondition(GameSession session, ConditionType type) {
        var reward = new QuestMetadataReward(0, 0, ExpType.none, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0);
        var conditionMetadata = new ConditionMetadata(type, 100, null, null);
        var metadata = new QuestMetadata(987654321, "Transfer notification",
            new QuestMetadataBasic(0, QuestType.GuildQuest, 0, 0, true, "", false, false, false, 0, 0,
                [], [], 0, "", "", ""),
            new QuestMetadataRequire(0, 0, [], [], [], 0, (0, 0), 0, "", 0),
            reward, reward, new QuestRemoteAccept(QuestRemoteType.None, 0),
            new QuestRemoteComplete(QuestRemoteType.None, 0, false), new QuestMetadataGoToNpc(false, 0, 0),
            new QuestMetadataGoToDungeon(QuestState.None, 0, 0), null, null, null, QuestEventMissionType.none,
            [conditionMetadata]);
        var condition = new Quest.Condition(conditionMetadata);
        var quest = new Quest(metadata) {
            State = QuestState.Started,
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Conditions = new SortedDictionary<int, Quest.Condition> { [0] = condition },
        };
        Set<QuestManager>(session.Quest, "characterValues", new Dictionary<int, Quest> { [quest.Id] = quest });
        return condition;
    }

    private static Player Snapshot(Player player) => new(
        new Account {
            Id = player.Account.Id,
            Username = player.Account.Username,
            LastModified = player.Account.LastModified,
        },
        new Character {
            Id = player.Character.Id,
            AccountId = player.Account.Id,
            Name = player.Character.Name,
            Mastery = new Mastery(),
            LastModified = player.Character.LastModified,
        }, player.ObjectId) {
        Home = player.Home,
        Unlock = new Unlock(),
        Currency = new Currency {
            Meso = player.Currency.Meso,
            Meret = player.Currency.Meret,
            GameMeret = player.Currency.GameMeret,
        },
    };

    private long ReadMeso(long characterId) => Scalar("SELECT JSON_EXTRACT(`Currency`, '$.Meso') FROM `character` WHERE `Id` = @id", characterId);
    private long ReadMeret(long accountId) => Scalar("SELECT JSON_EXTRACT(`Currency`, '$.Meret') FROM `account` WHERE `Id` = @id", accountId);

    private long Scalar(string sql, long id = 0) {
        using var context = new Ms2Context(options);
        context.Database.OpenConnection();
        using DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("@id", StringComparison.Ordinal)) {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = id;
            command.Parameters.Add(parameter);
        }
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static HomeLayout Layout() => new(0, "Test blueprint", 10, 8, DateTimeOffset.UtcNow, []);

    private static void Set<T>(object target, string name, object value) {
        FieldInfo field = typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field {typeof(T).Name}.{name}.");
        field.SetValue(target, value);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing persistence-test setting {name}.");

    private sealed class SessionData(GameSession session, BlockingCollection<(byte[], int)> packets) : IDisposable {
        public GameSession Session { get; } = session;
        public void Dispose() => packets.Dispose();
    }

    private sealed class RejectItemChange(long uid, int? amount = null, long? ownerId = null) : SaveChangesInterceptor {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry =>
                entry.Metadata.ClrType.Name == "Item" && entry.State == EntityState.Modified &&
                entry.CurrentValues.GetValue<long>("Id") == uid &&
                (!amount.HasValue || entry.CurrentValues.GetValue<int>("Amount") == amount.Value) &&
                (!ownerId.HasValue || entry.CurrentValues.GetValue<long>("OwnerId") == ownerId.Value))) {
                throw new DbUpdateException("Injected final item-transfer failure.");
            }
            return result;
        }
    }

    private sealed class RejectMailBinding(long characterId) : SaveChangesInterceptor {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry =>
                entry.Metadata.ClrType.Name == "Mail" && entry.State == EntityState.Added &&
                entry.CurrentValues.GetValue<long>("ReceiverId") == characterId)) {
                throw new DbUpdateException("Injected account mail binding failure.");
            }
            return result;
        }
    }

    private sealed class RejectCommitAcknowledgement : DbTransactionInterceptor {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) {
            throw new DbUpdateException("Injected loss of commit acknowledgement after the data was committed.");
        }
    }

    private sealed class MailWorldStub(GameSession session) : WorldClient {
        public Task<MailNotificationResponse>? Callback;
        public DateTime? Deadline;
        public int SynchronousCalls;

        public override MailNotificationResponse MailNotification(MailNotificationRequest request, CallOptions options) {
            SynchronousCalls++;
            throw new InvalidOperationException("World notification must not synchronously re-enter the Item lock.");
        }

        public override AsyncUnaryCall<MailNotificationResponse> MailNotificationAsync(MailNotificationRequest request, CallOptions options) {
            Deadline = options.Deadline;
            Callback = Task.Run(() => {
                lock (session.Item) {
                    using GameStorage.Request verify = session.GameStorage.Context();
                    Assert.That(verify.GetMail(request.MailId, session.CharacterId)!.Items.Single().Amount, Is.EqualTo(3));
                    session.ConditionUpdate(ConditionType.item_collect, codeLong: 20000027);
                    return new MailNotificationResponse();
                }
            });
            return new AsyncUnaryCall<MailNotificationResponse>(Callback, Task.FromResult(new Grpc.Core.Metadata()),
                () => Status.DefaultSuccess, () => new Grpc.Core.Metadata(), () => { });
        }
    }
}
