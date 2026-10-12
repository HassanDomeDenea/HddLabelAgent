namespace HddLabelAgent;

/// <summary>Runs code on the tray's UI thread.</summary>
public sealed class Ui
{
    private readonly Control _anchor = new();

    public Ui()
    {
        // The handle ties the control to the thread that creates it, which must be the UI thread.
        _ = _anchor.Handle;
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _anchor.BeginInvoke(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    public void Post(Action work) => _anchor.BeginInvoke(work);
}

/// <summary>
/// Remembers which web sites may print. A site that is not known yet makes the agent ask the person at
/// the computer, once; the answer "yes" is kept, "no" lasts until the agent restarts.
/// </summary>
public sealed class OriginGate(SettingsStore store, Ui ui)
{
    private const int MB_TOPMOST = 0x40000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Task<bool>> _asking = new();
    private readonly HashSet<string> _refused = [];

    public bool IsAllowed(string origin) => store.Current.AllowedOrigins.Contains(origin);

    public Task<bool> AskAsync(string origin)
    {
        lock (_lock)
        {
            if (_refused.Contains(origin)) return Task.FromResult(false);
            if (!_asking.TryGetValue(origin, out var pending)) _asking[origin] = pending = Ask(origin);
            return pending;
        }
    }

    private async Task<bool> Ask(string origin)
    {
        // Leave the lock before the dialog opens.
        await Task.Yield();
        var allowed = false;
        try
        {
            allowed = await ui.Run(() => MessageBox.Show(
                $"الموقع التالي يريد استخدام طابعة الملصقات:\n\n{origin}\n\nهل تسمح له بذلك؟\n\n" +
                "This site wants to use the label printer. Allow it?",
                Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2, (MessageBoxOptions)MB_TOPMOST) == DialogResult.Yes);

            if (allowed) store.Update(s => s.AllowedOrigins.Add(origin));
            Log.Write($"{origin} was {(allowed ? "allowed" : "refused")} by the user");
            return allowed;
        }
        finally
        {
            lock (_lock)
            {
                _asking.Remove(origin);
                if (!allowed) _refused.Add(origin);
            }
        }
    }
}
