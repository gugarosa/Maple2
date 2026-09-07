using System;
using Maple2.Server.Game.Model.ActorStateComponent;
using Maple2.Server.Game.Model.Enum;

namespace Maple2.Server.Tests.Game;

public class TaskStateTests {
    [Test]
    public void DiscardedQueueHeadDoesNotStrandTheNextLiveTask() {
        var queue = new TaskState(null!);
        var low = new RecordingTask(queue, NpcTaskPriority.IdleAction);
        var discarded = new RecordingTask(queue, NpcTaskPriority.BattleWalk);
        var high = new RecordingTask(queue, NpcTaskPriority.Interrupt);
        queue.Update(0);
        discarded.Cancel();
        high.Completed();

        queue.Update(1);

        Assert.That(low.ResumeCount, Is.EqualTo(1));
        Assert.That(low.Status, Is.EqualTo(NpcTaskStatus.Running));
        Assert.That(discarded.ResumeCount, Is.Zero);
    }

    [Test]
    public void CancellationDuringResumeKeepsTheSuccessorScheduled() {
        var queue = new TaskState(null!);
        var low = new RecordingTask(queue, NpcTaskPriority.IdleAction);
        var cancelledOnResume = new RecordingTask(queue, NpcTaskPriority.BattleWalk, task => task.Cancel());
        var high = new RecordingTask(queue, NpcTaskPriority.Interrupt);
        queue.Update(0);
        high.Completed();

        queue.Update(1);
        Assert.That(cancelledOnResume.Status, Is.EqualTo(NpcTaskStatus.Cancelled));
        Assert.That(low.ResumeCount, Is.Zero);
        queue.Update(2);

        Assert.That(low.ResumeCount, Is.EqualTo(1));
        Assert.That(low.Status, Is.EqualTo(NpcTaskStatus.Running));
    }

    [Test]
    public void CompletedQueuedEntriesAreDiscardedBeforeResuming() {
        var queue = new TaskState(null!);
        var low = new RecordingTask(queue, NpcTaskPriority.IdleAction);
        var completed = new RecordingTask(queue, NpcTaskPriority.BattleWalk);
        var high = new RecordingTask(queue, NpcTaskPriority.Interrupt);
        queue.Update(0);
        completed.Completed();
        high.Completed();

        queue.Update(1);

        Assert.That(completed.ResumeCount, Is.Zero);
        Assert.That(low.ResumeCount, Is.EqualTo(1));
    }

    [Test]
    public void TaskQueuedByResumeCallbackStartsOnTheFollowingTick() {
        var queue = new TaskState(null!);
        RecordingTask? next = null;
        var first = new RecordingTask(queue, NpcTaskPriority.IdleAction,
            _ => next = new RecordingTask(queue, NpcTaskPriority.Interrupt));

        queue.Update(0);
        Assert.That(first.ResumeCount, Is.EqualTo(1));
        Assert.That(next!.ResumeCount, Is.Zero);
        queue.Update(1);
        Assert.That(next.ResumeCount, Is.EqualTo(1));
    }

    [Test]
    public void SamePriorityReplacementResumesOnlyTheReplacement() {
        var queue = new TaskState(null!);
        var old = new RecordingTask(queue, NpcTaskPriority.IdleAction);
        var high = new RecordingTask(queue, NpcTaskPriority.Interrupt);
        queue.Update(0);
        var replacement = new RecordingTask(queue, NpcTaskPriority.IdleAction);
        high.Completed();

        queue.Update(1);
        queue.Update(2);

        Assert.That(old.Status, Is.EqualTo(NpcTaskStatus.Cancelled));
        Assert.That(old.ResumeCount, Is.Zero);
        Assert.That(replacement.ResumeCount, Is.EqualTo(1));
    }

    [Test]
    public void NewTasksDoNotResumeInsideTheirConstructors() {
        var queue = new TaskState(null!);
        var task = new RecordingTask(queue, NpcTaskPriority.IdleAction);

        Assert.That(task.ResumeCount, Is.Zero);
        queue.Update(0);
        Assert.That(task.ResumeCount, Is.EqualTo(1));
        queue.Update(1);
        Assert.That(task.ResumeCount, Is.EqualTo(1));
    }

    private sealed class RecordingTask : TaskState.NpcTask {
        private readonly Action<RecordingTask>? onResume;
        public int ResumeCount { get; private set; }

        public RecordingTask(TaskState queue, NpcTaskPriority priority, Action<RecordingTask>? onResume = null)
            : base(queue, priority) {
            this.onResume = onResume;
        }

        protected override void TaskResumed() {
            ResumeCount++;
            onResume?.Invoke(this);
        }
    }
}
