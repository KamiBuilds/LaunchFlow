using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using BindingSource = Microsoft.Playwright.BindingSource;

namespace LaunchFlow.Services;

/// <summary>Per-window capture state sharing one context-wide Playwright bridge.</summary>
public sealed class TeachRecorder : IDisposable
{
    private static readonly ConditionalWeakTable<IBrowserContext, Bridge> Bridges = new();
    private readonly Bridge _bridge;
    private readonly IPage _page;
    private readonly Queue<string> _pending = new();
    private readonly string _id = Guid.NewGuid().ToString("N");
    private long _epoch;
    private bool _active = true;
    private bool _disposed;
    private bool _unsupportedTargetObserved;

    public TeachRecorder(IPage page)
    {
        _page = page;
        _bridge = Bridges.GetValue(page.Context, context => new Bridge(context));
        lock (_bridge.Gate)
        {
            _bridge.Sessions.Add(_id, this);
            _bridge.Revision++;
        }
    }

    public bool IsCapturing { get { lock (_bridge.Gate) return _active && !_disposed && !_page.IsClosed; } }

    public bool UnsupportedTargetObserved { get { lock (_bridge.Gate) return _unsupportedTargetObserved; } }

    public Task SynchronizeAsync() => _bridge.SynchronizeAsync();

    public void Pause() => SetActive(false);
    public void Resume() => SetActive(true);

    private void SetActive(bool active)
    {
        lock (_bridge.Gate)
        {
            if (_disposed || _page.IsClosed || _active == active) return;
            _active = active;
            _epoch++;
            _bridge.Revision++;
        }
    }

    public IReadOnlyList<string> Drain()
    {
        lock (_bridge.Gate)
        {
            var result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }

    public void Dispose()
    {
        lock (_bridge.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            _pending.Clear();
            _bridge.Sessions.Remove(_id);
            _bridge.Revision++;
        }
    }

    private sealed class Bridge
    {
        private const string Binding = "__launchFlowTeachDispatchV1";
        // Validate at the native boundary too: page JavaScript can invoke or replace the binding wrapper.
        private static readonly Regex StructuralSelector = new(
            @"\A[a-z][a-z0-9-]*(?::nth-of-type\([1-9][0-9]*\))?(?: > [a-z][a-z0-9-]*(?::nth-of-type\([1-9][0-9]*\))?)*\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        private readonly IBrowserContext _context;
        private readonly SemaphoreSlim _updates = new(1, 1);
        private IAsyncDisposable? _binding;
        private bool _initialized;
        internal readonly object Gate = new();
        internal readonly Dictionary<string, TeachRecorder> Sessions = new();
        internal long Revision;

        internal Bridge(IBrowserContext context) => _context = context;

        private string Snapshot()
        {
            lock (Gate)
                return JsonSerializer.Serialize(new
                {
                    revision = Revision,
                    sessions = Sessions.Values.Where(s => s._active && !s._page.IsClosed).Select(s => new { id = s._id, epoch = s._epoch }).ToArray()
                });
        }

        private string Dispatch(BindingSource source, string json)
        {
            if (json is null || json.Length > 8192) return "null";
            try
            {
                using var document = JsonDocument.Parse(json);
                var message = document.RootElement;
                var type = message.GetProperty("type").GetString();
                if (type == "hello") return Snapshot();
                if (type is not ("Click" or "Unsupported")) return "null";
                var id = message.GetProperty("id").GetString();
                var epoch = message.GetProperty("epoch").GetInt64();
                var target = type == "Click" ? message.GetProperty("target").GetString() : "";
                if (type == "Click" && (string.IsNullOrWhiteSpace(target) || target.Length > 4096 || !StructuralSelector.IsMatch(target))) return "null";
                lock (Gate)
                {
                    if (id is not null && Sessions.TryGetValue(id, out var session) && session._active && !session._page.IsClosed && session._epoch == epoch)
                    {
                        // WorkflowStep has no frame/page identity: never persist a selector against a different target.
                        if (type == "Unsupported" || source.Page != session._page || source.Frame != session._page.MainFrame)
                            session._unsupportedTargetObserved = true;
                        else
                            session._pending.Enqueue(target!);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {
                // Page bindings are untrusted input; malformed messages must not escape into the UI.
            }
            return "null";
        }

        internal async Task SynchronizeAsync()
        {
            await _updates.WaitAsync();
            try
            {
                if (_binding is null)
                {
                    // Context registrations cannot be removed safely during an overlapping session startup.
                    _binding = await _context.ExposeBindingAsync(Binding, (BindingSource source, string json) => Dispatch(source, json));
                }
                if (!_initialized)
                {
                    await _context.AddInitScriptAsync(TeachRecorderScript.Install);
                    _initialized = true;
                }
                foreach (var page in _context.Pages)
                    foreach (var frame in page.Frames)
                    {
                        try
                        {
                            await frame.EvaluateAsync(TeachRecorderScript.Install);
                            await frame.EvaluateAsync("state => window.__launchFlowTeachRecorderV1.update(JSON.parse(state))", Snapshot());
                        }
                        catch (PlaywrightException)
                        {
                            // Navigation destroys execution contexts; the init script handshakes in the next document.
                        }
                    }
            }
            finally { _updates.Release(); }
        }
    }
}
