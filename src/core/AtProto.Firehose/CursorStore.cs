namespace AtProto.Firehose;

/// <summary>Persists the last processed firehose sequence so a restart can resume without gaps.</summary>
public interface ICursorStore
{
    ValueTask<long?> GetAsync(CancellationToken cancellationToken = default);
    ValueTask SetAsync(long seq, CancellationToken cancellationToken = default);
}

/// <summary>An in-memory cursor store (no durability; useful for tests).</summary>
public sealed class InMemoryCursorStore : ICursorStore
{
    private long? _seq;
    public ValueTask<long?> GetAsync(CancellationToken cancellationToken = default) => new(_seq);
    public ValueTask SetAsync(long seq, CancellationToken cancellationToken = default)
    {
        _seq = seq;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A file-backed cursor store: a single integer written atomically via replace.</summary>
public sealed class FileCursorStore(string path) : ICursorStore
{
    private readonly string _path = path;

    public async ValueTask<long?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return null;
        string text = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
        return long.TryParse(text, out long seq) ? seq : null;
    }

    public async ValueTask SetAsync(long seq, CancellationToken cancellationToken = default)
    {
        string tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, seq.ToString(), cancellationToken).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }
}
