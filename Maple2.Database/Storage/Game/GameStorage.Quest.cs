using System.Data.Common;
using Maple2.Database.Extensions;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Maple2.Database.Storage;

public enum QuestExpirationResult {
    Failed,
    Expired,
    RewardPending,
}

public partial class GameStorage {
    public partial class Request {
        public Quest? CreateQuest(long ownerId, Quest quest) {
            Model.Quest model = quest;
            model.OwnerId = ownerId;
            Context.Quest.Add(model);

            return Context.TrySaveChanges() ? ToQuest(model) : null;
        }

        public List<Item>? ActivateQuest(long accountId, long characterId, Quest quest, Quest? previous,
                                        IReadOnlyList<Item> additions, Func<Request, bool> saveItems) {
            long ownerId = quest.Metadata.Basic.Account > 0 ? accountId : characterId;
            if (HasFailed || IsTransaction || quest.State != QuestState.Started || quest.StartTime <= 0 ||
                (previous != null && (previous.Id != quest.Id || previous.State is not (QuestState.None or QuestState.Completed))) ||
                additions.Any(item => item.Amount <= 0 || item.Slot < 0 ||
                    item.Group is not (ItemGroup.Default or ItemGroup.Furnishing))) {
                Logger.LogError("Invalid activation of quest {QuestId} for owner {OwnerId}", quest.Id, ownerId);
                return null;
            }

            bool committing = false;
            try {
                using IDbContextTransaction transaction = Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
                Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
                if (!Context.Character.Any(character =>
                        character.Id == characterId && character.AccountId == accountId)) {
                    Logger.LogError("Invalid quest owner {AccountId}/{CharacterId}", accountId, characterId);
                    return null;
                }

                // Match session-save lock order: items before quests. Account-row locks here would invert it.
                if (additions.Count > 0 && !saveItems(this)) {
                    Logger.LogError("Failed to save inventory before activating quest {QuestId}", quest.Id);
                    return null;
                }

                Model.Quest? existing = Context.Quest
                    .FromSqlInterpolated($"SELECT * FROM `quest` WHERE `OwnerId` = {ownerId} AND `Id` = {quest.Id} FOR UPDATE")
                    .SingleOrDefault();
                if (previous == null ? existing != null : existing == null ||
                    existing.State != previous.State || existing.CompletionCount != previous.CompletionCount ||
                    existing.StartTime != previous.StartTime || existing.EndTime != previous.EndTime) {
                    Logger.LogWarning("Quest {QuestId} changed before activation for owner {OwnerId}", quest.Id, ownerId);
                    return null;
                }

                Model.Quest model = quest;
                model.OwnerId = ownerId;
                if (existing == null) {
                    Context.Quest.Add(model);
                } else {
                    Context.Entry(existing).CurrentValues.SetValues(model);
                }

                var models = new List<Model.Item>(additions.Count);
                foreach (Item item in additions) {
                    Model.Item value = item;
                    value.OwnerId = item.Group == ItemGroup.Furnishing ? accountId : characterId;
                    if (value.Id == 0) {
                        Context.Item.Add(value);
                    } else {
                        Model.Item? stored = Context.Item.Find(value.Id);
                        if (stored == null || stored.OwnerId != value.OwnerId || stored.Group != value.Group) {
                            Logger.LogError("Quest {QuestId} cannot update unowned item {ItemUid}", quest.Id, value.Id);
                            return null;
                        }
                        Context.Entry(stored).CurrentValues.SetValues(value);
                        value = stored;
                    }
                    models.Add(value);
                }

                Context.SaveChanges();
                List<Item> saved = models.Select((item, index) => item.Convert(additions[index].Metadata)).ToList();
                committing = true;
                transaction.Commit();
                return saved;
            } catch (Exception ex) when (!committing && (ex is DbUpdateException || ex.GetBaseException() is DbException)) {
                Logger.LogError(ex, "Failed to activate quest {QuestId} for owner {OwnerId}", quest.Id, ownerId);
                return null;
            }
        }

        public IDictionary<int, Quest> GetQuests(long ownerId) {
            return Context.Quest.Where(quest => quest.OwnerId == ownerId)
                .AsEnumerable()
                .Select(ToQuest)
                .Where(quest => quest != null)
                .ToDictionary(quest => quest!.Id, quest => quest!);
        }

        public QuestExpirationResult ExpireQuest(long ownerId, Quest quest, long now) {
            if (!quest.IsExpired(now)) {
                Logger.LogWarning("Rejected premature expiration of quest {QuestId} for owner {OwnerId}", quest.Id, ownerId);
                return QuestExpirationResult.Failed;
            }

            try {
                using IDbContextTransaction transaction = Context.Database.BeginTransaction();
                Model.Quest? model = Context.Quest
                    .FromSqlInterpolated($"SELECT * FROM `quest` WHERE `OwnerId` = {ownerId} AND `Id` = {quest.Id} FOR UPDATE")
                    .AsTracking().SingleOrDefault();
                if (model == null || model.StartTime != quest.StartTime || model.CompletionCount != quest.CompletionCount ||
                    model.State is not (QuestState.Started or QuestState.None)) {
                    Logger.LogWarning("Quest {QuestId} changed before expiration for owner {OwnerId}", quest.Id, ownerId);
                    return QuestExpirationResult.Failed;
                }
                if (Context.GuildQuestReward.Any(reward => reward.OwnerId == ownerId &&
                        reward.QuestId == quest.Id && reward.StartTime == quest.StartTime &&
                        reward.CompletionCount > quest.CompletionCount)) {
                    return QuestExpirationResult.RewardPending;
                }

                model.State = QuestState.None;
                Context.SaveChanges();
                transaction.Commit();
                return QuestExpirationResult.Expired;
            } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
                Logger.LogError(ex, "Failed to expire quest {QuestId} for owner {OwnerId}", quest.Id, ownerId);
                return QuestExpirationResult.Failed;
            }
        }

        public bool DeleteQuest(long ownerId, int questId) {
            try {
                using IDbContextTransaction transaction = Context.Database.BeginTransaction();
                Model.Quest? quest = Context.Quest
                    .FromSqlInterpolated($"SELECT * FROM `quest` WHERE `OwnerId` = {ownerId} AND `Id` = {questId} FOR UPDATE")
                    .AsTracking().SingleOrDefault();
                if (quest == null) {
                    return false;
                }
                if (Context.GuildQuestReward.Any(reward => reward.OwnerId == ownerId &&
                        reward.QuestId == questId && reward.StartTime == quest.StartTime)) {
                    Logger.LogWarning("Cannot delete quest {QuestId} after its guild reward was credited", questId);
                    return false;
                }
                Context.Quest.Remove(quest);
                Context.SaveChanges();
                transaction.Commit();
                return true;
            } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
                Logger.LogError(ex, "Failed to delete quest {QuestId} for owner {OwnerId}", questId, ownerId);
                return false;
            }
        }

        public bool SaveQuests(long ownerId, ICollection<Quest> quests) {
            try {
                foreach (Quest quest in quests) {
                    // Expiration is already durable; a later session save must not resurrect it.
                    if (quest.State == QuestState.None) {
                        continue;
                    }
                    Model.Quest model = quest;
                    model.OwnerId = ownerId;
                    var converter = Context.Model.FindEntityType(typeof(Model.Quest))?
                        .FindProperty(nameof(Model.Quest.Conditions))?.GetValueConverter();
                    if (converter?.ConvertToProvider(model.Conditions) is not string conditions) {
                        throw new InvalidOperationException("Quest conditions JSON conversion is not configured.");
                    }
                    // The EF7 provider cannot translate ExecuteUpdate for converted JSON columns.
                    int updated = Context.Database.ExecuteSqlInterpolated($"""
                        UPDATE `quest`
                        SET `State` = {(int) model.State}, `CompletionCount` = {model.CompletionCount},
                            `EndTime` = {model.EndTime}, `Track` = {model.Track}, `Conditions` = {conditions}
                        WHERE `OwnerId` = {ownerId} AND `Id` = {model.Id} AND `StartTime` = {model.StartTime}
                            AND `CompletionCount` <= {model.CompletionCount} AND `State` <> {(int) QuestState.None}
                            AND ({model.State == QuestState.Completed} OR `State` = {(int) QuestState.Started})
                        """);
                    if (updated != 1) {
                        Logger.LogWarning("Skipped stale quest save {QuestId} for owner {OwnerId}", quest.Id, ownerId);
                        return false;
                    }
                    Model.Quest? tracked = Context.Quest.Local.FirstOrDefault(value => value.OwnerId == ownerId && value.Id == quest.Id);
                    if (tracked != null) {
                        Context.Entry(tracked).CurrentValues.SetValues(model);
                        Context.Entry(tracked).State = EntityState.Unchanged;
                    }
                }
                return true;
            } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
                Logger.LogError(ex, "Failed to save quests for owner {OwnerId}", ownerId);
                return false;
            }
        }

        // Converts model to quest if possible, otherwise returns null.
        private Quest? ToQuest(Model.Quest? model) {
            if (model == null) {
                return null;
            }

            return game.questMetadata.TryGet(model.Id, out QuestMetadata? metadata) ? model.Convert(metadata) : null;
        }
    }
}
