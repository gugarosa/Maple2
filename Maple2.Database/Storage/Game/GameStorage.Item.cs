using System.Data.Common;
using Maple2.Database.Model;
using Maple2.Model.Enum;
using Maple2.Model.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Item = Maple2.Model.Game.Item;
using PetConfig = Maple2.Model.Game.PetConfig;
using UgcItemLook = Maple2.Model.Game.UgcItemLook;
using Currency = Maple2.Model.Game.Currency;
using Player = Maple2.Model.Game.Player;

namespace Maple2.Database.Storage;

public partial class GameStorage {
    public partial class Request {
        public Item? CreateItem(long ownerId, Item item) {
            Model.Item model = item;
            model.OwnerId = ownerId;
            model.Id = 0;
            Context.Item.Add(model);

            return SaveChanges() ? ToItem(model) : null;
        }

        public List<Item>? CreateItems(long ownerId, params Item[] items) {
            var models = new Model.Item[items.Length];
            for (int i = 0; i < items.Length; i++) {
                models[i] = items[i];
                models[i].OwnerId = ownerId;
                models[i].Id = 0;
                Context.Item.Add(models[i]);
            }

            if (!SaveChanges()) {
                return null;
            }

            return models.Select(ToItem).Where(item => item != null).ToList()!;
        }

        public Item? GetItem(long itemUid) {
            Model.Item? model = Context.Item.Find(itemUid);
            if (model == null) {
                return null;
            }

            return game.itemMetadata.TryGet(model.ItemId, out ItemMetadata? metadata) ? model.Convert(metadata) : null;
        }

        public UgcItemLook? GetTemplate(long itemUid) {
            ItemSubType? model = Context.Item.Select(item => new {
                item.Id,
                item.SubType,
            }).FirstOrDefault(result => result.Id == itemUid)?.SubType;
            if (model is not ItemUgc ugcModel) {
                return null;
            }

            return ugcModel.Template;
        }

        public IDictionary<ItemGroup, List<Item>> GetItemGroups(long ownerId, params ItemGroup[] groups) {
            return Context.Item.Where(item => item.OwnerId == ownerId && groups.Contains(item.Group))
                .AsEnumerable()
                .GroupBy(item => item.Group)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(ToItem).Where(item => item != null).ToList()
                )!;
        }

        public IDictionary<ItemGroup, List<Item>> GetItemGroupsNoTracking(long ownerId, params ItemGroup[] groups) {
            return Context.Item.Where(item => item.OwnerId == ownerId && groups.Contains(item.Group))
                .AsNoTracking()
                .AsEnumerable()
                .GroupBy(item => item.Group)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(ToItem).Where(item => item != null).ToList()
                )!;
        }

        public Dictionary<InventoryType, List<Item>> GetInventory(long characterId) {
            return Context.Item.Where(item => item.OwnerId == characterId && item.Group == ItemGroup.Default)
                .AsEnumerable()
                .Select(ToItem)
                .Where(item => item != null)
                .GroupBy(item => item!.Inventory)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList()
                )!;
        }

        public (long Mesos, short Expand) GetStorageInfo(long accountId) {
            ItemStorage? info = Context.ItemStorage.Find(accountId);
            if (info == null) {
                return (0, 0);
            }

            return (info.Meso, info.Expand);
        }

        public PetConfig GetPetConfig(long itemUid) {
            return Context.PetConfig.Find(itemUid) ?? new PetConfig();
        }

        public List<Item> GetStorage(long accountId) {
            return Context.Item.Where(item => item.OwnerId == accountId && item.Group == ItemGroup.Default)
                .AsEnumerable()
                .Select(ToItem)
                .Where(item => item != null)
                .ToList()!;
        }

        public List<Item> GetSavedHairs(long characterId) {
            return Context.Item.Where(item => item.OwnerId == characterId && item.Group == ItemGroup.SavedHair)
                .AsEnumerable()
                .Select(ToItem)
                .Where(item => item != null)
                .ToList()!;
        }

        public List<Item> GetAllItems(long ownerId) {
            return Context.Item.Where(item => item.OwnerId == ownerId)
                .AsEnumerable()
                .Select(ToItem)
                .Where(item => item != null)
                .ToList()!;
        }

        public bool SaveItems(long ownerId, params Item[] items) {
            if (HasFailed) return false;
            bool committing = false;
            try {
                using IDbContextTransaction? transaction = Context.Database.CurrentTransaction == null
                    ? Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted) : null;
                var changes = new List<(Model.Item Stored, Model.Item Value)>();
                foreach (Item item in items.Where(item => item.Uid != 0).DistinctBy(item => item.Uid).OrderBy(item => item.Uid)) {
                    Model.Item? stored = Context.Item
                        .FromSqlInterpolated($"SELECT * FROM `item` WHERE `Id` = {item.Uid} FOR UPDATE")
                        .AsTracking().SingleOrDefault();
                    if (stored != null) Context.Entry(stored).Reload();
                    // Repeated cleanup is harmless, but a stale inventory must never reclaim somebody else's item.
                    if (ownerId == 0 && (stored == null || stored.OwnerId == 0)) continue;
                    if (stored == null || stored.OwnerId != item.OwnerId || ownerId != 0 && stored.OwnerId != ownerId) {
                        Logger.LogWarning("Rejected stale save of item {ItemUid} from owner {OwnerId}", item.Uid, item.OwnerId);
                        return false;
                    }

                    Model.Item model = item;
                    model.OwnerId = ownerId;
                    changes.Add((stored, model));
                }
                foreach ((Model.Item stored, Model.Item value) in changes) {
                    Context.Entry(stored).CurrentValues.SetValues(value);
                }
                if (!SaveChanges()) return false;
                committing = transaction != null;
                transaction?.Commit();
                return true;
            } catch (Exception ex) when (!committing && (ex is DbUpdateException || ex.GetBaseException() is DbException)) {
                Logger.LogError(ex, "Failed to save items for owner {OwnerId}", ownerId);
                return false;
            }
        }

        public long? GetItemOwner(long itemUid) {
            return Context.Item.Where(item => item.Id == itemUid).Select(item => (long?) item.OwnerId).SingleOrDefault();
        }

        // Changes contain final stack values, including any source remainder. Omitted sources are retired.
        // An outer transaction may include mail, currency, or layout changes; its caller must check the result.
        public Item[]? TransferItems(IReadOnlyList<(long OwnerId, Item Item)> sources,
                                    IReadOnlyList<(long OwnerId, Item Item)> changes,
                                    Func<Request, bool>? save = null) {
            if (HasFailed || sources.Any(source => source.Item.Uid <= 0 || source.Item.Amount <= 0) ||
                changes.Any(change => change.OwnerId <= 0 || change.Item.Amount <= 0) ||
                sources.Select(source => source.Item.Uid).Distinct().Count() != sources.Count ||
                changes.Where(change => change.Item.Uid != 0).Select(change => change.Item.Uid).Distinct().Count() !=
                changes.Count(change => change.Item.Uid != 0)) {
                return null;
            }

            bool committing = false;
            try {
                using IDbContextTransaction? transaction = Context.Database.CurrentTransaction == null
                    ? Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted) : null;
                if (save != null && !save(this) || HasFailed) {
                    return null;
                }

                Dictionary<long, (long OwnerId, Item Item)> originals = sources.ToDictionary(source => source.Item.Uid);
                var storedItems = new Dictionary<long, Model.Item>();
                long[] uids = sources.Select(source => source.Item.Uid)
                    .Concat(changes.Select(change => change.Item.Uid)).Where(uid => uid != 0).Distinct().Order().ToArray();
                foreach (long uid in uids) {
                    Model.Item? stored = Context.Item
                        .FromSqlInterpolated($"SELECT * FROM `item` WHERE `Id` = {uid} FOR UPDATE")
                        .AsTracking().SingleOrDefault();
                    if (stored != null) Context.Entry(stored).Reload();
                    long ownerId = originals.TryGetValue(uid, out var source) ? source.OwnerId
                        : changes.First(change => change.Item.Uid == uid).OwnerId;
                    if (stored == null || stored.OwnerId != ownerId ||
                        originals.ContainsKey(uid) && stored.Amount != source.Item.Amount) {
                        Logger.LogWarning("Item {ItemUid} changed before transfer from owner {OwnerId}", uid, ownerId);
                        return null;
                    }
                    storedItems.Add(uid, stored);
                }

                var models = new List<Model.Item>(changes.Count);
                foreach ((long ownerId, Item item) in changes) {
                    Model.Item model = item;
                    model.OwnerId = ownerId;
                    if (model.Id == 0) {
                        Context.Item.Add(model);
                    } else {
                        Model.Item stored = storedItems[model.Id];
                        Context.Entry(stored).CurrentValues.SetValues(model);
                        model = stored;
                    }
                    models.Add(model);
                }
                HashSet<long> retained = changes.Select(change => change.Item.Uid).ToHashSet();
                foreach ((long uid, _) in originals) {
                    if (retained.Contains(uid)) continue;
                    Model.Item retired = storedItems[uid];
                    retired.OwnerId = 0;
                    retired.Amount = 0;
                    retired.Slot = -1;
                }

                Context.SaveChanges();
                Item[] result = models.Select((model, index) => model.Convert(changes[index].Item.Metadata)).ToArray();
                // A commit error is ambiguous: do not return a retryable rejection for a possibly delivered batch.
                committing = transaction != null;
                transaction?.Commit();
                return result;
            } catch (Exception ex) when (!committing && (ex is DbUpdateException || ex.GetBaseException() is DbException)) {
                Logger.LogError(ex, "Failed to persist item transfer");
                return null;
            }
        }

        public bool UpdateItem(long ownerId, Item item) {
            if (item.Uid <= 0 || item.Amount <= 0) {
                return false;
            }
            Item? current = GetItem(item.Uid);
            return current != null && TransferItems([(ownerId, current)], [(ownerId, item)]) != null;
        }

        public (DateTime AccountLastModified, DateTime CharacterLastModified)? SaveCurrency(Player player,
            long meso = 0, long meret = 0, long gameMeret = 0) {
            long accountId = player.Account.Id;
            long characterId = player.Character.Id;
            Currency current = player.Currency;
            if (HasFailed || meso < -current.Meso || meso > Constant.MaxMeso - current.Meso ||
                meret < -current.Meret || meret > Constant.MaxMeret - current.Meret ||
                gameMeret < -current.GameMeret || gameMeret > Constant.MaxMeret - current.GameMeret) {
                return null;
            }

            bool committing = false;
            try {
                using IDbContextTransaction? transaction = Context.Database.CurrentTransaction == null
                    ? Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted) : null;
                Model.Account? account = Context.Account
                    .FromSqlInterpolated($"SELECT * FROM `account` WHERE `Id` = {accountId} FOR UPDATE")
                    .AsTracking().SingleOrDefault();
                Model.Character? character = Context.Character
                    .FromSqlInterpolated($"SELECT * FROM `character` WHERE `Id` = {characterId} FOR UPDATE")
                    .AsTracking().SingleOrDefault();
                if (account != null) Context.Entry(account).Reload();
                if (character != null) Context.Entry(character).Reload();
                if (account == null || character == null || character.AccountId != accountId ||
                    account.LastModified != player.Account.LastModified || character.LastModified != player.Character.LastModified) {
                    Logger.LogWarning("Rejected stale currency snapshot for character {CharacterId}", characterId);
                    return null;
                }

                int accountUpdated = Context.Database.ExecuteSqlInterpolated($"""
                    UPDATE `account`
                    SET `Currency` = JSON_SET(`Currency`, '$.Meret', {current.Meret + meret}, '$.GameMeret', {current.GameMeret + gameMeret}),
                        `LastModified` = GREATEST(CURRENT_TIMESTAMP(6), DATE_ADD(`LastModified`, INTERVAL 1 MICROSECOND))
                    WHERE `Id` = {accountId} AND `LastModified` = {player.Account.LastModified}
                        AND COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(`Currency`, '$.Meret')) AS SIGNED), 0) = {current.Meret}
                        AND COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(`Currency`, '$.GameMeret')) AS SIGNED), 0) = {current.GameMeret}
                    """);
                int characterUpdated = Context.Database.ExecuteSqlInterpolated($"""
                    UPDATE `character`
                    SET `Currency` = JSON_SET(`Currency`, '$.Meso', {current.Meso + meso}),
                        `LastModified` = GREATEST(CURRENT_TIMESTAMP(6), DATE_ADD(`LastModified`, INTERVAL 1 MICROSECOND))
                    WHERE `Id` = {characterId} AND `AccountId` = {accountId} AND `LastModified` = {player.Character.LastModified}
                        AND COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(`Currency`, '$.Meso')) AS SIGNED), 0) = {current.Meso}
                    """);
                if (accountUpdated != 1 || characterUpdated != 1) {
                    throw new DbUpdateConcurrencyException("The saved currency snapshot changed before transfer.");
                }
                Context.Entry(account).Reload();
                Context.Entry(character).Reload();
                committing = transaction != null;
                transaction?.Commit();
                return (account.LastModified, character.LastModified);
            } catch (Exception ex) when (!committing && (ex is DbUpdateException || ex.GetBaseException() is DbException)) {
                Logger.LogError(ex, "Failed to persist currency for character {CharacterId}", characterId);
                return null;
            }
        }

        // Delete all items that are unowned. (Character, account, etc.)
        public void DeleteUnownedItems() {
            int deletedCount = Context.Database.ExecuteSqlRaw("DELETE FROM `item` WHERE OwnerId = 0");
            if (deletedCount == 0) {
                return;
            }

            Logger.LogInformation("Deleted {Count} unowned items.", deletedCount);
        }

        public bool SaveStorageInfo(long accountId, long mesos, short expand) {
            ItemStorage? info = Context.ItemStorage.Find(accountId);
            if (info == null) {
                Context.Add(new ItemStorage {
                    AccountId = accountId,
                    Meso = mesos,
                    Expand = expand,
                });
            } else {
                info.Meso = mesos;
                info.Expand = expand;
                Context.ItemStorage.Update(info);
            }

            return SaveChanges();
        }

        public bool SavePetConfig(long itemUid, PetConfig config) {
            Model.PetConfig? model = Context.PetConfig.Find(itemUid);
            Model.PetConfig value = config;
            value.ItemUid = itemUid;
            if (model == null) {
                Context.Add(value);
            } else {
                Context.Entry(model).CurrentValues.SetValues(value);
                Context.Update(model);
            }

            return SaveChanges();
        }

        // Converts model to item if possible, otherwise returns null.
        private Item? ToItem(Model.Item? model) {
            if (model == null) {
                return null;
            }

            return game.itemMetadata.TryGet(model.ItemId, out ItemMetadata? metadata) ? model.Convert(metadata) : null;
        }
    }
}
