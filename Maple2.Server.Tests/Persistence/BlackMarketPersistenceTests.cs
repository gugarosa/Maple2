using System;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maple2.Database.Context;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Tests.Game.Manager;
using Maple2.Server.World.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using GuildQuestRewardsMigration = Maple2.Server.World.Migrations.GuildQuestRewards;
using Item = Maple2.Model.Game.Item;

namespace Maple2.Server.Tests.Persistence;

[Explicit("Requires MAPLE2_RUN_DB_TESTS=1 and isolated validation metadata.")]
[NonParallelizable]
public class BlackMarketPersistenceTests {
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

        connection.Database = "maple2_validation_market_" + Guid.NewGuid().ToString("N")[..12];
        gameOptions = new DbContextOptionsBuilder().UseMySql(connection.ConnectionString, version,
            mysql => mysql.MigrationsAssembly(typeof(GuildQuestRewardsMigration).Assembly.GetName().Name)).Options;
        using (var context = new Ms2Context(gameOptions)) {
            if (context.Database.CanConnect()) {
                throw new InvalidOperationException("Refusing to reuse an existing persistence-test database.");
            }
            databaseCreated = true;
            context.Database.Migrate();
        }
        storage = new GameStorage(gameOptions, items, new MapMetadataStorage(metadataContext),
            new AchievementMetadataStorage(metadataContext), new QuestMetadataStorage(metadataContext),
            new TableMetadataStorage(metadataContext), new ServerTableMetadataStorage(metadataContext),
            NullLogger<GameStorage>.Instance, new FunctionCubeMetadataStorage(metadataContext));
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
    public void ConcurrentBuyersCannotBothPurchaseTheLastStack() {
        Player seller = CreatePlayer();
        Player first = CreatePlayer(10000);
        Player second = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 3);
        BlackMarketError[] results = Race(() => Purchase(first, listing.Id, 3), () => Purchase(second, listing.Id, 3));

        Assert.That(results.Count(error => error == BlackMarketError.none), Is.EqualTo(1));
        Assert.That(results.Count(error => error == BlackMarketError.s_err_lack_itemcount), Is.EqualTo(1));
        Assert.That(Balance(first) + Balance(second), Is.EqualTo(19700));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetBlackMarketListing(listing.Id), Is.Null);
        Mail[] buyers = verify.GetAllMail(first.Character.Id).Concat(verify.GetAllMail(second.Character.Id)).ToArray();
        Assert.That(buyers, Has.Length.EqualTo(1));
        Assert.That(buyers[0].Items.Single().Uid, Is.EqualTo(listing.Item.Uid));
        Assert.That(buyers[0].Items.Single().Amount, Is.EqualTo(3));
        Assert.That(verify.GetAllMail(seller.Character.Id).Single().Meso, Is.EqualTo(295));
    }

    [Test]
    public void ConcurrentPartialPurchasesSplitStockAndRefundTheDepositOnlyOnce() {
        Player seller = CreatePlayer();
        Player first = CreatePlayer(10000);
        Player second = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 2);
        BlackMarketError[] results = Race(() => Purchase(first, listing.Id, 1), () => Purchase(second, listing.Id, 1));

        Assert.That(results, Is.All.EqualTo(BlackMarketError.none));
        Assert.That(Balance(first), Is.EqualTo(9900));
        Assert.That(Balance(second), Is.EqualTo(9900));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetBlackMarketListing(listing.Id), Is.Null);
        Item[] attachments = verify.GetAllMail(first.Character.Id).Concat(verify.GetAllMail(second.Character.Id))
            .SelectMany(mail => mail.Items).ToArray();
        Assert.That(attachments, Has.Length.EqualTo(2));
        Assert.That(attachments.Sum(item => item.Amount), Is.EqualTo(2));
        Assert.That(attachments.Select(item => item.Uid).Distinct().Count(), Is.EqualTo(2));
        Assert.That(attachments.Count(item => item.Uid == listing.Item.Uid), Is.EqualTo(1));
        Assert.That(verify.GetAllMail(seller.Character.Id).Sum(mail => mail.Meso), Is.EqualTo(205));
    }

    [Test]
    public void CancellationAndPurchaseUseTheSameStockClaim() {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 1);
        BlackMarketError[] results = Race(() => Purchase(buyer, listing.Id, 1), () => Cancel(seller, listing.Id));

        Assert.That(results.Count(error => error == BlackMarketError.none), Is.EqualTo(1));
        bool purchased = results[0] == BlackMarketError.none;
        Assert.That(Balance(buyer), Is.EqualTo(purchased ? 9900 : 10000));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetBlackMarketListing(listing.Id), Is.Null);
        Mail[] buyerMails = verify.GetAllMail(buyer.Character.Id).ToArray();
        Mail[] sellerMails = verify.GetAllMail(seller.Character.Id).ToArray();
        Assert.That(buyerMails.Length, Is.EqualTo(purchased ? 1 : 0));
        Assert.That(sellerMails, Has.Length.EqualTo(1));
        Item[] attachments = buyerMails.Concat(sellerMails).SelectMany(mail => mail.Items).ToArray();
        Assert.That(attachments.Single().Uid, Is.EqualTo(listing.Item.Uid));
        Assert.That(attachments.Single().Amount, Is.EqualTo(1));
        Assert.That(sellerMails[0].Meso, Is.EqualTo(purchased ? 115 : 0));
        Assert.That(Cancel(seller, listing.Id), Is.EqualTo(BlackMarketError.s_blackmarket_error_close));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(4)]
    public void InvalidQuantityCannotDebitOrDeliver(int quantity) {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 3);
        Assert.That(Purchase(buyer, listing.Id, quantity), Is.EqualTo(BlackMarketError.s_blackmarket_error_purchase_count));
        AssertUnchanged(seller, buyer, listing, 10000);
    }

    [Test]
    public void FailedPaymentLeavesStockAndBothMailboxesUnchanged() {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(99);
        BlackMarketListing listing = CreateListing(seller, 3);
        Assert.That(Purchase(buyer, listing.Id, 1), Is.EqualTo(BlackMarketError.s_err_lack_meso));
        AssertUnchanged(seller, buyer, listing, 99);
    }

    [Test]
    public void PurchaseUsesTheCurrentPersistedPrice() {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 3);
        using (var context = new Ms2Context(gameOptions)) {
            Assert.That(context.Database.ExecuteSqlInterpolated(
                $"UPDATE `black-market-listing` SET `Price` = 150 WHERE `Id` = {listing.Id}"), Is.EqualTo(1));
        }
        Assert.That(Purchase(buyer, listing.Id, 2), Is.EqualTo(BlackMarketError.none));
        Assert.That(Balance(buyer), Is.EqualTo(9700));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetBlackMarketListing(listing.Id)?.Quantity, Is.EqualTo(1));
        Assert.That(verify.GetAllMail(buyer.Character.Id).Single().Items.Single().Amount, Is.EqualTo(2));
        Assert.That(verify.GetAllMail(seller.Character.Id).Single().Meso, Is.EqualTo(270));
    }

    [Test]
    public void ExpiredListingsCanOnlyReturnTheirItemAndDepositToTheSeller() {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 3);
        using (var context = new Ms2Context(gameOptions)) {
            DateTime expired = DateTime.Now.AddDays(-1);
            Assert.That(context.Database.ExecuteSqlInterpolated(
                $"UPDATE `black-market-listing` SET `ExpiryTime` = {expired} WHERE `Id` = {listing.Id}"), Is.EqualTo(1));
        }
        Assert.That(Purchase(buyer, listing.Id, 1), Is.EqualTo(BlackMarketError.s_blackmarket_error_buy_expired));
        Assert.That(Cancel(buyer, listing.Id), Is.EqualTo(BlackMarketError.s_blackmarket_error_close));
        Assert.That(Cancel(seller, listing.Id), Is.EqualTo(BlackMarketError.none));
        Assert.That(Balance(buyer), Is.EqualTo(10000));
        using GameStorage.Request verify = storage.Context();
        Mail returned = verify.GetAllMail(seller.Character.Id).Single();
        Assert.That(returned.Meso, Is.EqualTo(25));
        Assert.That(returned.Items.Single().Uid, Is.EqualTo(listing.Item.Uid));
        Assert.That(returned.Items.Single().Amount, Is.EqualTo(3));
    }

    [TestCase(1, 1)]
    [TestCase(1, 2)]
    [TestCase(3, 1)]
    [TestCase(3, 2)]
    public void FailedMailOrAttachmentWriteRollsBackPaymentAndStock(int stock, int failedSave) {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, stock);
        DbContextOptions options = new DbContextOptionsBuilder(gameOptions)
            .AddInterceptors(new RejectSave(failedSave)).Options;
        using (var context = new Ms2Context(options))
        using (var request = new GameStorage.Request(storage, context, NullLogger<GameStorage>.Instance)) {
            Assert.Throws<DbUpdateException>(() => request.PurchaseBlackMarketListing(buyer, listing.Id, 1, 0.1f, out _));
        }
        AssertUnchanged(seller, buyer, listing, 10000);
        Assert.That(buyer.Currency.Meso, Is.EqualTo(10000));
        Assert.That(Purchase(buyer, listing.Id, 1), Is.EqualTo(BlackMarketError.none));
        Assert.That(Balance(buyer), Is.EqualTo(9900));
    }

    [Test]
    public void FailedCancellationDeliveryDoesNotDeleteTheListing() {
        Player seller = CreatePlayer();
        BlackMarketListing listing = CreateListing(seller, 3);
        DbContextOptions options = new DbContextOptionsBuilder(gameOptions).AddInterceptors(new RejectSave(2)).Options;
        using (var context = new Ms2Context(options))
        using (var request = new GameStorage.Request(storage, context, NullLogger<GameStorage>.Instance)) {
            Assert.Throws<DbUpdateException>(() => request.CancelBlackMarketListing(seller.Account.Id, seller.Character.Id, listing.Id));
        }
        using (GameStorage.Request verify = storage.Context()) {
            Assert.That(verify.GetBlackMarketListing(listing.Id)?.Quantity, Is.EqualTo(3));
            Assert.That(verify.GetAllMail(seller.Character.Id), Is.Empty);
            Assert.That(verify.GetAllItems(listing.Id).Single().Uid, Is.EqualTo(listing.Item.Uid));
        }
        Assert.That(Cancel(seller, listing.Id), Is.EqualTo(BlackMarketError.none));
    }

    [Test]
    public void CommittedPurchaseRejectsRetriesAndStaleSessionSaves() {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 3);
        BlackMarketPurchase? receipt;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.PurchaseBlackMarketListing(buyer, listing.Id, 1, 0.1f, out receipt), Is.EqualTo(BlackMarketError.none));
        }
        Assert.That(receipt, Is.Not.Null);
        Assert.That(buyer.Currency.Meso, Is.EqualTo(10000), "The caller applies the balance only after a confirmed commit.");
        using (GameStorage.Request retry = storage.Context()) {
            Assert.Throws<DbUpdateConcurrencyException>(() => retry.PurchaseBlackMarketListing(buyer, listing.Id, 1, 0.1f, out _));
        }
        using (GameStorage.Request staleSave = storage.Context()) {
            Assert.That(staleSave.SavePlayer(buyer), Is.False);
        }
        Assert.That(Balance(buyer), Is.EqualTo(9900));
        buyer.Currency.Meso = receipt!.BuyerMeso;
        buyer.Character.LastModified = receipt.CharacterLastModified;
        using (GameStorage.Request currentSave = storage.Context()) {
            Assert.That(currentSave.SavePlayer(buyer), Is.True);
        }
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetBlackMarketListing(listing.Id)?.Quantity, Is.EqualTo(2));
        Assert.That(verify.GetAllMail(buyer.Character.Id), Has.Count.EqualTo(1));
        Assert.That(verify.GetAllMail(seller.Character.Id), Has.Count.EqualTo(1));
        Assert.That(Balance(buyer), Is.EqualTo(9900));
    }

    [Test]
    public void PendingCurrencyMustBeSavedBeforeItCanBeSpent() {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(99);
        BlackMarketListing listing = CreateListing(seller, 1);
        buyer.Currency.Meso += 101;
        Assert.Throws<DbUpdateConcurrencyException>(() => Purchase(buyer, listing.Id, 1));
        AssertUnchanged(seller, buyer, listing, 99);
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SavePlayer(buyer), Is.True);
        }
        Assert.That(Purchase(buyer, listing.Id, 1), Is.EqualTo(BlackMarketError.none));
        Assert.That(Balance(buyer), Is.EqualTo(100));
    }

    [Test]
    public void StaleWorldCacheCannotRestoreOwnershipOnDispose() {
        Player seller = CreatePlayer();
        Player buyer = CreatePlayer(10000);
        BlackMarketListing listing = CreateListing(seller, 1);
        var cache = new BlackMarketLookup(storage);
        Assert.That(Purchase(buyer, listing.Id, 1), Is.EqualTo(BlackMarketError.none));
        cache.Dispose();
        Assert.That(cache.Refresh(listing.Id), Is.False);
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetAllItems(listing.Id), Is.Empty);
        Assert.That(verify.GetAllMail(buyer.Character.Id).Single().Items.Single().Uid, Is.EqualTo(listing.Item.Uid));
    }

    [TestCase(3, 2)]
    [TestCase(2, 3)]
    public void FailedRegistrationRollsBackTheDepositAndOriginalStack(int quantity, int failedSave) {
        Player seller = CreatePlayer(1000);
        Item source;
        using (GameStorage.Request request = storage.Context()) {
            source = request.CreateItem(seller.Character.Id, new Item(itemMetadata, amount: 3) { Slot = 0 })!;
        }
        DbContextOptions options = new DbContextOptionsBuilder(gameOptions).AddInterceptors(new RejectSave(failedSave)).Options;
        using (var context = new Ms2Context(options))
        using (var request = new GameStorage.Request(storage, context, NullLogger<GameStorage>.Instance)) {
            Assert.Throws<DbUpdateException>(() => request.RegisterBlackMarketListing(seller, source, quantity, 100, 25, out _));
        }
        Assert.That(Balance(seller), Is.EqualTo(1000));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetBlackMarketListings(seller.Character.Id), Is.Empty);
        Assert.That(verify.GetAllItems(seller.Character.Id).Single().Uid, Is.EqualTo(source.Uid));
        Assert.That(verify.GetItem(source.Uid)?.Amount, Is.EqualTo(3));
    }

    [TestCase(3, 20L)]
    [TestCase(-1, 20L)]
    [TestCase(-1, 2000000000L)]
    public void StackSaleCreditsAndPricesBuybackForTheEntireSoldAmount(int requested, long unitPrice) {
        Player seller = CreatePlayer(100);
        Item source;
        using (GameStorage.Request request = storage.Context()) {
            source = request.CreateItem(seller.Character.Id, new Item(itemMetadata, amount: 3) { Slot = 0 })!;
        }
        ItemMetadata pricedMetadata = itemMetadata with {
            Limit = itemMetadata.Limit with { Level = 0, ShopSell = true },
            Property = itemMetadata.Property with { SellPrices = [unitPrice], CustomSellPrices = [0] },
        };
        source = source.Mutate(pricedMetadata);
        using var shop = new ShopEconomyTests.ShopState(unitPrice, source, seller, storage);
        shop.Manager.Sell(source.Uid, requested);

        long total = checked(unitPrice * 3);
        Assert.That(shop.Session.Currency.Meso, Is.EqualTo(100 + total));
        Assert.That(shop.Session.Item.Inventory.Get(source.Uid), Is.Null);
        var buyback = shop.Buyback.Values.Single();
        Assert.That(buyback.Price, Is.EqualTo(total));
        Assert.That(buyback.Item.Amount, Is.EqualTo(3));
        Assert.That(buyback.Item.Uid, Is.EqualTo(source.Uid));
    }

    [Test]
    public void PartialStackSaleUsesTheSoldAmountForCreditAndBuyback() {
        Player seller = CreatePlayer(1000);
        Item source;
        using (GameStorage.Request request = storage.Context()) {
            source = request.CreateItem(seller.Character.Id, new Item(itemMetadata, amount: 3) { Slot = 0 })!;
        }
        using var shop = new ShopEconomyTests.ShopState(0, source, seller, storage);
        long unitPrice = Maple2.Server.Core.Formulas.Shop.SellPrice(source.Metadata, source.Type, source.Rarity);
        Assert.That(unitPrice, Is.GreaterThan(0));
        shop.Manager.Sell(source.Uid, 2);

        Assert.That(shop.Session.Currency.Meso, Is.EqualTo(1000 + checked(unitPrice * 2)));
        Assert.That(source.Amount, Is.EqualTo(1));
        var buyback = shop.Buyback.Values.Single();
        Assert.That(buyback.Item.Amount, Is.EqualTo(2));
        Assert.That(buyback.Item.Uid, Is.Not.EqualTo(source.Uid));
        Assert.That(buyback.Price, Is.EqualTo(checked(unitPrice * 2)));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetItem(source.Uid)?.Amount, Is.EqualTo(1));
        Assert.That(verify.GetItem(buyback.Item.Uid)?.Amount, Is.EqualTo(2));
    }

    [Test]
    public void ShopBuybackEvictionDefersNestedItemConditionsUntilAfterUnlock() {
        Player seller = CreatePlayer(1000);
        Item source;
        using (GameStorage.Request request = storage.Context()) {
            source = request.CreateItem(seller.Character.Id, new Item(itemMetadata) { Slot = 0 })!;
        }
        using var shop = new ShopEconomyTests.ShopState(0, source, seller, storage);
        for (int id = 1; id <= Constant.MaxBuyBackItems; id++) {
            shop.Buyback[id] = new BuyBackItem {
                Id = id,
                Item = new Item(itemMetadata) { Uid = id },
                AddedTime = id,
                Price = 1,
            };
        }
        int callbacks = 0;
        bool callbackHeldItem = false;
        shop.ObserveConditions(() => {
            callbacks++;
            callbackHeldItem |= Monitor.IsEntered(shop.Session.Item);
        });

        lock (shop.Session.Item) {
            shop.Manager.Sell(source.Uid, 1);
            shop.Session.Scheduler.InvokeAll();
            Assert.That(callbacks, Is.Zero, "Pumping the scheduler must not bypass an outer item lock.");
        }
        shop.Session.Scheduler.InvokeAll();

        Assert.That(callbacks, Is.EqualTo(3), "Item destruction, meso gain and shop sale must each notify.");
        Assert.That(callbackHeldItem, Is.False, "No nested condition may execute while Item is held.");
        Assert.That(shop.Buyback, Has.Count.EqualTo(Constant.MaxBuyBackItems));
        Assert.That(shop.Session.Item.Inventory.Get(source.Uid), Is.Null);
    }

    private BlackMarketError Purchase(Player buyer, long listingId, int quantity) {
        using GameStorage.Request request = storage.Context();
        return request.PurchaseBlackMarketListing(buyer, listingId, quantity, 0.1f, out _);
    }

    private BlackMarketError Cancel(Player seller, long listingId) {
        using GameStorage.Request request = storage.Context();
        return request.CancelBlackMarketListing(seller.Account.Id, seller.Character.Id, listingId);
    }

    private static BlackMarketError[] Race(Func<BlackMarketError> first, Func<BlackMarketError> second) {
        using var ready = new Barrier(2);
        Task<BlackMarketError> left = Task.Run(() => Run(first));
        Task<BlackMarketError> right = Task.Run(() => Run(second));
        return Task.WhenAll(left, right).GetAwaiter().GetResult();

        BlackMarketError Run(Func<BlackMarketError> action) {
            if (!ready.SignalAndWait(TimeSpan.FromSeconds(30))) {
                throw new TimeoutException("Both independent market requests must reach the concurrency gate.");
            }
            return action();
        }
    }

    private void AssertUnchanged(Player seller, Player buyer, BlackMarketListing listing, long balance) {
        Assert.That(Balance(buyer), Is.EqualTo(balance));
        using GameStorage.Request verify = storage.Context();
        Assert.That(verify.GetBlackMarketListing(listing.Id)?.Quantity, Is.EqualTo(listing.Quantity));
        Assert.That(verify.GetAllItems(listing.Id).Single().Amount, Is.EqualTo(listing.Quantity));
        Assert.That(verify.GetAllMail(seller.Character.Id), Is.Empty);
        Assert.That(verify.GetAllMail(buyer.Character.Id), Is.Empty);
    }

    private long Balance(Player player) {
        using var context = new Ms2Context(gameOptions);
        context.Database.OpenConnection();
        using DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT JSON_UNQUOTE(JSON_EXTRACT(`Currency`, '$.Meso')) FROM `character` WHERE `Id` = @id";
        DbParameter id = command.CreateParameter();
        id.ParameterName = "@id";
        id.Value = player.Character.Id;
        command.Parameters.Add(id);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private Player CreatePlayer(long meso = 1000) {
        Account account;
        Character character;
        using (GameStorage.Request request = storage.Context()) {
            account = request.CreateAccount(new Account {
                Username = "market" + Guid.NewGuid().ToString("N")[..12],
            }, Guid.NewGuid().ToString("N"));
            var value = new Character {
                AccountId = account.Id,
                Name = "Market" + Guid.NewGuid().ToString("N")[..6],
                MapId = 2000001,
                Mastery = new Mastery(),
            };
            value.ReturnMaps.Push(value.MapId);
            character = request.CreateCharacter(value) ?? throw new InvalidOperationException("Failed to create a test character.");
            Assert.That(request.InitNewCharacter(character.Id, new Unlock()), Is.True);
        }
        Player player;
        using (GameStorage.Request request = storage.Context()) {
            player = request.LoadPlayer(account.Id, character.Id, 1, 1) ?? throw new InvalidOperationException("Failed to load a test player.");
        }
        player.Currency.Meso = meso;
        using (GameStorage.Request request = storage.Context()) {
            Assert.That(request.SavePlayer(player), Is.True);
        }
        return player;
    }

    private BlackMarketListing CreateListing(Player seller, int stock) {
        using GameStorage.Request request = storage.Context();
        Item item = request.CreateItem(seller.Character.Id, new Item(itemMetadata, amount: stock) { Slot = 0 })!;
        Assert.That(item, Is.Not.Null);
        Assert.That(request.RegisterBlackMarketListing(seller, item, stock, 100, 25, out BlackMarketRegistration? registration),
            Is.EqualTo(BlackMarketError.none));
        Assert.That(registration, Is.Not.Null);
        seller.Currency.Meso = registration!.SellerMeso;
        seller.Character.LastModified = registration.CharacterLastModified;
        return registration.Listing;
    }

    private sealed class RejectSave(int rejectedSave) : SaveChangesInterceptor {
        private int saves;
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) {
            if (++saves == rejectedSave) {
                throw new DbUpdateException("Injected market mail or item persistence failure.");
            }
            return result;
        }
    }

    private static string Required(string name) {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Missing persistence-test setting {name}.");
    }
}
