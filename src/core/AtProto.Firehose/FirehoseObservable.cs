namespace AtProto.Firehose;

/// <summary>
/// A tiny BCL-only bridge from <see cref="IAsyncEnumerable{T}"/> to <see cref="IObservable{T}"/>.
/// The <see cref="IObservable{T}"/> interface lives in the runtime, so exposing this seam needs
/// no System.Reactive dependency. Consumers that want operators (GroupBy/Replay/Buffer) opt into
/// Rx.NET themselves — that stays scoped to the projection layer (AppView).
/// </summary>
public static class FirehoseObservable
{
    public static IObservable<T> ToObservable<T>(this IAsyncEnumerable<T> source) =>
        new AsyncEnumerableObservable<T>(source);

    private sealed class AsyncEnumerableObservable<T>(IAsyncEnumerable<T> source) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            var cts = new CancellationTokenSource();
            _ = PumpAsync(observer, cts.Token);
            return new Unsubscriber(cts);
        }

        private async Task PumpAsync(IObserver<T> observer, CancellationToken ct)
        {
            try
            {
                await foreach (T item in source.WithCancellation(ct).ConfigureAwait(false))
                {
                    // Stop delivering promptly on unsubscribe even if the source itself
                    // does not observe the cancellation token between items.
                    ct.ThrowIfCancellationRequested();
                    observer.OnNext(item);
                }
                observer.OnCompleted();
            }
            catch (OperationCanceledException)
            {
                // unsubscribed; deliver nothing further
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        }

        private sealed class Unsubscriber(CancellationTokenSource cts) : IDisposable
        {
            public void Dispose()
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }
}
