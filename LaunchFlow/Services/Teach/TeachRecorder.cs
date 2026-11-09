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
    private readonly Queue<string> _pendingKeys = new();
    private readonly Queue<(string Type, string Target)> _pendingActions = new();
    // Keep provenance after Drain so a live host restriction can revoke already displayed clicks too.
    private readonly HashSet<string> _acceptedUrls = new(StringComparer.Ordinal);
    private readonly string _id = Guid.NewGuid().ToString("N");
    private long _epoch;
    private bool _active = true;
    private bool _disposed;
    private bool _unsupportedTargetObserved;
    private bool _policyDenied;
    private string _lastCapturedPageUrl;

    public TeachRecorder(IPage page)
    {
        var policy = EnterprisePolicy.Current;
        if (policy.DisableTeachMode || policy.DisableAutomation || !policy.IsUrlAllowed(page.Url))
            throw new InvalidOperationException("Teach recording is disabled by your organization.");
        _page = page;
        _lastCapturedPageUrl = page.Url;
        _bridge = Bridges.GetValue(page.Context, context => new Bridge(context));
        lock (_bridge.Gate)
        {
            _bridge.Sessions.Add(_id, this);
            _bridge.Revision++;
        }
    }

    public bool IsCapturing { get { lock (_bridge.Gate) return CheckPolicy() && _active && !_disposed && !_page.IsClosed; } }

    public bool CheckPolicy(params string[] additionalUrls)
    {
        lock (_bridge.Gate)
        {
            if (_disposed) return false;
            var policy = EnterprisePolicy.Current;
            if (!policy.DisableTeachMode && !policy.DisableAutomation && policy.IsUrlAllowed(_page.Url) &&
                _acceptedUrls.All(policy.IsUrlAllowed) && additionalUrls.All(policy.IsUrlAllowed))
            {
                return !_policyDenied;
            }
            _policyDenied = true;
            _active = false;
            _epoch++;
            _pending.Clear();
            _pendingKeys.Clear();
            _pendingActions.Clear();
            _acceptedUrls.Clear();
            _bridge.Revision++;
            return false;
        }
    }

    public bool UnsupportedTargetObserved { get { lock (_bridge.Gate) return _unsupportedTargetObserved; } }

    public Task SynchronizeAsync()
    {
        CheckPolicy();
        return _bridge.SynchronizeAsync();
    }

    /// <summary>
    /// Privacy-safe recorder readiness: never returns DOM contents, typed text,
    /// input attributes, full URLs or selectors to the diagnostics UI.
    /// </summary>
    public async Task<string> InspectConnectionAsync()
    {
        if (_page.IsClosed) return "closed";
        try
        {
            return await _page.EvaluateAsync<string>(
                "() => { const bridge = window.__launchFlowTeachRecorderV1;" +
                " if (!bridge || typeof bridge.status !== 'function') return 'missing';" +
                " const status = bridge.status();" +
                " return status.ready ? 'ready' : 'waiting'; }"
            ).ConfigureAwait(false) ?? "waiting";
        }
        catch (PlaywrightException)
        {
            // A navigation can destroy the execution context; the next page
            // receives an init script and a fresh handshake.
            return "navigating";
        }
    }

    public void Pause() => SetActive(false);
    public void Resume() => SetActive(true);

    private void SetActive(bool active)
    {
        lock (_bridge.Gate)
        {
            if (active)
            {
                _policyDenied = false;
                if (!CheckPolicy()) return;
            }
            if (_disposed || _page.IsClosed || _active == active) return;
            _active = active;
            _epoch++;
            _bridge.Revision++;
        }
    }

    public IReadOnlyList<(string Type, string Target)> DrainActions()
    {
        lock (_bridge.Gate)
        {
            if (!CheckPolicy()) return Array.Empty<(string, string)>();
            var result = _pendingActions.ToArray();
            _pendingActions.Clear();
            _pending.Clear();
            _pendingKeys.Clear();
            _pendingActions.Clear();
            return result;
        }
    }

    public IReadOnlyList<string> DrainKeys()
    {
        lock (_bridge.Gate)
        {
            if (!CheckPolicy()) return Array.Empty<string>();
            var result = _pendingKeys.ToArray();
            _pendingKeys.Clear();
            return result;
        }
    }

    public IReadOnlyList<string> Drain()
    {
        lock (_bridge.Gate)
        {
            if (!CheckPolicy()) return Array.Empty<string>();
            var result = _pending.ToArray();
            _pending.Clear();
            _pendingActions.Clear();
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
            _pendingKeys.Clear();
            _pendingActions.Clear();
            _acceptedUrls.Clear();
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
                if (type is not ("Click" or "Press" or "Unsupported")) return "null";
                var id = message.GetProperty("id").GetString();
                var epoch = message.GetProperty("epoch").GetInt64();
                var target = type is "Click" or "Press" ? message.GetProperty("target").GetString() : "";
                if (type == "Click" && (string.IsNullOrWhiteSpace(target) || target.Length > 4096 || !StructuralSelector.IsMatch(target))) return "null";
                if (type == "Press" && target is not ("Enter" or "Tab" or "Escape" or
                    "ArrowUp" or "ArrowDown" or "ArrowLeft" or "ArrowRight")) return "null";
                lock (Gate)
                {
                    if (id is not null && Sessions.TryGetValue(id, out var session) && session._active && !session._page.IsClosed && session._epoch == epoch)
                    {
                        // WorkflowStep has no frame/page identity: never persist a selector against a different target.
                        if (type == "Unsupported" || source.Page != session._page || source.Frame != session._page.MainFrame)
                            session._unsupportedTargetObserved = true;
                        if (!session.CheckPolicy(source.Page.Url, source.Frame.Url)) return "null";
                        if (source.Page == session._page && source.Frame == session._page.MainFrame)
                        {
                            if (type is "Click" or "Press" &&
                                !string.Equals(session._lastCapturedPageUrl, source.Frame.Url, StringComparison.Ordinal))
                            {
                                // The init script survives same-tab navigation. If the
                                // user crosses to another website, automatically teach
                                // the new location before recording clicks there.
                                // Never serialize query strings, fragments or embedded
                                // credentials: OAuth callbacks often contain secrets.
                                if (!Uri.TryCreate(session._lastCapturedPageUrl, UriKind.Absolute, out var previous) ||
                                    !Uri.TryCreate(source.Frame.Url, UriKind.Absolute, out var next) ||
                                    next.Scheme is not ("https" or "http") ||
                                    !string.IsNullOrEmpty(next.Query) ||
                                    !string.IsNullOrEmpty(next.Fragment) ||
                                    !string.IsNullOrEmpty(next.UserInfo))
                                {
                                    session._unsupportedTargetObserved = true;
                                    return "null";
                                }
                                if (!string.Equals(previous.Authority, next.Authority, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(previous.Scheme, next.Scheme, StringComparison.OrdinalIgnoreCase))
                                {
                                    var safeUrl = next.GetLeftPart(UriPartial.Path);
                                    if (safeUrl.Length > 2048 || !session.CheckPolicy(source.Frame.Url, safeUrl))
                                    {
                                        session._unsupportedTargetObserved = true;
                                        return "null";
                                    }
                                    session._acceptedUrls.Add(source.Frame.Url);
                                    session._pendingActions.Enqueue(("Navigate", safeUrl));
                                }
                                session._lastCapturedPageUrl = source.Frame.Url;
                            }
                            if (type == "Click")
                            {
                                session._acceptedUrls.Add(source.Frame.Url);
                                session._pending.Enqueue(target!);
                                session._pendingActions.Enqueue(("Click", target!));
                            }
                            else if (type == "Press")
                            {
                                session._acceptedUrls.Add(source.Frame.Url);
                                session._pendingKeys.Enqueue(target!);
                                session._pendingActions.Enqueue(("Press", target!));
                            }
                        }
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
