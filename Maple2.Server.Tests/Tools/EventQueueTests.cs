using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Maple2.Tools.Scheduler;
using Serilog;

namespace Maple2.Server.Tests.Tools;

public class EventQueueTests {
    private readonly Serilog.Core.Logger logger = new LoggerConfiguration().CreateLogger();

    [OneTimeTearDown]
    public void OneTimeTearDown() {
        logger.Dispose();
    }

    [Test]
    public void Schedule_ImmediateTask_Executes() {
        var queue = new EventQueue(logger);
        queue.Start();
        bool called = false;
        queue.Schedule(() => called = true);
        queue.InvokeAll();
        Assert.That(called, Is.True);
    }

    [Test]
    public void Schedule_DelayedTask_ExecutesAfterDelay() {
        var queue = new EventQueue(logger);
        queue.Start();
        long calledAt = long.MinValue;
        long scheduledAt = Environment.TickCount64;
        queue.Schedule(() => calledAt = Environment.TickCount64, TimeSpan.FromMilliseconds(50));

        PumpUntil(queue, () => calledAt != long.MinValue);
        Assert.That(calledAt - scheduledAt, Is.GreaterThanOrEqualTo(50));
    }

    [Test]
    public void ScheduleRepeated_ExecutesMultipleTimes() {
        var queue = new EventQueue(logger);
        queue.Start();
        var calls = new List<long>();
        queue.ScheduleRepeated(() => calls.Add(Environment.TickCount64), TimeSpan.FromMilliseconds(30));

        PumpUntil(queue, () => calls.Count == 3);
        for (int i = 1; i < calls.Count; i++) {
            Assert.That(calls[i] - calls[i - 1], Is.GreaterThanOrEqualTo(30));
        }
    }

    [Test]
    public void ScheduleRepeated_StrictMode_ExecutesAtFixedIntervals() {
        var queue = new EventQueue(logger);
        queue.Start();
        var calls = new List<long>();
        long scheduledAt = Environment.TickCount64;
        queue.ScheduleRepeated(() => calls.Add(Environment.TickCount64), TimeSpan.FromMilliseconds(20), strict: true);

        PumpUntil(queue, () => calls.Count == 2);
        Assert.That(calls[1] - scheduledAt, Is.GreaterThanOrEqualTo(20));
    }

    [Test]
    public void ScheduleRepeated_SkipFirst_SkipsInitialExecution() {
        var queue = new EventQueue(logger);
        queue.Start();
        long calledAt = long.MinValue;
        long scheduledAt = Environment.TickCount64;
        queue.ScheduleRepeated(() => calledAt = Environment.TickCount64, TimeSpan.FromMilliseconds(20), skipFirst: true);

        PumpUntil(queue, () => calledAt != long.MinValue);
        Assert.That(calledAt - scheduledAt, Is.GreaterThanOrEqualTo(20));
    }

    [Test]
    public void DrainImmediateFinishesNestedOneShotWorkAfterStopWithoutRunningTimers() {
        var queue = new EventQueue(logger);
        var calls = new List<int>();
        queue.ScheduleRepeated(() => calls.Add(99), TimeSpan.FromSeconds(1));
        queue.Schedule(() => {
            calls.Add(1);
            queue.Schedule(() => calls.Add(2));
        });
        queue.Stop();

        queue.DrainImmediate();
        Assert.That(calls, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(queue.Running, Is.False);
        Assert.That(queue.Queued, Is.Zero);
    }

    [Test]
    public async Task DrainImmediateWaitsForTheAlreadyDequeuedCallbackAndItsFollowup() {
        var queue = new EventQueue(logger);
        queue.Start();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = new List<int>();
        queue.Schedule(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) {
                throw new TimeoutException("Callback release was not signaled.");
            }
            calls.Add(1);
            queue.Schedule(() => calls.Add(2));
        });
        Task pump = Task.Run(queue.InvokeAll);
        Task drain = Task.CompletedTask;
        bool waited = false;
        try {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            drain = Task.Run(queue.DrainImmediate);
            waited = !drain.Wait(TimeSpan.FromMilliseconds(100));
            queue.InvokeAll(); // A second pump must not block behind the callback.
        } finally {
            release.Set();
            await Task.WhenAll(pump, drain).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(waited, Is.True);
        Assert.That(calls, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void DrainImmediatePropagatesFailuresAndBoundsSelfReschedulingWork() {
        var queue = new EventQueue(logger);
        queue.Schedule(() => throw new InvalidOperationException("Injected callback failure."));
        InvalidOperationException? failure = Assert.Throws<InvalidOperationException>(queue.DrainImmediate);
        Assert.That(failure!.Message, Is.EqualTo("Injected callback failure."));

        int calls = 0;
        void Repeat() {
            calls++;
            queue.Schedule(Repeat);
        }
        queue.Schedule(Repeat);
        Assert.Throws<InvalidOperationException>(queue.DrainImmediate);
        Assert.That(calls, Is.EqualTo(10000));
        Assert.That(queue.Queued, Is.EqualTo(1));
    }

    private static void PumpUntil(EventQueue queue, Func<bool> completed) {
        Assert.That(() => {
            queue.InvokeAll();
            return completed();
        }, Is.True.After(5000, 10));
    }
}
