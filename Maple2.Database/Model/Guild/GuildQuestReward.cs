using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Maple2.Database.Model;

internal class GuildQuestReward {
    public long OwnerId { get; set; }
    public int QuestId { get; set; }
    public int CompletionCount { get; set; }
    public long StartTime { get; set; }
    public long CharacterId { get; set; }
    public long GuildId { get; set; }

    public static void Configure(EntityTypeBuilder<GuildQuestReward> builder) {
        builder.ToTable("guild-quest-reward");
        builder.HasKey(reward => new {
            reward.OwnerId,
            reward.QuestId,
            reward.StartTime,
            reward.CompletionCount,
        });
    }
}
