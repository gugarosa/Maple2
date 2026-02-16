using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Game.Dungeon;
using Maple2.PacketLib.Tools;
using Maple2.Server.Game.Manager.Field;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Trigger.Helpers;
using Maple2.Tools.Scheduler;
using Serilog;
using Serilog.Core;

namespace Maple2.Server.Game.Trigger;

public partial class TriggerContext : ITriggerContext {
    private readonly FieldTrigger owner;
    private readonly ILogger logger = Log.Logger.ForContext<TriggerContext>();

    private FieldManager Field => owner.Field;
    private TriggerCollection Objects => owner.Field.TriggerObjects;

    private float currentRandom = float.MaxValue;

    // Skip state class reference, must instantiate before using.
    private TriggerState? skipState;
    public readonly EventQueue Events;
    public long StartTick;

    public TriggerContext(FieldTrigger owner) {
        this.owner = owner;

        Events = new EventQueue(logger);
        Events.Start();
        StartTick = Environment.TickCount64;
    }

    public bool TryGetSkip([NotNullWhen(true)] out TriggerState? state) {
        if (skipState == null) {
            state = null;
            return false;
        }

        state = skipState;
        return true;
    }

    private void Broadcast(ByteWriter packet) => Field.Broadcast(packet);

    private string lastDebugKey = "";

    [Conditional("TRIGGER_DEBUG")]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal void DebugLog(string messageTemplate, params object[] args) {
        LogOnce(logger.Debug, messageTemplate, args);
    }

    [Conditional("TRIGGER_DEBUG")]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal void WarnLog(string messageTemplate, params object[] args) {
        LogOnce(logger.Warning, messageTemplate, args);
    }

    [Conditional("TRIGGER_DEBUG")]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal void ErrorLog(string messageTemplate, params object[] args) {
        LogOnce(logger.Error, messageTemplate, args);
    }

    private void LogOnce(Action<string, object[]> logAction, string messageTemplate, params object[] args) {
        string key = messageTemplate + string.Join(", ", args);
        if (key == lastDebugKey) {
            return;
        }

        logAction($"{owner.Value.Name} {messageTemplate}", args);
        lastDebugKey = key;
    }

    // Accessors
    public bool ShadowExpeditionPoints(int score) {
        ErrorLog("[GetShadowExpeditionPoints]");
        return 0 >= score;
    }

    public bool DungeonVariable(int id, int value) {
        DebugLog("[GetDungeonVariable] id:{Id}", id);
        int currentValue = Field.UserValues.GetValueOrDefault($"dungeon_{id}", 0);
        return currentValue == value;
    }

    public bool NpcDamage(int spawnPointId, float damage, OperatorType operatorType) {
        DebugLog("[GetNpcDamageRate] spawnPointId:{Id}, damage:{Damage}, operatorType:{Operator}", spawnPointId, damage, operatorType);
        FieldNpc? npc = Field.EnumerateNpcs().FirstOrDefault(n => n.SpawnPointId == spawnPointId);
        float damageRate = 1.0f;
        if (npc != null) {
            Stat health = npc.Stats.Values[BasicAttribute.Health];
            if (health.Total > 0) {
                damageRate = 1.0f - ((float) health.Current / health.Total);
            }
        }
        return operatorType switch {
            OperatorType.Greater => damageRate > damage,
            OperatorType.GreaterEqual => damageRate >= damage,
            OperatorType.Equal => Math.Abs(damageRate - damage) < 0.0001f,
            OperatorType.LessEqual => damageRate <= damage,
            OperatorType.Less => damageRate < damage,
            _ => false,
        };
    }

    public bool NpcHp(int spawnPointId, bool isRelative, int value, CompareType compareType) {
        DebugLog("[GetNpcHpRate] spawnPointId:{Id}, isRelative:{IsRelative}, value:{Value}, compareType:{CompareType}", spawnPointId, isRelative, value, compareType);
        FieldNpc? npc = Field.EnumerateNpcs().FirstOrDefault(n => n.SpawnPointId == spawnPointId);
        int hpPercent = 100;
        if (npc != null) {
            Stat health = npc.Stats.Values[BasicAttribute.Health];
            if (health.Total > 0) {
                hpPercent = (int) ((float) health.Current / health.Total * 100);
            }
        }
        return compareType switch {
            CompareType.lower => hpPercent < value,
            CompareType.lowerEqual => hpPercent <= value,
            CompareType.higher => hpPercent > value,
            CompareType.higherEqual => hpPercent >= value,
            _ => false,
        };
    }

    public bool DungeonId(int dungeonId) {
        DebugLog("[GetDungeonId]");
        return Field.DungeonId == dungeonId;
    }

    public bool DungeonLevel(int level) {
        DebugLog("[GetDungeonLevel]");
        if (Field is DungeonFieldManager dungeonField) {
            return dungeonField.DungeonMetadata.Level == level;
        }
        return false;
    }

    public bool DungeonMaxUserCount(int value) {
        DebugLog("[GetDungeonMaxUserCount]");
        return Field.Size == value;
    }

    public bool DungeonRound(int round) {
        DebugLog("[GetDungeonRoundsRequired]");
        if (Field is not DungeonFieldManager dungeonField) {
            return false;
        }
        foreach (FieldPlayer player in Field.Players.Values) {
            if (player.Session.Dungeon.UserRecord is { } record) {
                return record.Round >= round;
            }
        }
        return false;
    }

    public bool CheckUser(bool negate) {
        if (negate) {
            return Field.Players.IsEmpty;
        }
        return !Field.Players.IsEmpty;
    }

    public bool UserCount(int count) {
        return Field.Players.Count == count;
    }

    public bool CountUsers(int boxId, int userTagId, int minUsers, OperatorType operatorType, bool negate) {
        DebugLog("[GetUserCount] boxId:{BoxId}, userTagId:{TagId}", boxId, userTagId);
        if (!Objects.Boxes.TryGetValue(boxId, out TriggerBox? box)) {
            return negate;
        }

        int count;
        if (userTagId > 0) {
            count = Field.Players.Values.Count(player => player.TagId == userTagId && box.Contains(player.Position));
        } else {
            count = Field.Players.Values.Count(player => box.Contains(player.Position));
        }

        bool result = operatorType switch {
            OperatorType.Greater => count > minUsers,
            OperatorType.GreaterEqual => count >= minUsers,
            OperatorType.Equal => count == minUsers,
            OperatorType.LessEqual => count <= minUsers,
            OperatorType.Less => count < minUsers,
            _ => false,
        };
        return negate ? !result : result;
    }

    public bool NpcExtraData(int spawnId, string extraDataKey, int extraDataValue, OperatorType operatorType) {
        WarnLog("[GetNpcExtraData] spawnId:{SpawnId}, extraDataKey:{Key}, extraDataValue:{Value}, operatorType:{Operator}", spawnId, extraDataKey, extraDataValue, operatorType);
        var npc = Field.EnumerateNpcs().FirstOrDefault(npc => npc.SpawnPointId == spawnId);
        if (npc is null) {
            return false;
        }
        int extraData = npc.AiExtraData.GetValueOrDefault(extraDataKey, 0);
        return operatorType switch {
            OperatorType.Greater => extraData > extraDataValue,
            OperatorType.GreaterEqual => extraData >= extraDataValue,
            OperatorType.Equal => extraData == extraDataValue,
            OperatorType.LessEqual => extraData <= extraDataValue,
            OperatorType.Less => extraData < extraDataValue,
            _ => false,
        };
    }

    public bool DungeonPlayTime(int playSeconds) {
        DebugLog("[GetDungeonPlayTime]");
        if (Field is DungeonFieldManager dungeonField && dungeonField.Lobby != null) {
            DungeonRoomRecord record = dungeonField.Lobby.DungeonRoomRecord;
            if (record.StartTick > 0) {
                long elapsedMs = dungeonField.Lobby.FieldTick - record.StartTick;
                return elapsedMs >= playSeconds * 1000L;
            }
        }
        return false;
    }

    // Scripts seem to just check if this is "Fail"
    public bool DungeonState(string checkState) {
        DebugLog("[GetDungeonState]");
        if (Field is DungeonFieldManager dungeonField) {
            return dungeonField.DungeonRoomRecord.State.ToString() == checkState;
        }
        return checkState == "";
    }

    public bool DungeonFirstUserMissionScore(int score, OperatorType operatorType) {
        ErrorLog("[GetDungeonFirstUserMissionScore]");
        return operatorType switch {
            OperatorType.Greater => score > 0,
            OperatorType.GreaterEqual => score >= 0,
            OperatorType.Equal => score == 0,
            OperatorType.LessEqual => score <= 0,
            OperatorType.Less => score < 0,
            _ => false,
        };
    }

    public bool ScoreBoardScore(int score, OperatorType operatorType) {
        ErrorLog("[GetScoreBoardScore]");
        return operatorType switch {
            OperatorType.Greater => score > 0,
            OperatorType.GreaterEqual => score >= 0,
            OperatorType.Equal => score == 0,
            OperatorType.LessEqual => score <= 0,
            OperatorType.Less => score < 0,
            _ => false,
        };
    }

    public bool UserValue(string key, int value, bool negate) {
        WarnLog("[GetUserValue] key:{Key}", key);
        int userValue = Field.UserValues.GetValueOrDefault(key, 0);
        if (negate) {
            return userValue != value;
        }
        return userValue == value;
    }

    public void DebugString(string value, string feature) {
        logger.Debug("{Value} [{Feature}]", value, feature);
    }

    public void WriteLog(string logName, string @event, int triggerId, string subEvent, int level) {
        logger.Information("{Log}: {Event}, {TriggerId}, {SubEvent}, {Level}", logName, @event, triggerId, subEvent, level);
    }

    #region Conditions
    public bool DayOfWeek(int[] dayOfWeeks, string description, bool negate) {
        if (negate) {
            return !dayOfWeeks.Contains((int) DateTime.UtcNow.DayOfWeek + 1);
        }
        return dayOfWeeks.Contains((int) DateTime.UtcNow.DayOfWeek + 1);
    }

    public bool RandomCondition(float rate, string description) {
        if (rate < 0f || rate > 100f) {
            LogOnce(logger.Error, "[RandomCondition] Invalid rate: {Rate}", rate);
            return false;
        }

        if (currentRandom >= 100f) {
            currentRandom = Random.Shared.NextSingle() * 100;
        }

        currentRandom -= rate;
        if (currentRandom > rate) {
            return false;
        }

        currentRandom = float.MaxValue; // Reset
        return true;
    }

    public bool WaitAndResetTick(int waitTick) {
        long tickNow = Environment.TickCount64;
        if (tickNow <= StartTick + waitTick) {
            return false;
        }

        StartTick = tickNow;
        return true;
    }

    public bool WaitTick(int waitTick) {
        return Environment.TickCount64 > StartTick + waitTick;
    }
    #endregion
}
