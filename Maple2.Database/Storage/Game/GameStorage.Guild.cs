using System.Data.Common;
using Maple2.Database.Extensions;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Tools.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Maple2.Database.Storage;

public enum GuildQuestRewardStatus {
    Applied,
    AlreadyApplied,
    NotMember,
    InvalidRequest,
    Failed,
}

public readonly record struct GuildQuestRewardResult(
    GuildQuestRewardStatus Status,
    long GuildId = 0,
    int Experience = 0,
    int Funds = 0);

public readonly record struct GuildCheckInResult(
    bool Success,
    int Experience = 0,
    int Funds = 0,
    long CheckInTime = 0,
    int Contribution = 0);

public partial class GameStorage {
    public partial class Request {
        public Guild? GetGuild(long guildId) {
            return LoadGuild(guildId, string.Empty);
        }

        public Guild? GetGuild(string guildName) {
            return LoadGuild(0, guildName);
        }

        public bool GuildExists(long guildId = 0, string guildName = "") {
            return Context.Guild.Any(guild => guild.Id == guildId || guild.Name == guildName);
        }

        public IList<GuildMember> GetGuildMembers(IPlayerInfoProvider provider, long guildId) {
            return Context.GuildMember.Where(member => member.GuildId == guildId)
                .AsEnumerable()
                .Select(member => {
                    PlayerInfo? info = provider.GetPlayerInfo(member.CharacterId);
                    return info == null ? null : new GuildMember {
                        GuildId = member.GuildId,
                        Info = info,
                        Message = member.Message,
                        Rank = member.Rank,
                        WeeklyContribution = member.WeeklyContribution,
                        TotalContribution = member.TotalContribution,
                        DailyDonationCount = member.DailyDonationCount,
                        JoinTime = member.CreationTime.ToEpochSeconds(),
                        CheckinTime = member.CheckinTime.ToEpochSeconds(),
                        DonationTime = member.DonationTime.ToEpochSeconds(),
                    };
                })
                .WhereNotNull()
                .ToList();
        }

        public Guild? CreateGuild(string name, long leaderId) {
            BeginTransaction();

            var guild = new Model.Guild {
                Name = name,
                LeaderId = leaderId,
                HouseRank = 1,
                HouseTheme = 1,
                Ranks = [
                    new Model.GuildRank {Name = "Master", Permission = GuildPermission.All},
                    new Model.GuildRank {Name = "Jr. Master", Permission = GuildPermission.Default},
                    new Model.GuildRank {Name = "Member 1", Permission = GuildPermission.Default},
                    new Model.GuildRank {Name = "Member 2", Permission = GuildPermission.Default},
                    new Model.GuildRank {Name = "New Member 1", Permission = GuildPermission.Default},
                    new Model.GuildRank {Name = "New Member 2", Permission = GuildPermission.Default},
                ],
                Buffs = [
                    new Model.GuildBuff {Id = 1, Level = 1},
                    new Model.GuildBuff {Id = 2, Level = 1},
                    new Model.GuildBuff {Id = 3, Level = 1},
                    new Model.GuildBuff {Id = 4, Level = 1},
                    new Model.GuildBuff {Id = 10001, Level = 1},
                    new Model.GuildBuff {Id = 10002, Level = 1},
                    new Model.GuildBuff {Id = 10003, Level = 1},
                    new Model.GuildBuff {Id = 10004, Level = 1},
                    new Model.GuildBuff {Id = 10005, Level = 1},
                ],
                Posters = [],
                Npcs = [],
            };
            Context.Guild.Add(guild);
            if (!SaveChanges()) {
                return null;
            }

            var guildLeader = new Model.GuildMember {
                GuildId = guild.Id,
                CharacterId = leaderId,
                Rank = 0,
            };
            Context.GuildMember.Add(guildLeader);
            if (!SaveChanges()) {
                return null;
            }

            return Commit() ? LoadGuild(guild.Id, string.Empty) : null;
        }

        public GuildMember? CreateGuildMember(long guildId, PlayerInfo info) {
            var member = new Model.GuildMember {
                GuildId = guildId,
                CharacterId = info.CharacterId,
                Rank = 5,
            };
            Context.GuildMember.Add(member);
            if (!SaveChanges()) {
                return null;
            }

            return new GuildMember {
                GuildId = member.GuildId,
                Info = info,
                Rank = member.Rank,
                JoinTime = member.CreationTime.ToEpochSeconds(),
            };
        }

        public bool SaveGuild(Guild guild) {
            // Don't save guild if it was disbanded.
            Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            Model.Guild? model = Context.Guild.Find(guild.Id);
            if (model == null) {
                return false;
            }

            using IDbContextTransaction transaction = Context.Database.BeginTransaction();

            Model.Guild value = guild;
            model.Name = value.Name;
            model.Emblem = value.Emblem;
            model.Notice = value.Notice;
            model.Focus = value.Focus;
            model.HouseRank = value.HouseRank;
            model.HouseTheme = value.HouseTheme;
            model.Ranks = value.Ranks;
            model.Buffs = value.Buffs;
            model.Posters = value.Posters;
            model.Npcs = value.Npcs;
            model.LeaderId = value.LeaderId;
            if (!SaveGuildMemberProfiles(guild.Id, guild.Members.Values)) {
                return false;
            }

            transaction.Commit();
            return true;
        }

        public bool DeleteGuild(long guildId) {
            BeginTransaction();

            int count = Context.Guild.Where(guild => guild.Id == guildId).ExecuteDelete();
            if (count == 0) {
                return false;
            }

            Context.GuildMember.Where(member => member.GuildId == guildId).ExecuteDelete();
            Context.GuildApplication.Where(app => app.GuildId == guildId).ExecuteDelete();

            return Commit();
        }

        public bool DeleteGuildMember(long guildId, long characterId) {
            int count = Context.GuildMember.Where(member => member.GuildId == guildId && member.CharacterId == characterId).ExecuteDelete();
            return SaveChanges() && count > 0;
        }

        public bool DeleteGuildApplication(long applicationId) {
            int count = Context.GuildApplication.Where(app => app.Id == applicationId).ExecuteDelete();
            return SaveChanges() && count > 0;
        }

        public bool DeleteGuildApplications(long characterId) {
            int count = Context.GuildApplication.Where(app => app.ApplicantId == characterId).ExecuteDelete();
            return SaveChanges() && count > 0;
        }

        private bool SaveGuildMemberProfiles(long guildId, ICollection<GuildMember> members) {
            Dictionary<long, GuildMember> values = members.ToDictionary(member => member.CharacterId);
            Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            foreach (Model.GuildMember member in Context.GuildMember.Where(member => member.GuildId == guildId)) {
                if (!values.TryGetValue(member.CharacterId, out GuildMember? value)) {
                    continue;
                }

                member.Message = value.Message;
                member.Rank = value.Rank;
            }

            return SaveChanges();
        }

        public bool SaveGuildMember(GuildMember member) {
            Model.GuildMember? model = Context.GuildMember.Find(member.GuildId, member.CharacterId);
            if (model == null) {
                return false;
            }

            Context.GuildMember.Update(member);
            return SaveChanges();
        }

        public long? GetGuildId(long characterId) {
            return Context.GuildMember
                .Where(member => member.CharacterId == characterId)
                .Select(member => (long?) member.GuildId)
                .SingleOrDefault();
        }

        public (bool Found, long GuildId) GetGuildQuestRewardGuildId(
            long characterId, int questId, long startTime, int completionCount) {
            if (!game.questMetadata.TryGet(questId, out QuestMetadata? metadata)) {
                return (false, 0);
            }

            Model.Character? character = Context.Character.Find(characterId);
            if (character == null) {
                return (false, 0);
            }

            long ownerId = metadata.Basic.Account > 0 ? character.AccountId : characterId;
            long? guildId = Context.GuildQuestReward
                .Where(receipt =>
                    receipt.OwnerId == ownerId &&
                    receipt.QuestId == questId &&
                    receipt.StartTime == startTime &&
                    receipt.CompletionCount == completionCount)
                .Select(receipt => (long?) receipt.GuildId)
                .SingleOrDefault();
            return guildId == null ? (false, 0) : (true, guildId.Value);
        }

        public GuildCheckInResult CheckInGuild(long guildId, long characterId) {
            const int contribution = 10;

            try {
                using IDbContextTransaction transaction = Context.Database.BeginTransaction();
                Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
                Model.GuildMember? member = Context.GuildMember
                    .FromSqlInterpolated($"""
                        SELECT * FROM `guild-member`
                        WHERE `GuildId` = {guildId}
                          AND `CharacterId` = {characterId}
                        FOR UPDATE
                        """)
                    .SingleOrDefault();
                Model.Guild? guild = Context.Guild
                    .FromSqlInterpolated($"SELECT * FROM `guild` WHERE `Id` = {guildId} FOR UPDATE")
                    .SingleOrDefault();
                if (member == null || guild == null) {
                    return new GuildCheckInResult(false);
                }
                if (member.CheckinTime >= DateTime.UtcNow.Date) {
                    return new GuildCheckInResult(false);
                }

                GuildTable.Property property = game.tableMetadata.GuildTable.GetProperty(guild.Experience);
                (guild.Experience, guild.Funds) = game.tableMetadata.GuildTable.AddProgress(
                    guild.Experience, guild.Funds, property.CheckInExp, property.CheckInFund);
                member.CheckinTime = DateTime.UtcNow;
                member.WeeklyContribution += contribution;
                member.TotalContribution += contribution;

                Context.SaveChanges();
                transaction.Commit();
                return new GuildCheckInResult(
                    true,
                    guild.Experience,
                    guild.Funds,
                    member.CheckinTime.ToEpochSeconds(),
                    contribution);
            } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
                Logger.LogError(ex, "Failed guild check-in for character {CharacterId} in guild {GuildId}",
                    characterId, guildId);
                return new GuildCheckInResult(false);
            }
        }

        public GuildQuestRewardResult AwardGuildQuestReward(
            long guildId, long characterId, int questId, long startTime, int completionCount) {
            if (startTime <= 0 || completionCount <= 0 ||
                !game.questMetadata.TryGet(questId, out QuestMetadata? metadata)) {
                return new GuildQuestRewardResult(GuildQuestRewardStatus.InvalidRequest);
            }

            QuestMetadataReward reward = metadata.CompleteReward;
            if (reward.GuildExp < 0 || reward.GuildFund < 0 ||
                (reward.GuildExp == 0 && reward.GuildFund == 0)) {
                return new GuildQuestRewardResult(GuildQuestRewardStatus.InvalidRequest);
            }

            try {
                using IDbContextTransaction transaction = Context.Database.BeginTransaction();
                Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;

                Model.Character? character = Context.Character.SingleOrDefault(value => value.Id == characterId);
                if (character == null) {
                    return new GuildQuestRewardResult(GuildQuestRewardStatus.NotMember);
                }

                long ownerId = metadata.Basic.Account > 0 ? character.AccountId : characterId;
                Model.Quest? quest = Context.Quest
                    .FromSqlInterpolated($"""
                        SELECT * FROM `quest`
                        WHERE `OwnerId` = {ownerId}
                          AND `Id` = {questId}
                        FOR UPDATE
                        """)
                    .SingleOrDefault();
                if (quest == null) {
                    return new GuildQuestRewardResult(GuildQuestRewardStatus.InvalidRequest);
                }

                Model.GuildQuestReward? receipt = Context.GuildQuestReward
                    .FromSqlInterpolated($"""
                        SELECT * FROM `guild-quest-reward`
                        WHERE `OwnerId` = {ownerId}
                          AND `QuestId` = {questId}
                          AND `StartTime` = {startTime}
                          AND `CompletionCount` = {completionCount}
                        FOR UPDATE
                        """)
                    .SingleOrDefault();
                if (receipt != null) {
                    if (receipt.GuildId != guildId) {
                        return new GuildQuestRewardResult(GuildQuestRewardStatus.NotMember);
                    }

                    Model.Guild? rewardedGuild = Context.Guild
                        .FromSqlInterpolated($"SELECT * FROM `guild` WHERE `Id` = {receipt.GuildId} FOR UPDATE")
                        .SingleOrDefault();
                    transaction.Commit();
                    return new GuildQuestRewardResult(
                        GuildQuestRewardStatus.AlreadyApplied,
                        receipt.GuildId,
                        rewardedGuild?.Experience ?? 0,
                        rewardedGuild?.Funds ?? 0);
                }

                if (quest.State != QuestState.Started || quest.StartTime != startTime ||
                    quest.CompletionCount != completionCount - 1) {
                    return new GuildQuestRewardResult(GuildQuestRewardStatus.InvalidRequest);
                }

                Model.GuildMember? member = Context.GuildMember
                    .FromSqlInterpolated($"""
                        SELECT * FROM `guild-member`
                        WHERE `CharacterId` = {characterId}
                        FOR UPDATE
                        """)
                    .SingleOrDefault();
                if (member == null) {
                    return new GuildQuestRewardResult(GuildQuestRewardStatus.NotMember);
                }
                if (member.GuildId != guildId) {
                    return new GuildQuestRewardResult(GuildQuestRewardStatus.NotMember);
                }

                Model.Guild? guild = Context.Guild
                    .FromSqlInterpolated($"SELECT * FROM `guild` WHERE `Id` = {guildId} FOR UPDATE")
                    .SingleOrDefault();
                if (guild == null) {
                    return new GuildQuestRewardResult(GuildQuestRewardStatus.NotMember);
                }

                (guild.Experience, guild.Funds) = game.tableMetadata.GuildTable.AddProgress(
                    guild.Experience, guild.Funds, reward.GuildExp, reward.GuildFund);

                Context.GuildQuestReward.Add(new Model.GuildQuestReward {
                    OwnerId = ownerId,
                    QuestId = questId,
                    CompletionCount = completionCount,
                    StartTime = startTime,
                    CharacterId = characterId,
                    GuildId = guild.Id,
                });
                Context.SaveChanges();
                transaction.Commit();
                return new GuildQuestRewardResult(
                    GuildQuestRewardStatus.Applied,
                    guild.Id,
                    guild.Experience,
                    guild.Funds);
            } catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is DbException) {
                Logger.LogError(ex, "Failed to award guild reward for quest {QuestId} start {StartTime} completion {CompletionCount} to character {CharacterId}",
                    questId, startTime, completionCount, characterId);
                return new GuildQuestRewardResult(GuildQuestRewardStatus.Failed);
            }
        }

        // Note: GuildMembers must be loaded separately.
        private Guild? LoadGuild(long guildId, string guildName) {
            IQueryable<Model.Guild> query = guildId > 0
                ? Context.Guild.Where(guild => guild.Id == guildId)
                : Context.Guild.Where(guild => guild.Name == guildName);
            return query
                .Join(Context.Character, guild => guild.LeaderId, character => character.Id,
                    (guild, character) => new Tuple<Model.Guild, Model.Character>(guild, character))
                .AsEnumerable()
                .Select(entry => {
                    Model.Guild guild = entry.Item1;
                    Character character = entry.Item2;
                    return new Guild(guild.Id, guild.Name, character.AccountId, character.Id, character.Name) {
                        Emblem = guild.Emblem,
                        Notice = guild.Notice,
                        CreationTime = guild.CreationTime.ToEpochSeconds(),
                        Focus = guild.Focus,
                        Experience = guild.Experience,
                        Funds = guild.Funds,
                        HouseRank = guild.HouseRank,
                        HouseTheme = guild.HouseTheme,
                        Ranks = guild.Ranks.Select((rank, i) => new GuildRank {
                            Id = (byte) i,
                            Name = rank.Name,
                            Permission = rank.Permission,
                        }).ToArray(),
                        Buffs = guild.Buffs.Select(skill => new GuildBuff {
                            Id = skill.Id,
                            Level = skill.Level,
                            ExpiryTime = skill.ExpiryTime,
                        }).ToList(),
                        Posters = guild.Posters.Select(poster => new GuildPoster {
                            Id = poster.Id,
                            Picture = poster.Picture,
                            OwnerId = poster.OwnerId,
                            OwnerName = poster.OwnerName,
                        }).ToList(),
                        Npcs = guild.Npcs.Select(npc => new GuildNpc {
                            Type = npc.Type,
                            Level = npc.Level,
                        }).ToList(),
                    };
                })
                .FirstOrDefault();
        }
    }
}
