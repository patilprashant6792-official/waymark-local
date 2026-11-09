using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Tests.Support;

namespace Waymark.Tests.Events;

public sealed class EventBusTests
{
    private sealed record Ping(int Number);

    private sealed record Pong(string Text);

    private readonly CapturingLogger<EventBus> _logger = new();

    private EventBus CreateBus() => new(_logger);

    [Fact]
    public async Task HandlersReceiveTheEventInSubscriptionOrder()
    {
        var bus = CreateBus();
        var seen = new List<string>();
        bus.Subscribe<Ping>(e => { seen.Add($"first:{e.Number}"); return ValueTask.CompletedTask; });
        bus.Subscribe<Ping>(e => { seen.Add($"second:{e.Number}"); return ValueTask.CompletedTask; });

        await bus.PublishAsync(new Ping(7));

        Assert.Equal(["first:7", "second:7"], seen);
    }

    [Fact]
    public async Task OnlyHandlersOfTheSameEventTypeAreCalled()
    {
        var bus = CreateBus();
        var pings = 0;
        var pongs = 0;
        bus.Subscribe<Ping>(_ => { pings++; return ValueTask.CompletedTask; });
        bus.Subscribe<Pong>(_ => { pongs++; return ValueTask.CompletedTask; });

        await bus.PublishAsync(new Pong("x"));

        Assert.Equal(0, pings);
        Assert.Equal(1, pongs);
    }

    [Fact]
    public async Task PublishingWithoutSubscribersIsHarmless()
    {
        var bus = CreateBus();

        await bus.PublishAsync(new Ping(1));

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task DisposingTheSubscriptionStopsDelivery()
    {
        var bus = CreateBus();
        var calls = 0;
        var subscription = bus.Subscribe<Ping>(_ => { calls++; return ValueTask.CompletedTask; });
        await bus.PublishAsync(new Ping(1));

        subscription.Dispose();
        await bus.PublishAsync(new Ping(2));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmlessAndKeepsOtherHandlers()
    {
        var bus = CreateBus();
        var kept = 0;
        var subscription = bus.Subscribe<Ping>(_ => ValueTask.CompletedTask);
        bus.Subscribe<Ping>(_ => { kept++; return ValueTask.CompletedTask; });

        subscription.Dispose();
        subscription.Dispose();
        await bus.PublishAsync(new Ping(1));

        Assert.Equal(1, kept);
    }

    [Fact]
    public async Task AFailingHandlerIsLoggedAndTheOthersStillRun()
    {
        var bus = CreateBus();
        var reached = false;
        bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<Ping>(_ => { reached = true; return ValueTask.CompletedTask; });

        await bus.PublishAsync(new Ping(1));

        Assert.True(reached);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Contains(nameof(Ping), entry.Message);
    }

    [Fact]
    public async Task AnAsyncFailingHandlerIsLoggedToo()
    {
        var bus = CreateBus();
        bus.Subscribe<Ping>(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("late boom");
        });

        await bus.PublishAsync(new Ping(1));

        Assert.Single(_logger.Entries);
    }

    [Fact]
    public async Task AHandlerThatSubscribesDuringPublishDoesNotSeeTheCurrentEvent()
    {
        var bus = CreateBus();
        var lateCalls = 0;
        bus.Subscribe<Ping>(outer =>
        {
            bus.Subscribe<Ping>(inner => { lateCalls++; return ValueTask.CompletedTask; });
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new Ping(1));
        Assert.Equal(0, lateCalls);

        await bus.PublishAsync(new Ping(2));
        Assert.Equal(1, lateCalls);
    }

    [Fact]
    public async Task AHandlerThatUnsubscribesAnotherDuringPublishStillLetsItRunThisTime()
    {
        var bus = CreateBus();
        var secondCalls = 0;
        IDisposable? second = null;
        bus.Subscribe<Ping>(_ => { second!.Dispose(); return ValueTask.CompletedTask; });
        second = bus.Subscribe<Ping>(_ => { secondCalls++; return ValueTask.CompletedTask; });

        await bus.PublishAsync(new Ping(1));
        await bus.PublishAsync(new Ping(2));

        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public void NullHandlerIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => CreateBus().Subscribe<Ping>(null!));
}
