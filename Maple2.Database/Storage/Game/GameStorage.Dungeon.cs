using Maple2.Database.Extensions;
using Maple2.Model.Game;
using Maple2.Model.Game.Dungeon;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using DbAccount = Maple2.Database.Model.Account;
using DbCharacterUnlock = Maple2.Database.Model.CharacterUnlock;
using DbDungeonRankReward = Maple2.Database.Model.DungeonRankReward;
using DbItem = Maple2.Database.Model.Item;
using DbMail = Maple2.Database.Model.Mail;

namespace Maple2.Database.Storage;

public partial class GameStorage {
    public partial class Request {
        public Dictionary<int, DungeonRecord> GetDungeonRecords(long ownerId, bool accountWide) {
            return Context.DungeonRecord.Where(record => record.OwnerId == ownerId && record.AccountWide == accountWide)
                .AsEnumerable()
                .Select<Model.DungeonRecord, DungeonRecord>(record => record)
                .ToDictionary(record => record.DungeonId);
        }

        public DungeonRecord? CreateDungeonRecord(DungeonRecord dungeonRecord, long ownerId, bool accountWide) {
            if (accountWide) {
                return CreateAccountDungeonRecord(dungeonRecord, ownerId);
            }

            Model.DungeonRecord model = dungeonRecord;
            model.OwnerId = ownerId;
            model.AccountWide = accountWide;
            model.CharacterOwnerId = ownerId;
            Context.DungeonRecord.Add(model);

            return Context.TrySaveChanges() ? model : null;
        }

        private DungeonRecord? CreateAccountDungeonRecord(DungeonRecord dungeonRecord, long accountId) {
            Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            try {
                using IDbContextTransaction transaction = Context.Database.BeginTransaction();
                DbAccount? account = Context.Account
                    .FromSqlInterpolated($"SELECT * FROM `account` WHERE `Id` = {accountId} FOR UPDATE")
                    .AsEnumerable()
                    .SingleOrDefault();
                if (account == null) {
                    transaction.Rollback();
                    return null;
                }

                Model.DungeonRecord? existing = Context.DungeonRecord.Find(accountId, true, dungeonRecord.DungeonId);
                if (existing != null) {
                    transaction.Commit();
                    return existing;
                }

                Model.DungeonRecord[] legacy = Context.DungeonRecord
                    .Where(record => !record.AccountWide && record.DungeonId == dungeonRecord.DungeonId)
                    .Join(Context.Character.Where(character => character.AccountId == accountId),
                        record => record.OwnerId,
                        character => character.Id,
                        (record, _) => record)
                    .ToArray();
                if (legacy.Length > 0) {
                    DungeonRecord merged = DungeonRecord.Merge(
                        dungeonRecord.DungeonId,
                        legacy.Select<Model.DungeonRecord, DungeonRecord>(record => record),
                        DateTimeOffset.Now.ToUnixTimeSeconds());
                    dungeonRecord = merged;
                    Context.DungeonRecord.RemoveRange(legacy);
                }

                Model.DungeonRecord model = dungeonRecord;
                model.OwnerId = accountId;
                model.AccountWide = true;
                model.CharacterOwnerId = null;
                Context.DungeonRecord.Add(model);
                Context.SaveChanges();
                transaction.Commit();
                return model;
            } catch (DbUpdateException ex) {
                Logger.LogError(ex, "Failed to migrate account-wide dungeon record {DungeonId} for account {AccountId}",
                    dungeonRecord.DungeonId, accountId);
                return null;
            } catch (System.Data.Common.DbException ex) {
                Logger.LogError(ex, "Failed to migrate account-wide dungeon record {DungeonId} for account {AccountId}",
                    dungeonRecord.DungeonId, accountId);
                return null;
            }
        }

        public bool SaveDungeonRecords(long ownerId, bool accountWide, params DungeonRecord[] records) {
            var models = new Model.DungeonRecord[records.Length];
            for (int i = 0; i < records.Length; i++) {
                models[i] = records[i];
                models[i].OwnerId = ownerId;
                models[i].AccountWide = accountWide;
                models[i].CharacterOwnerId = accountWide ? null : ownerId;
                Context.DungeonRecord.Update(models[i]);
            }

            return Context.TrySaveChanges();
        }

        public DateTime? SaveDungeonRankRewards(long ownerId, IDictionary<int, DungeonRankReward> rewards) {
            Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            DbCharacterUnlock? unlock = Context.CharacterUnlock.Find(ownerId);
            if (unlock == null) {
                return null;
            }

            unlock.DungeonRankRewards = rewards.Values
                .Select<DungeonRankReward, DbDungeonRankReward>(reward => reward)
                .ToDictionary(reward => reward.Id);
            Context.Entry(unlock).Property(entry => entry.DungeonRankRewards).IsModified = true;

            return Context.TrySaveChanges() ? unlock.LastModified : null;
        }

        public (DungeonRankReward Reward, DateTime LastModified)? ClaimDungeonRankRewards(
            long ownerId,
            DungeonRankReward reward,
            long weekStartTimestamp,
            IReadOnlyCollection<(int Rank, Mail Mail)> rewardMails) {
            Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            try {
                using IDbContextTransaction transaction = Context.Database.BeginTransaction();
                DbCharacterUnlock? unlock = Context.CharacterUnlock
                    .FromSqlInterpolated($"SELECT * FROM `character-unlock` WHERE `CharacterId` = {ownerId} FOR UPDATE")
                    .AsEnumerable()
                    .SingleOrDefault();
                if (unlock == null) {
                    transaction.Rollback();
                    return null;
                }

                DateTime weekStart = weekStartTimestamp.FromEpochSeconds();
                unlock.DungeonRankRewards.TryGetValue(reward.Id, out DbDungeonRankReward? existing);
                int claimedRank = existing != null && existing.ClaimTime >= weekStart ? existing.RankClaimed : 0;
                if (claimedRank >= reward.RankClaimed) {
                    transaction.Commit();
                    DungeonRankReward current = existing!;
                    return (current, unlock.LastModified);
                }

                (int Rank, Mail Mail)[] pending = rewardMails
                    .Where(entry => entry.Rank > claimedRank && entry.Rank <= reward.RankClaimed)
                    .ToArray();
                if (pending.Length == 0 || pending.Any(entry => entry.Mail.Items.Count == 0)) {
                    transaction.Rollback();
                    return null;
                }

                var mails = new List<(DbMail Model, Mail Value)>(pending.Length);
                foreach ((_, Mail mail) in pending) {
                    DbMail model = mail;
                    model.Id = 0;
                    Context.Mail.Add(model);
                    mails.Add((model, mail));
                }
                Context.SaveChanges();

                foreach ((DbMail mail, Mail value) in mails) {
                    foreach (Maple2.Model.Game.Item item in value.Items) {
                        DbItem model = item;
                        model.Id = 0;
                        model.OwnerId = mail.Id;
                        Context.Item.Add(model);
                    }
                }

                var claimed = new DbDungeonRankReward {
                    Id = reward.Id,
                    RankClaimed = pending.Max(entry => entry.Rank),
                    ClaimTime = reward.UpdatedTimestamp.FromEpochSeconds(),
                };
                unlock.DungeonRankRewards[claimed.Id] = claimed;
                Context.Entry(unlock).Property(entry => entry.DungeonRankRewards).IsModified = true;
                Context.SaveChanges();
                transaction.Commit();
                DungeonRankReward result = claimed;
                return (result, unlock.LastModified);
            } catch (DbUpdateException ex) {
                Logger.LogError(ex, "Failed to claim dungeon rank rewards for character {CharacterId}", ownerId);
                return null;
            } catch (System.Data.Common.DbException ex) {
                Logger.LogError(ex, "Failed to claim dungeon rank rewards for character {CharacterId}", ownerId);
                return null;
            }
        }
    }
}
