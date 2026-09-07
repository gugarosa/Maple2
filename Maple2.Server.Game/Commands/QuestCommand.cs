using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Session;

namespace Maple2.Server.Game.Commands;

public class QuestCommand : GameCommand {
    private readonly GameSession session;
    private readonly QuestMetadataStorage questStorage;

    public QuestCommand(GameSession session, QuestMetadataStorage questStorage) : base(AdminPermissions.Quest, "quest", "Modify a quest's state.") {
        this.session = session;
        this.questStorage = questStorage;

        var id = new Argument<int>("id", "Id of quest to modify.");
        var state = new Option<QuestState>(["--state", "-s"], () => QuestState.None, "State of the quest.");

        AddArgument(id);
        AddOption(state);
        this.SetHandler<InvocationContext, int, QuestState>(Handle, id, state);
    }

    private void Handle(InvocationContext ctx, int id, QuestState state) {
        if (!questStorage.TryGet(id, out QuestMetadata? metadata)) {
            ctx.Console.Error.WriteLine($"Quest id {id} does not exist.");
            ctx.ExitCode = 1;
            return;
        }

        session.Quest.TryGetQuest(id, out Quest? quest);
        switch (state) {
            case QuestState.Started:
                if (quest?.State == QuestState.Started) {
                    ctx.Console.Error.WriteLine("Quest is already started.");
                    return;
                }

                if (quest != null && metadata.Basic.Repeatable == 0 && !session.Quest.Remove(quest)) {
                    Fail("Could not reset quest.");
                    return;
                }
                QuestError error = session.Quest.Start(id, true);
                if (error != QuestError.none) {
                    Fail($"Could not start quest: {error}.");
                }
                break;
            case QuestState.Completed:
                if (quest == null) {
                    if (session.Quest.Start(id, true) != QuestError.none || !session.Quest.TryGetQuest(id, out quest)) {
                        Fail("Could not start quest for completion.");
                        return;
                    }
                }
                if (quest.State == QuestState.Completed) {
                    ctx.Console.Error.WriteLine("Quest is already completed.");
                    return;
                }
                if (!session.Quest.Complete(quest, true)) {
                    Fail("Could not complete quest.");
                }
                break;
        }

        return;

        void Fail(string message) {
            ctx.Console.Error.WriteLine(message);
            ctx.ExitCode = 1;
        }
    }
}
