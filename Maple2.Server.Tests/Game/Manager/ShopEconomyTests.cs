using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Maple2.Database.Context;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Game.Shop;
using Maple2.Model.Metadata;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Session;
using Maple2.Tools.Scheduler;
using Microsoft.EntityFrameworkCore;
using Serilog;
using GameItem = Maple2.Model.Game.Item;
using NetworkSession = Maple2.Server.Core.Network.Session;

namespace Maple2.Server.Tests.Game.Manager;

public class ShopEconomyTests {
    [TestCase(-1, 59L)]
    [TestCase(3, 59L)]
    [TestCase(2, 39L)]
    public void MesoCeilingRejectsTheSaleBeforeRemovingAnyItems(int requested, long remainingCapacity) {
        using var state = new ShopState(20);
        state.Session.Currency.Meso = Constant.MaxMeso - remainingCapacity;
        state.ClearPackets();
        state.Manager.Sell(state.Item.Uid, requested);

        Assert.That(state.Session.Currency.Meso, Is.EqualTo(Constant.MaxMeso - remainingCapacity));
        Assert.That(state.Session.Item.Inventory.Get(state.Item.Uid), Is.SameAs(state.Item));
        Assert.That(state.Item.Amount, Is.EqualTo(3));
        Assert.That(state.Buyback, Is.Empty);
        AssertShopError(state, ShopError.s_msg_cant_sell);
    }

    [Test]
    public void SaleTotalOverflowCannotRemoveItemsOrCreditWrappedCurrency() {
        using var state = new ShopState(long.MaxValue);
        state.Manager.Sell(state.Item.Uid, 3);

        Assert.That(state.Session.Currency.Meso, Is.Zero);
        Assert.That(state.Session.Item.Inventory.Get(state.Item.Uid), Is.SameAs(state.Item));
        Assert.That(state.Item.Amount, Is.EqualTo(3));
        Assert.That(state.Buyback, Is.Empty);
        AssertShopError(state, ShopError.s_msg_cant_sell);
    }

    [TestCase(0)]
    [TestCase(-2)]
    [TestCase(4)]
    public void OnlyMinusOneOrAnAvailablePositiveSaleAmountIsAccepted(int requested) {
        using var state = new ShopState(20);
        state.Manager.Sell(state.Item.Uid, requested);
        Assert.That(state.Session.Item.Inventory.Get(state.Item.Uid), Is.SameAs(state.Item));
        Assert.That(state.Session.Currency.Meso, Is.Zero);
        Assert.That(state.Buyback, Is.Empty);
        AssertShopError(state, ShopError.s_msg_cant_sell);
    }

    [TestCase(0L, 50L, 20L, true, 30L)]
    [TestCase(1000L, 50L, 20L, true, 30L)]
    [TestCase(1000L, 19L, 20L, false, 19L)]
    [TestCase(1000L, 0L, 1L, false, 0L)]
    public void SharedPurchaseAndRestockPaymentDebitsOnlyGameMeret(
        long regular, long game, long price, bool expected, long remaining) {
        using var state = new ShopState(20);
        state.Session.Currency.Meret = regular;
        state.Session.Currency.GameMeret = game;
        state.ClearPackets();

        Assert.That(state.Manager.Pay(new ShopCost { Type = ShopCurrencyType.GameMeret, Amount = 1 }, price), Is.EqualTo(expected));
        Assert.That(state.Session.Currency.Meret, Is.EqualTo(regular));
        Assert.That(state.Session.Currency.GameMeret, Is.EqualTo(remaining));
        if (!expected) {
            AssertShopError(state, ShopError.s_err_lack_merat);
        }
    }

    [Test]
    public void BuybackPaymentPreservesTotalsLargerThanInt32() {
        using var state = new ShopState(20);
        state.Session.Currency.Meso = 6000000001;
        Assert.That(state.Manager.Pay(new ShopCost { Type = ShopCurrencyType.Meso }, 6000000000), Is.True);
        Assert.That(state.Session.Currency.Meso, Is.EqualTo(1));
    }

    [Test]
    public void PaymentWaitsForTheSameItemGateUsedBySessionSave() {
        using var state = new ShopState(20);
        state.Session.Currency.GameMeret = 50;
        using var ready = new ManualResetEventSlim();
        Task<bool> payment;
        lock (state.Session.Item) {
            payment = Task.Run(() => {
                ready.Set();
                return state.Manager.Pay(new ShopCost { Type = ShopCurrencyType.GameMeret }, 20);
            });
            Assert.That(ready.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(payment.Wait(TimeSpan.FromMilliseconds(100)), Is.False);
            Assert.That(state.Session.Currency.GameMeret, Is.EqualTo(50));
        }
        Assert.That(payment.GetAwaiter().GetResult(), Is.True);
        Assert.That(state.Session.Currency.GameMeret, Is.EqualTo(30));
    }

    [Test]
    public void QuarantinedSessionsCannotResumePaymentsOrSales() {
        using var state = new ShopState(20);
        state.Session.Currency.GameMeret = 50;
        state.Session.AbortPersistence("Injected uncertain transaction outcome.");
        Assert.That(state.Manager.Pay(new ShopCost { Type = ShopCurrencyType.GameMeret }, 20), Is.False);
        state.Manager.Sell(state.Item.Uid, 3);
        Assert.That(state.Session.Currency.GameMeret, Is.EqualTo(50));
        Assert.That(state.Session.Currency.Meso, Is.Zero);
        Assert.That(state.Session.Item.Inventory.Get(state.Item.Uid), Is.SameAs(state.Item));
        Assert.That(state.Buyback, Is.Empty);
    }

    [TestCase(ShopCurrencyType.Meso)]
    [TestCase(ShopCurrencyType.Meret)]
    [TestCase(ShopCurrencyType.GameMeret)]
    public void PaymentAppliesStateBeforeDeferredNotifications(ShopCurrencyType type) {
        using var state = new ShopState(20);
        state.Session.Currency.Meso = 50;
        state.Session.Currency.Meret = 50;
        state.Session.Currency.GameMeret = 50;
        state.ClearPackets();
        var notifications = new List<Action>();
        MethodInfo pay = typeof(ShopManager).GetMethod("PayInternal", BindingFlags.Instance | BindingFlags.NonPublic)!;
        lock (state.Session.Item) {
            Assert.That(pay.Invoke(state.Manager, [new ShopCost { Type = type }, 20L, notifications]), Is.EqualTo(true));
            Assert.That(state.Packets, Is.Empty);
            Assert.That(notifications, Has.Count.EqualTo(1));
        }
        notifications.ForEach(notify => {
            Assert.That(Monitor.IsEntered(state.Session.Item), Is.False);
            notify();
        });
        Assert.That(state.Packets, Has.Count.EqualTo(1));
        Assert.That(type switch {
            ShopCurrencyType.Meso => state.Session.Currency.Meso,
            ShopCurrencyType.Meret => state.Session.Currency.Meret,
            _ => state.Session.Currency.GameMeret,
        }, Is.EqualTo(30));
    }

    [Test]
    public void BuybackEvictionUsesTheShopCollectorInsteadOfSchedulingNestedConditions() {
        using var state = new ShopState(20);
        for (int id = 1; id <= Constant.MaxBuyBackItems; id++) {
            state.Buyback[id] = new BuyBackItem {
                Id = id,
                Item = new GameItem(state.Item.Metadata) { Uid = id },
                AddedTime = id,
                Price = 1,
            };
        }
        var notifications = new List<Action>();
        int callbacks = 0;
        bool callbackHeldItem = false;
        state.ObserveConditions(() => {
            callbacks++;
            callbackHeldItem |= Monitor.IsEntered(state.Session.Item);
        });
        MethodInfo evict = typeof(ShopManager).GetMethod("RemoveBuyBackItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        lock (state.Session.Item) {
            Assert.That(evict.Invoke(state.Manager, [notifications]), Is.EqualTo(true));
            Assert.That(notifications, Has.Count.EqualTo(1));
            Assert.That(state.Session.Scheduler.Count, Is.Zero);
            state.Session.Scheduler.InvokeAll();
            Assert.That(callbacks, Is.Zero);
        }
        notifications.ForEach(state.Session.Item.AfterUnlock);
        state.Session.Scheduler.InvokeAll();

        Assert.That(callbacks, Is.EqualTo(1));
        Assert.That(callbackHeldItem, Is.False);
        Assert.That(state.Buyback, Has.Count.EqualTo(Constant.MaxBuyBackItems - 1));
    }

    private static void AssertShopError(ShopState state, ShopError error) {
        byte[] packet = state.Packets.Single().Packet;
        Assert.That((SendOp) BitConverter.ToUInt16(packet, 0), Is.EqualTo(SendOp.Shop));
        Assert.That(packet[2], Is.EqualTo(15));
        Assert.That((ShopError) BitConverter.ToInt32(packet, 3), Is.EqualTo(error));
    }

    internal sealed class ShopState : IDisposable {
        public readonly GameSession Session;
        public readonly ShopManager Manager;
        public readonly GameItem Item;
        public readonly Dictionary<int, BuyBackItem> Buyback = [];
        public readonly BlockingCollection<(byte[] Packet, int Length)> Packets = new();
        private readonly MetadataContext metadataContext = new(new DbContextOptionsBuilder().Options);

        public ShopState(long unitPrice, GameItem? source = null, Player? owner = null, GameStorage? storage = null) {
            Session = (GameSession) RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
            GC.SuppressFinalize(Session);
            Set<NetworkSession>(Session, "sendQueue", Packets);
            Set<NetworkSession>(Session, "lastSentPackets", new ConcurrentDictionary<SendOp, byte[]>());
            Set<NetworkSession>(Session, "Logger", Log.Logger);
            Set<GameSession>(Session, nameof(GameSession.Scheduler), new EventQueue(Log.Logger));
            Session.Scheduler.Start();
            Player player = owner ?? new Player(new Account { Username = "shop-test" },
                new Character { Name = "ShopTest", Mastery = new Mastery() }, 1) {
                Currency = new Currency(),
                Home = null!,
                Unlock = new Unlock(),
            };
            typeof(NetworkSession).GetProperty(nameof(NetworkSession.AccountId))!.SetValue(Session, player.Account.Id);
            typeof(NetworkSession).GetProperty(nameof(NetworkSession.CharacterId))!.SetValue(Session, player.Character.Id);
            if (storage != null) {
                typeof(GameSession).GetProperty(nameof(GameSession.GameStorage))!.SetValue(Session, storage);
            }
            var fieldPlayer = (FieldPlayer) RuntimeHelpers.GetUninitializedObject(typeof(FieldPlayer));
            Set<Actor<Player>>(fieldPlayer, "<Value>k__BackingField", player);
            typeof(GameSession).GetProperty(nameof(GameSession.Player))!.SetValue(Session, fieldPlayer);
            Session.Currency = new CurrencyManager(Session);

            var achievements = new AchievementMetadataStorage(metadataContext);
            Set<AchievementMetadataStorage>(achievements, "cachedTypes", Enum.GetValues<ConditionType>().ToHashSet());
            typeof(GameSession).GetProperty(nameof(GameSession.AchievementMetadata))!.SetValue(Session, achievements);
            Session.Achievement = (AchievementManager) RuntimeHelpers.GetUninitializedObject(typeof(AchievementManager));
            Set<AchievementManager>(Session.Achievement, "session", Session);
            Session.Quest = (QuestManager) RuntimeHelpers.GetUninitializedObject(typeof(QuestManager));
            Set<QuestManager>(Session.Quest, "accountValues", new Dictionary<int, Quest>());
            Set<QuestManager>(Session.Quest, "characterValues", new Dictionary<int, Quest>());

            var property = new ItemMetadataProperty(false, 0, 100, 18, 0, "", "", ItemTag.None, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                [], false, 0, false, [], [unitPrice], [0], 0, 0);
            var limit = new ItemMetadataLimit(Gender.All, 0, 0, 4, true, true, true, true, true, false, false, 0, [], []);
            var metadata = new ItemMetadata(20000027, "Sale stack", [], "", [], new ItemMetadataLife(0, 0), property,
                new ItemMetadataCustomize(0, 0), limit, null, null, [], null, null, null, null);
            Item = source ?? new GameItem(metadata, amount: 3) { Uid = 123 };
            Session.Item = (ItemManager) RuntimeHelpers.GetUninitializedObject(typeof(ItemManager));
            Set<ItemManager>(Session.Item, "session", Session);
            var inventory = (InventoryManager) RuntimeHelpers.GetUninitializedObject(typeof(InventoryManager));
            Set<InventoryManager>(inventory, "session", Session);
            Set<InventoryManager>(inventory, "delete", new List<GameItem>());
            Set<InventoryManager>(inventory, "logger", Log.Logger);
            Set<InventoryManager>(inventory, "tabs", new Dictionary<InventoryType, ItemCollection> {
                [Item.Inventory] = new ItemCollection(10) { [0] = Item },
            });
            Set<ItemManager>(Session.Item, nameof(ItemManager.Inventory), inventory);

            var restock = new ShopRestockData(ResetType.Default, ShopCurrencyType.Meso, ShopCurrencyType.Meso, 0, 0, 0, false, true, false);
            var shop = new ShopMetadata(1, 0, "", 0, false, false, false, false, false, false, false, 0, false, restock);
            Manager = (ShopManager) RuntimeHelpers.GetUninitializedObject(typeof(ShopManager));
            Set<ShopManager>(Manager, "session", Session);
            Set<ShopManager>(Manager, "logger", Log.Logger);
            Set<ShopManager>(Manager, "activeShop", new Shop(shop));
            Set<ShopManager>(Manager, "buyBackItems", Buyback);
        }

        public void Dispose() {
            Session.Scheduler.Stop();
            Session.Scheduler.Clear();
            Packets.Dispose();
            metadataContext.Dispose();
        }

        public void ClearPackets() {
            while (Packets.TryTake(out _)) { }
        }

        public void ObserveConditions(Action observe) {
            Set<QuestManager>(Session.Quest, "characterValues", new ObservedQuestValues(observe));
        }
    }

    private sealed class ObservedQuestValues(Action observe) : Dictionary<int, Quest>, IDictionary<int, Quest> {
        ICollection<Quest> IDictionary<int, Quest>.Values {
            get {
                observe();
                return base.Values;
            }
        }
    }

    private static void Set<T>(object target, string name, object value) {
        FieldInfo field = typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field {typeof(T).Name}.{name}.");
        field.SetValue(target, value);
    }
}
