using Maple2.Server.Game.Model.Enum;

namespace Maple2.Server.Game.Model.ActorStateComponent;

public class TaskState {
    private FieldNpc Actor { get; }

    private readonly PriorityQueue<NpcTask, NpcTaskPriority> taskQueue;
    private readonly NpcTask?[] runningTasks;
    private bool isPendingStart;

    public TaskState(FieldNpc actor) {
        Actor = actor;

        var comparer = Comparer<NpcTaskPriority>.Create((item1, item2) => item2.CompareTo(item1));

        taskQueue = new PriorityQueue<NpcTask, NpcTaskPriority>(comparer);
        runningTasks = new NpcTask?[(int) NpcTaskPriority.Count];
    }

    private NpcTaskStatus QueueTask(NpcTask task) {
        NpcTask? queued = runningTasks[task.PriorityValue];

        if (queued != null && !task.ShouldOverride(queued)) {
            return NpcTaskStatus.Cancelled;
        }

        queued?.Cancel();

        if (taskQueue.TryPeek(out NpcTask? currentTask, out _) && currentTask.PriorityValue < task.PriorityValue) {
            if (currentTask.CancelOnInterrupt) {
                currentTask.Cancel();
            } else {
                currentTask.Pause();
            }
        }

        runningTasks[task.PriorityValue] = task;
        taskQueue.Enqueue(task, task.Priority);

        NpcTask npcTask = taskQueue.Peek();
        if (npcTask == task) {
            isPendingStart = true;

            return NpcTaskStatus.Running;
        }

        return NpcTaskStatus.Pending;
    }

    private void FinishTask(NpcTask task) {
        if (runningTasks[task.PriorityValue] != task) {
            return;
        }

        runningTasks[task.PriorityValue] = null;

        if (!taskQueue.TryPeek(out NpcTask? currentTask, out _) || currentTask != task) {
            return;
        }

        taskQueue.Dequeue();

        if (!taskQueue.TryPeek(out currentTask, out _)) {
            return;
        }
        isPendingStart = true;
    }

    public void Update(long tickCount) {
        if (!isPendingStart) {
            return;
        }

        // Resume callbacks may finish a task and schedule its successor for the next tick.
        isPendingStart = false;
        while (taskQueue.TryPeek(out NpcTask? finished, out _) && finished.IsDone) {
            taskQueue.Dequeue();
        }
        if (taskQueue.TryPeek(out NpcTask? task, out _)) {
            task.Resume();
        }
    }

    public abstract class NpcTask {
        private TaskState Queue { get; }
        public NpcTaskPriority Priority { get; }
        public int PriorityValue => (int) Priority;
        public NpcTaskStatus Status { get; private set; }
        public bool IsDone => Status is NpcTaskStatus.Cancelled or NpcTaskStatus.Complete;
        public virtual bool CancelOnInterrupt { get; }

        protected NpcTask(TaskState queue, NpcTaskPriority priority) {
            Queue = queue;
            Priority = priority;

            Status = queue.QueueTask(this);
        }

        internal void Resume() {
            Status = NpcTaskStatus.Running;

            TaskResumed();
        }

        protected virtual void TaskResumed() { }

        public virtual bool ShouldOverride(NpcTask task) {
            return (int) Priority >= (int) task.Priority;
        }

        public void Pause() {
            if (Status != NpcTaskStatus.Running) {
                return;
            }

            Status = NpcTaskStatus.Pending;

            TaskPaused();
        }

        protected virtual void TaskPaused() { }

        public void Finish(bool isCompleted) {
            if (IsDone) {
                return;
            }

            Status = isCompleted ? NpcTaskStatus.Complete : NpcTaskStatus.Cancelled;

            Queue.FinishTask(this);
            TaskFinished(isCompleted);
        }

        protected virtual void TaskFinished(bool isCompleted) { }

        public void Cancel() {
            Finish(false);
        }

        public void Completed() {
            Finish(true);
        }

        public override string ToString() {
            return $"{GetType().Name} (Priority: {Priority}, Status: {Status})";
        }
    }
}
