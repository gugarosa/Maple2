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
        bool called = false;
        queue.Schedule(() => called = true, TimeSpan.FromMilliseconds(50));
        queue.InvokeAll();
        Assert.That(called, Is.False);
        Thread.Sleep(60);
        queue.InvokeAll();
        Assert.That(called, Is.True);
    }

    [Test]
    public void ScheduleRepeated_ExecutesMultipleTimes() {
        var queue = new EventQueue(logger);
        queue.Start();
        int count = 0;
        queue.ScheduleRepeated(() => count++, TimeSpan.FromMilliseconds(30));
        for (int i = 0; i < 3; i++) {
            Thread.Sleep(35);
            queue.InvokeAll();
        }
        Assert.That(count, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void ScheduleRepeated_StrictMode_ExecutesAtFixedIntervals() {
        var queue = new EventQueue(logger);
        queue.Start();
        int count = 0;
        queue.ScheduleRepeated(() => count++, TimeSpan.FromMilliseconds(20), strict: true);
        Thread.Sleep(25);
        queue.InvokeAll();
        Thread.Sleep(25);
        queue.InvokeAll();
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void ScheduleRepeated_SkipFirst_SkipsInitialExecution() {
        var queue = new EventQueue(logger);
        queue.Start();
        int count = 0;
        queue.ScheduleRepeated(() => count++, TimeSpan.FromMilliseconds(20), skipFirst: true);
        queue.InvokeAll();
        Assert.That(count, Is.EqualTo(0));

        // Wait with retry to handle timing imprecision on CI environments
        int maxRetries = 10;
        for (int i = 0; i < maxRetries && count == 0; i++) {
            Thread.Sleep(10);
            queue.InvokeAll();
        }
        Assert.That(count, Is.EqualTo(1));
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
}
