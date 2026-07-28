namespace AtProto.Firehose.Tests;

public sealed class CursorAndObservableTests
{
    [Fact]
    public async Task File_cursor_store_round_trips_and_survives_reopen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"atproto-cursor-{Guid.NewGuid():N}");
        try
        {
            var store = new FileCursorStore(path);
            Assert.Null(await store.GetAsync());

            await store.SetAsync(42);
            Assert.Equal(42, await store.GetAsync());

            await store.SetAsync(1_000_000);
            // a fresh instance (simulating a restart) reads the persisted value
            Assert.Equal(1_000_000, await new FileCursorStore(path).GetAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Observable_seam_delivers_all_items_then_completes()
    {
        var received = new List<int>();
        var done = new TaskCompletionSource();

        IObservable<int> observable = Source().ToObservable();
        using (observable.Subscribe(new DelegateObserver<int>(received.Add, () => done.SetResult())))
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([0, 1, 2, 3, 4], received);

        static async IAsyncEnumerable<int> Source()
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }
    }

    [Fact]
    public async Task Observable_seam_stops_pumping_after_dispose()
    {
        int count = 0;
        var first = new TaskCompletionSource();

        IObservable<int> observable = Infinite().ToObservable();
        IDisposable sub = observable.Subscribe(new DelegateObserver<int>(
            _ => { if (Interlocked.Increment(ref count) == 1) first.TrySetResult(); },
            () => { }));

        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sub.Dispose();

        // After unsubscribe the pump must quiesce: allow one in-flight item to settle, then
        // assert the count is stable across a second window (no ongoing delivery).
        await Task.Delay(200);
        int settled = Volatile.Read(ref count);
        await Task.Delay(200);
        Assert.Equal(settled, Volatile.Read(ref count));

        static async IAsyncEnumerable<int> Infinite()
        {
            while (true)
            {
                await Task.Delay(5);
                yield return 1;
            }
        }
    }

    private sealed class DelegateObserver<T>(Action<T> onNext, Action onCompleted) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnCompleted() => onCompleted();
        public void OnError(Exception error) => throw error;
    }
}
