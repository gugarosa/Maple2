using System.Data.Common;
using Maple2.Database.Extensions;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Maple2.Database.Storage;

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
            if (quest.State != QuestState.Started || quest.StartTime <= 0 ||
                (previous != null && (previous.Id != quest.Id || previous.State != QuestState.Completed)) ||
                additions.Any(item => item.Amount <= 0 || item.Slot < 0 ||
                    item.Group is not (ItemGroup.Default or ItemGroup.Furnishing))) {
                Logger.LogError("Invalid activation of quest {QuestId} for owner {OwnerId}", quest.Id, ownerId);
                return null;
            }

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
                transaction.Commit();
                return saved;
            } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
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
            foreach (Quest quest in quests) {
                Model.Quest model = quest;
                model.OwnerId = ownerId;

                Context.Quest.Update(model);
            }

            return Context.TrySaveChanges();
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
