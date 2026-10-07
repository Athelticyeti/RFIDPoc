using System.Threading.Channels;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace KQ.Brs.Core.Persistence;

/// <summary>
/// Writes to SQLite off the engine thread. Callers enqueue snapshots; one background task applies them in batches
/// (up to 500 operations or every 250 ms) inside a single transaction.
/// </summary>
public sealed class PersistenceWriter : IAsyncDisposable
{
    private readonly BrsStore _store;
    // Each entry is either a write (Apply) or a flush marker (Flushed), completed after the batch commits.
    private readonly Channel<(Action<SqliteCommand>? Apply, TaskCompletionSource? Flushed)> _ops =
        Channel.CreateUnbounded<(Action<SqliteCommand>?, TaskCompletionSource?)>(new() { SingleReader = true });
    private readonly Task _loop;
    private long _written;

    public PersistenceWriter(BrsStore store)
    {
        _store = store;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Raised on the writer thread when a batch fails (data from that batch is lost).</summary>
    public event Action<string>? Error;

    public long Written => Interlocked.Read(ref _written);

    public void SaveBag(Bag bag)
    {
        var copy = bag.Clone();
        Enqueue(cmd => BrsStore.UpsertBag(cmd, copy));
    }

    public void AppendEvent(BagEvent e) => Enqueue(cmd => BrsStore.InsertEvent(cmd, e));

    public void SaveException(BagException e)
    {
        var copy = e.Clone();
        Enqueue(cmd => BrsStore.UpsertException(cmd, copy));
    }

    public void SaveMessage(TypeBMessage m) => Enqueue(cmd => BrsStore.UpsertMessage(cmd, m));

    public void SaveBinding(string epc, string plate, DateTimeOffset utc) => Enqueue(cmd => BrsStore.UpsertBinding(cmd, epc, plate, utc));

    public void DeleteBinding(string epc) => Enqueue(cmd =>
    {
        cmd.CommandText = "DELETE FROM bindings WHERE epc = $epc";
        cmd.Parameters.AddWithValue("$epc", epc);
    });

    public void SaveScanWindow(ScanWindowSummary w) => Enqueue(cmd => BrsStore.InsertScanWindow(cmd, w));

    public void SetCounter(string name, int value) => Enqueue(cmd => BrsStore.SetCounter(cmd, name, value));

    public void LogReaderStatus(DateTimeOffset utc, string status, string? reason) => Enqueue(cmd => BrsStore.InsertReaderLog(cmd, utc, status, reason));

    /// <summary>Completes when everything enqueued so far has been written.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ops.Writer.TryWrite((null, done))) done.TrySetResult();
        return done.Task;
    }

    private void Enqueue(Action<SqliteCommand> op) => _ops.Writer.TryWrite((op, null));

    private async Task LoopAsync()
    {
        var batch = new List<(Action<SqliteCommand>? Apply, TaskCompletionSource? Flushed)>(500);
        while (await _ops.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Give a burst a moment to accumulate so it lands in one transaction.
            await Task.Delay(250).ConfigureAwait(false);
            while (batch.Count < 500 && _ops.Reader.TryRead(out var op))
                batch.Add(op);
            WriteBatch(batch);
            batch.Clear();
        }
    }

    private void WriteBatch(List<(Action<SqliteCommand>? Apply, TaskCompletionSource? Flushed)> batch)
    {
        try
        {
            using var connection = _store.Open();
            using var tx = connection.BeginTransaction();
            var writes = 0;
            foreach (var (apply, _) in batch)
            {
                if (apply == null) continue;
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                apply(cmd);
                cmd.ExecuteNonQuery();
                writes++;
            }
            tx.Commit();
            Interlocked.Add(ref _written, writes);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Database write failed: {ex.Message}");
        }
        finally
        {
            // Flush markers complete only after the commit, so callers see the data on disk.
            foreach (var (_, flushed) in batch) flushed?.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ops.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }
}
