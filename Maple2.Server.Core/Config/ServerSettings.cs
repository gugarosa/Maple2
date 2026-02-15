using Maple2.Model.Enum;

namespace Maple2.Server.Core.Config;

public sealed class ServerSettings {
    public ExpSection Exp { get; set; } = new();
    public LootSection Loot { get; set; } = new();
    public MesosSection Mesos { get; set; } = new();
    public MobSection Mob { get; set; } = new();

    public sealed class ExpSection {
        public float Global { get; set; } = 1.0f;
        public float Kill { get; set; } = 1.0f;
        public float Quest { get; set; } = 1.0f;
        public float Dungeon { get; set; } = 1.0f;
        public float Prestige { get; set; } = 1.0f;
        public float Mastery { get; set; } = 1.0f;
    }


    public sealed class LootSection {
        public float GlobalDropRate { get; set; } = 1.0f;
        public float BossDropRate { get; set; } = 1.0f;
        public float RareDropRate { get; set; } = 1.0f;
    }

    public sealed class MesosSection {
        public float DropRate { get; set; } = 1.0f;
        public float PerLevelMin { get; set; } = 1.0f;
        public float PerLevelMax { get; set; } = 3.0f;
    }
    public sealed class MobSection {
        // Combat tuning
        public float DamageDealtRate { get; set; } = 1.0f; 
        public float DamageTakenRate { get; set; } = 1.0f; 
        public float EnemyHpScale { get; set; } = 1.0f; 
        public int EnemyLevelOffset { get; set; } = 0; 

        // Despawn tuning (seconds). 0 or negative disables capping
        public float DeathDespawnCapSeconds { get; set; } = 0.0f;
        public float BossDeathDespawnCapSeconds { get; set; } = 0.0f;
    }
}

public static class ServerSettingsExtensions {
    public static float ExpMultiplier(this ServerSettings settings, ExpType type) {
        float g = settings.Exp.Global;
        return type switch {
            ExpType.monster or ExpType.monsterBoss or ExpType.monsterElite or ExpType.assist or ExpType.assistBonus => g * settings.Exp.Kill,
            ExpType.quest or ExpType.epicQuest or ExpType.mission or ExpType.questEtc => g * settings.Exp.Quest,
            ExpType.dungeonClear or ExpType.dungeonBoss or ExpType.dungeonRelative => g * settings.Exp.Dungeon,
            ExpType.fishing or ExpType.gathering or ExpType.manufacturing or ExpType.arcade or ExpType.miniGame or ExpType.userMiniGame or ExpType.musicMastery1 or ExpType.musicMastery2 or ExpType.musicMastery3 or ExpType.musicMastery4 => g * settings.Exp.Mastery,
            _ => g,
        };
    }
}