using Maple2.Database.Model;
using Microsoft.EntityFrameworkCore;

namespace Maple2.Database.Storage;

public partial class GameStorage {
    public partial class Request {
        public DateTime GetLastDailyReset() {
            ServerInfo? dailyReset = Context.ServerInfo.Find("DailyReset");
            return dailyReset?.LastModified ?? CreateServerInfo("DailyReset");
        }

        public DateTime GetLastWeeklyReset() {
            ServerInfo? weeklyReset = Context.ServerInfo.Find("WeeklyReset");
            return weeklyReset?.LastModified ?? CreateServerInfo("WeeklyReset");
        }

        public DateTime GetLastMonthlyReset() {
            ServerInfo? monthlyReset = Context.ServerInfo.Find("MonthlyReset");
            return monthlyReset?.LastModified ?? CreateServerInfo("MonthlyReset");
        }

        private DateTime CreateServerInfo(string key) {
            var model = new ServerInfo {
                Key = key,
                LastModified = DateTime.Now,
            };
            Context.ServerInfo.Add(model);
            Context.SaveChanges();

            return model.LastModified;
        }

        public void DailyReset() {
            lock (Context) {
                using var transaction = Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
                ServerInfo? serverInfo = Context.ServerInfo.Find("DailyReset");
                if (serverInfo == null) {
                    serverInfo = new ServerInfo { Key = "DailyReset", LastModified = DateTime.Now };
                    Context.ServerInfo.Add(serverInfo);
                } else {
                    serverInfo.LastModified = DateTime.Now;
                    Context.Update(serverInfo);
                }
                Context.SaveChanges();

                // Online accounts reset through their session's checked save, including its concurrency tokens.
                Context.Database.ExecuteSqlRaw("""
                    UPDATE `account`
                    SET `PrestigeExp` = `PrestigeCurrentExp`, `PrestigeLevelsGained` = 0,
                        `PremiumRewardsClaimed` = '[]', `MarketLimits` = JSON_SET(`MarketLimits`, '$.MesoListed', 0)
                    WHERE `Online` = 0
                    """);
                Context.Database.ExecuteSqlRaw("""
                    UPDATE `character-config` config
                    JOIN `character` c ON c.`Id` = config.`CharacterId`
                    JOIN `account` a ON a.`Id` = c.`AccountId`
                    SET config.`GatheringCounts` = JSON_OBJECT(), config.`InstantRevivalCount` = 0
                    WHERE a.`Online` = 0 OR c.`Channel` < 0
                    """);
                Context.Database.ExecuteSqlRaw("UPDATE `nurturing` SET `PlayedBy` = '[]'");
                Context.Database.ExecuteSqlRaw("UPDATE `home` SET `DecorationRewardTimestamp` = 0 WHERE `AccountId` IN (SELECT `Id` FROM `account` WHERE `Online` = 0)");
                ResetOfflineShops(1);
                Context.Database.ExecuteSqlRaw("""
                    UPDATE `dungeon-record` record
                    LEFT JOIN `character` c ON NOT record.`AccountWide` AND c.`Id` = record.`OwnerId`
                    JOIN `account` a ON a.`Id` = IF(record.`AccountWide`, record.`OwnerId`, c.`AccountId`)
                    SET record.`CurrentSubClears` = 0, record.`ExtraCurrentSubClears` = 0
                    WHERE a.`Online` = 0 OR (NOT record.`AccountWide` AND c.`Channel` < 0)
                    """);
                transaction.Commit();
            }
        }

        public void WeeklyReset() {
            lock (Context) {
                using var transaction = Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
                ServerInfo? serverInfo = Context.ServerInfo.Find("WeeklyReset");
                if (serverInfo == null) {
                    serverInfo = new ServerInfo { Key = "WeeklyReset", LastModified = DateTime.Now };
                    Context.ServerInfo.Add(serverInfo);
                } else {
                    serverInfo.LastModified = DateTime.Now;
                    Context.Update(serverInfo);
                }
                Context.SaveChanges();

                Context.Database.ExecuteSqlRaw("UPDATE `guild-member` SET `WeeklyContribution` = 0");
                Context.Database.ExecuteSqlRaw("UPDATE `account` SET `PrestigeRewardsClaimed` = '[]' WHERE `Online` = 0");
                ResetOfflineShops(2);
                Context.Database.ExecuteSqlRaw("""
                    UPDATE `dungeon-record` record
                    LEFT JOIN `character` c ON NOT record.`AccountWide` AND c.`Id` = record.`OwnerId`
                    JOIN `account` a ON a.`Id` = IF(record.`AccountWide`, record.`OwnerId`, c.`AccountId`)
                    SET record.`CurrentClears` = 0, record.`ExtraCurrentClears` = 0
                    WHERE a.`Online` = 0 OR (NOT record.`AccountWide` AND c.`Channel` < 0)
                    """);
                transaction.Commit();
            }
        }

        public void MonthlyReset() {
            lock (Context) {
                using var transaction = Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
                ServerInfo? serverInfo = Context.ServerInfo.Find("MonthlyReset");
                if (serverInfo == null) {
                    serverInfo = new ServerInfo { Key = "MonthlyReset", LastModified = DateTime.Now };
                    Context.ServerInfo.Add(serverInfo);
                } else {
                    serverInfo.LastModified = DateTime.Now;
                    Context.Update(serverInfo);
                }
                Context.SaveChanges();

                Context.Database.ExecuteSqlRaw("UPDATE `account` SET `MarketLimits` = JSON_SET(MarketLimits, '$.MesoPurchased', 0) WHERE `Online` = 0");
                transaction.Commit();
            }
        }

        private void ResetOfflineShops(int interval) {
            Context.Database.ExecuteSqlInterpolated($"""
                UPDATE `character-shop-data` shops
                SET shops.`RestockCount` = 0
                WHERE shops.`Interval` = {interval} AND shops.`OwnerId` NOT IN (
                    SELECT `Id` FROM `account` WHERE `Online` = 1
                    UNION SELECT c.`Id` FROM `character` c JOIN `account` a ON a.`Id` = c.`AccountId`
                    WHERE a.`Online` = 1 AND c.`Channel` >= 0)
                """);
            Context.Database.ExecuteSqlInterpolated($"""
                UPDATE `character-shop-item-data` items
                JOIN `character-shop-data` shops ON shops.`ShopId` = items.`ShopId` AND shops.`OwnerId` = items.`OwnerId`
                SET items.`StockPurchased` = 0
                WHERE shops.`Interval` = {interval} AND shops.`OwnerId` NOT IN (
                    SELECT `Id` FROM `account` WHERE `Online` = 1
                    UNION SELECT c.`Id` FROM `character` c JOIN `account` a ON a.`Id` = c.`AccountId`
                    WHERE a.`Online` = 1 AND c.`Channel` >= 0)
                """);
        }
    }
}
