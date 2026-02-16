using Maple2.Model.Game;
using Maple2.Server.Game.Packets;

namespace Maple2.Server.Game.Trigger;

public partial class TriggerContext {
    public void GiveGuildExp(int boxId, int type) { }

    public void GuildVsGameEndGame() { }

    public void GuildVsGameGiveContribution(int teamId, bool isWin, string description) { }

    public void GuildVsGameGiveReward(string type, int teamId, bool isWin, string description) { }

    public void GuildVsGameLogResult(string description) { }

    public void GuildVsGameLogWonByDefault(int teamId, string description) { }

    public void GuildVsGameResult(string description) { }

    public void GuildVsGameScoreByUser(int boxId, int score, string description) { }

    public void SetUserValueFromGuildVsGameScore(int teamId, string key) { }

    public void SetUserValueFromUserCount(int boxId, string key, int userTagId) {
        int count;
        if (userTagId > 0) {
            count = PlayersInBox(boxId).Count(p => p.TagId == userTagId);
        } else {
            count = PlayersInBox(boxId).Count();
        }
        Field.UserValues[key] = count;
    }

    public void UserValueToNumberMesh(string key, int startMeshId, int digitCount) {
        Field.UserValues.TryGetValue(key, out int value);
        // Display value as digits using meshes. Each digit position uses 10 mesh IDs (0-9).
        // Rightmost digit starts at startMeshId, next at startMeshId+10, etc.
        for (int i = 0; i < digitCount; i++) {
            int digit = value % 10;
            value /= 10;
            int baseMeshId = startMeshId + i * 10;
            for (int d = 0; d < 10; d++) {
                int meshId = baseMeshId + d;
                if (Objects.Meshes.TryGetValue(meshId, out TriggerObjectMesh? mesh)) {
                    mesh.Visible = d == digit;
                    Broadcast(TriggerPacket.Update(mesh));
                }
            }
        }
    }

    #region Conditions
    public bool GuildVsGameScoredTeam(int teamId) {
        return false;
    }

    public bool GuildVsGameWinnerTeam(int teamId) {
        return false;
    }
    #endregion
}
