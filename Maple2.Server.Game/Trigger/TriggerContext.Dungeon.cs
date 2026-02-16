using Maple2.Model.Game.Dungeon;
using Maple2.Server.Game.Manager.Field;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Packets;

namespace Maple2.Server.Game.Trigger;

public partial class TriggerContext {
    public void DungeonClear(string uiType) {
        DebugLog("[DungeonClear] uiType:{UiType}", uiType);
        if (Field is not DungeonFieldManager dungeonField) {
            return;
        }

        if (uiType == "None") {
            // Do not send dungeon clear UI
            return;
        }

        dungeonField.ChangeState(Maple2.Model.Enum.DungeonState.Clear);
    }

    public void DungeonClearRound(int round) {
        DebugLog("[DungeonClearRound] round:{Round}", round);

        foreach (FieldPlayer player in Field.Players.Values) {
            if (player.Session.Dungeon.UserRecord is null) {
                continue;
            }

            player.Session.Dungeon.UserRecord.Round = round;
            player.Session.ConditionUpdate(ConditionType.dungeon_round_clear, codeLong: round);
        }
    }

    public void DungeonCloseTimer() {
        DebugLog("[DungeonCloseTimer]");
        if (Field.RoomTimer != null) {
            Field.RoomTimer.Modify(-Field.RoomTimer.Duration);
        }
    }

    public void DungeonDisableRanking() {
        DebugLog("[DungeonDisableRanking]");
        if (Field is not DungeonFieldManager dungeonField) {
            return;
        }
        dungeonField.DungeonRoomRecord.RankingDisabled = true;
    }

    public void DungeonEnableGiveUp(bool enabled) {
        DebugLog("[DungeonEnableGiveUp] enabled:{Enabled}", enabled);
        Field.Broadcast(DungeonMissionPacket.SetAbandon(enabled));
    }

    public void DungeonFail() {
        DebugLog("[DungeonFail]");
        if (Field is not DungeonFieldManager dungeonField) {
            return;
        }

        dungeonField.ChangeState(Maple2.Model.Enum.DungeonState.Fail);
    }

    public void DungeonMissionComplete(string feature, int missionId) {
        DebugLog("[DungeonMissionComplete] missionId:{MissionId}, feature:{Feature}", missionId, feature);

        foreach (FieldPlayer player in Field.Players.Values) {
            if (player.Session.Dungeon.UserRecord is null) {
                continue;
            }
            if (!player.Session.Dungeon.UserRecord.Missions.TryGetValue(missionId, out DungeonMission? missionRecord)) {
                continue;
            }

            missionRecord.Complete();
            player.Session.Send(DungeonMissionPacket.Update(missionRecord));
        }
    }

    public void DungeonMoveLapTimeToNow(int id) {
        ErrorLog("[DungeonMoveLapTimeToNow] id:{Id}", id);
    }

    public void DungeonResetTime(int seconds) {
        DebugLog("[DungeonResetTime] seconds:{Seconds}", seconds);
        if (Field is DungeonFieldManager dungeonField && dungeonField.Lobby != null) {
            RoomTimer? timer = dungeonField.Lobby.RoomTimer;
            if (timer != null) {
                int newDuration = seconds * 1000;
                timer.Modify(newDuration - timer.Duration);
            }
        }
    }

    public void DungeonSetEndTime() {
        DebugLog("[DungeonSetEndTime]");
        if (Field is DungeonFieldManager dungeonField) {
            dungeonField.DungeonRoomRecord.EndTick = Field.FieldTick;
        }
    }

    public void DungeonSetLapTime(int id, int lapTime) {
        ErrorLog("[DungeonSetLapTime] id:{Id}, lapTime:{LapTime}", id, lapTime);
    }

    public void DungeonStopTimer() {
        DebugLog("[DungeonStopTimer]");
        if (Field is DungeonFieldManager dungeonField && dungeonField.Lobby != null) {
            dungeonField.Lobby.RoomTimer?.Modify(-dungeonField.Lobby.RoomTimer.Duration);
        }
    }

    public void RandomAdditionalEffect(
        string target,
        int boxId,
        int spawnId,
        int targetCount,
        int tick,
        int waitTick,
        string targetEffect,
        int additionalEffectId
    ) {
        DebugLog("[RandomAdditionalEffect] target:{Target}, boxId:{BoxId}, spawnId:{SpawnId}, targetCount:{Count}, targetEffect:{Effect}, additionalEffectId:{Id}",
            target, boxId, spawnId, targetCount, targetEffect, additionalEffectId);

        if (additionalEffectId <= 0) {
            return;
        }

        if (target == "PlayerInBox") {
            List<FieldPlayer> players = PlayersInBox(boxId).ToList();
            int count = Math.Min(targetCount, players.Count);
            foreach (FieldPlayer player in players.OrderBy(_ => Random.Shared.Next()).Take(count)) {
                player.Buffs.AddBuff(player, player, additionalEffectId, 1, Field.FieldTick);
            }
        } else if (target == "NpcInBox") {
            List<FieldNpc> npcs = NpcsInBox(boxId).ToList();
            int count = Math.Min(targetCount, npcs.Count);
            foreach (FieldNpc npc in npcs.OrderBy(_ => Random.Shared.Next()).Take(count)) {
                npc.Buffs.AddBuff(npc, npc, additionalEffectId, 1, Field.FieldTick);
            }
        }
    }

    public void SetDungeonVariable(int varId, int value) {
        DebugLog("[SetDungeonVariable] varId:{VarId}, value:{Value}", varId, value);
        Field.UserValues[$"dungeon_{varId}"] = value;
    }

    public void SetUserValueFromDungeonRewardCount(string key, int dungeonRewardId) {
        DebugLog("[SetUserValueFromDungeonRewardCount] key:{Key}, dungeonRewardId:{RewardId}", key, dungeonRewardId);
        foreach (FieldPlayer player in Field.Players.Values) {
            DungeonRecord record = player.Session.Dungeon.GetRecord(dungeonRewardId);
            int rewardCount = player.Session.Dungeon.GetWeeklyClearCount(record);
            Field.UserValues[key] = rewardCount;
            return;
        }
    }

    public void StartTutorial() {
        ErrorLog("[StartTutorial]");
    }

    #region DarkStream
    public void DarkStreamSpawnMonster(int[] spawnIds, int score) {
        ErrorLog("[DarkStreamSpawnMonster]");
    }

    public void DarkStreamStartGame(int round) {
        ErrorLog("[DarkStreamStartGame]");
    }

    public void DarkStreamStartRound(int round, int uiDuration, int damagePenalty) {
        ErrorLog("[DarkStreamStartRound]");
    }

    public void DarkStreamClearRound(int round) {
        ErrorLog("[DarkStreamClearRound]");
    }
    #endregion

    #region ShadowExpedition
    public void ShadowExpeditionOpenBossGauge(int maxGaugePoint, string title) {
        ErrorLog("[ShadowExpeditionOpenBossGauge]");
    }

    public void ShadowExpeditionCloseBossGauge() {
        ErrorLog("[ShadowExpeditionCloseBossGauge]");
    }
    #endregion

    #region Conditions
    public bool CheckDungeonLobbyUserCount(bool negate) {
        DebugLog("[CheckDungeonLobbyUserCount]");
        if (Field is not DungeonFieldManager dungeonField) {
            return negate;
        }

        if (negate) {
            return Field.Players.Values.Count < dungeonField.Size;
        }
        return Field.Players.Values.Count >= dungeonField.Size;
    }

    public bool DungeonTimeout() {
        DebugLog("[DungeonTimeout]");
        if (Field is DungeonFieldManager dungeonField && dungeonField.Lobby != null) {
            RoomTimer? timer = dungeonField.Lobby.RoomTimer;
            if (timer != null) {
                return timer.Expired(Field.FieldTick);
            }
        }
        return false;
    }

    public bool IsDungeonRoom(bool negate) {
        DebugLog("[IsDungeonRoom]");
        if (negate) {
            return Field is not DungeonFieldManager;
        }
        return Field is DungeonFieldManager;
    }

    public bool IsPlayingMapleSurvival(bool negate) {
        ErrorLog("[IsPlayingMapleSurvival]");
        return false;
    }
    #endregion
}
