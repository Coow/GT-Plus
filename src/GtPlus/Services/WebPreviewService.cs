using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>runs one live headless browser per <see cref="GtWebElement"/> on the canvas and keeps its newest frame ready to draw. The canvas asks for a frame while it renders and gets whatever arrived last - never a wait - and pointer and key events go back the other way when the element is interactive. Everything here is session state: no frame is written to disk and nothing survives closing the document, which is what keeps a design-time-only web object out of saved files and exports</summary>
public sealed class WebPreviewService : IDisposable
{
    /// <summary>the live browser behind one web element, and the last picture it sent</summary>
    public sealed class WebView
    {
        public CdpSession? Session;
        public Bitmap?     Frame;
        public string?     Error;

        /// <summary>a browser is being launched right now; stops a second launch racing the first</summary>
        public bool Starting;

        /// <summary>what the running session is currently showing, so a change to any of it can be pushed down</summary>
        public string    SessionUrl = "";
        public PixelSize SessionSize;
        public bool      SessionTransparent;

        /// <summary>size last drawn at and when, so a resize drag settles before the page is re-laid-out</summary>
        public PixelSize RequestedSize;
        public DateTime  RequestedUtc;

        /// <summary>newest frame off the socket, waiting to be decoded; frames overwrite each other so a slow decode drops frames instead of queueing them</summary>
        public byte[]? PendingPng;
        public int     DecodeQueued;
    }

    private readonly Dictionary<GtWebElement, WebView> _views = new(ReferenceEqualityComparer.Instance);
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    /// <summary>a resize has to stop moving for this long before the page is re-laid-out at the new size</summary>
    private static readonly TimeSpan SizeSettle = TimeSpan.FromMilliseconds(350);

    /// <summary>remembered browser path from preferences; empty means "search the usual places"</summary>
    public string PreferredBrowserPath { get; set; } = "";

    private GtDocument? _document;

    /// <summary>document the previews belong to; lifecycle is driven from its web elements so an element the user deleted has its browser shut down, and replacing the document closes every session</summary>
    public GtDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            _document = value;
            Clear();
        }
    }

    /// <summary>fired on the UI thread whenever a frame or an error changed</summary>
    public event EventHandler? Changed;

    public WebPreviewService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>true when no browser could be found, which is the one failure worth explaining differently in the UI</summary>
    public bool BrowserMissing => ChromiumService.Locate(NullIfBlank(PreferredBrowserPath)) is null;

    /// <summary>the view to draw for <paramref name="element"/> at <paramref name="size"/>; registers the element and records the size the next lifecycle pass should lay the page out at. Safe to call from inside a render pass - it never blocks and never raises <see cref="Changed"/> synchronously</summary>
    public WebView Ensure(GtWebElement element, PixelSize size)
    {
        if (!_views.TryGetValue(element, out var view))
        {
            view = new WebView();
            _views[element] = view;
        }

        if (size != view.RequestedSize)
        {
            view.RequestedSize = size;
            view.RequestedUtc  = DateTime.UtcNow;
        }

        return view;
    }

    /// <summary>reloads the page in place, keeping the session</summary>
    public void Reload(GtWebElement element)
    {
        if (!_views.TryGetValue(element, out var view)) return;

        if (view.Session is { } session)
        {
            _ = session.ReloadAsync();
            return;
        }

        // no session yet means the last attempt failed; clearing the error lets the next pass try again
        view.Error      = null;
        view.SessionUrl = "";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>shuts every browser down and frees the frames; call when the document is replaced or closed</summary>
    public void Clear()
    {
        foreach (var view in _views.Values) Close(view);
        _views.Clear();
    }

    private static void Close(WebView view)
    {
        if (view.Session is { } session)
        {
            view.Session = null;
            // disposal kills a process and deletes a profile directory, neither of which the UI thread should wait on
            _ = Task.Run(async () => await session.DisposeAsync().ConfigureAwait(false));
        }

        view.Frame?.Dispose();
        view.Frame      = null;
        view.PendingPng = null;
        view.SessionUrl = "";
    }

    // ---- lifecycle ---------------------------------------------------------

    private void Tick()
    {
        if (_disposed || _views.Count == 0) return;

        var now     = DateTime.UtcNow;
        var browser = ChromiumService.Locate(NullIfBlank(PreferredBrowserPath));

        // the document is the authority on what still exists; a view left behind by a deleted element would otherwise keep a browser running forever
        var live = new HashSet<GtWebElement>(ReferenceEqualityComparer.Instance);
        if (_document is not null)
            foreach (var layer in _document.Layers)
                foreach (var el in layer.Elements)
                    if (el is GtWebElement web) live.Add(web);

        List<GtWebElement>? gone = null;
        foreach (var element in _views.Keys)
            if (!live.Contains(element))
                (gone ??= new()).Add(element);

        if (gone is not null)
        {
            foreach (var element in gone)
                if (_views.Remove(element, out var dead))
                    Close(dead);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        foreach (var (element, view) in _views)
        {
            if (view.Starting) continue;

            var url = element.Url?.Trim() ?? "";

            // a hidden element or one with no url is not something to look at, so it does not get to run a browser
            if (!element.Visible || url.Length == 0)
            {
                if (view.Session is not null || view.Frame is not null || view.Error is not null)
                {
                    Close(view);
                    view.Error = null;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                continue;
            }

            if (browser is null)
            {
                if (view.Error is null)
                {
                    view.Error = "No Chrome, Edge or Chromium found";
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                continue;
            }

            var size = view.RequestedSize;
            if (size.Width <= 0 || size.Height <= 0) continue;

            if (view.Session is null)
            {
                // one failure is not retried on the timer, only when the url changes or Reload is pressed, otherwise a dead url relaunches a browser forever
                if (view.Error is not null) continue;
                Start(element, view, browser, url, size);
                continue;
            }

            if (!string.Equals(view.SessionUrl, url, StringComparison.Ordinal))
            {
                view.SessionUrl = url;
                _ = view.Session.NavigateAsync(url);
            }

            if (size != view.SessionSize && now - view.RequestedUtc >= SizeSettle)
            {
                view.SessionSize = size;
                _ = view.Session.ResizeAsync(size.Width, size.Height);
            }

            if (element.TransparentBackground != view.SessionTransparent)
            {
                view.SessionTransparent = element.TransparentBackground;
                _ = view.Session.SetTransparentAsync(view.SessionTransparent);
            }
        }
    }

    private void Start(GtWebElement element, WebView view, string browser, string url, PixelSize size)
    {
        view.Starting           = true;
        view.SessionUrl         = url;
        view.SessionSize        = size;
        view.SessionTransparent = element.TransparentBackground;

        _ = Task.Run(async () =>
        {
            var session = await CdpSession
                .StartAsync(browser, size.Width, size.Height, element.TransparentBackground,
                            CancellationToken.None)
                .ConfigureAwait(false);

            if (session is not null)
                await session.NavigateAsync(url).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                view.Starting = false;

                if (_disposed || !_views.ContainsKey(element))
                {
                    if (session is not null) _ = session.DisposeAsync();
                    return;
                }

                if (session is null)
                {
                    view.Error = "Could not start the browser";
                    Changed?.Invoke(this, EventArgs.Empty);
                    return;
                }

                view.Session = session;
                view.Error   = null;

                session.FrameReceived += png => OnFrame(view, png);
                session.Failed        += message => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(view.Session, session)) return;
                    Close(view);
                    view.Error = message;
                    Changed?.Invoke(this, EventArgs.Empty);
                });

                Changed?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    /// <summary>a frame off the socket; frames arrive faster than they can be decoded and drawn, so the newest one simply replaces whatever was waiting and one decode is queued for it</summary>
    private void OnFrame(WebView view, byte[] png)
    {
        Volatile.Write(ref view.PendingPng, png);

        if (Interlocked.Exchange(ref view.DecodeQueued, 1) != 0) return;

        Dispatcher.UIThread.Post(() => DecodeLatest(view), DispatcherPriority.Background);
    }

    private void DecodeLatest(WebView view)
    {
        Interlocked.Exchange(ref view.DecodeQueued, 0);

        var png = Interlocked.Exchange(ref view.PendingPng, null);
        // a frame that arrived just as the session was closed belongs to a page that is gone
        if (png is null || _disposed || view.Session is null) return;

        try
        {
            using var ms = new MemoryStream(png);
            var bitmap = new Bitmap(ms);
            view.Frame?.Dispose();
            view.Frame = bitmap;
            view.Error = null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Dropped a web frame that would not decode: {ex.Message}");
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- input -------------------------------------------------------------

    /// <summary>CDP modifier bits: Alt 1, Ctrl 2, Meta 4, Shift 8</summary>
    public static int Modifiers(bool alt, bool ctrl, bool meta, bool shift) =>
        (alt ? 1 : 0) | (ctrl ? 2 : 0) | (meta ? 4 : 0) | (shift ? 8 : 0);

    private CdpSession? SessionFor(GtWebElement element) =>
        _views.TryGetValue(element, out var view) ? view.Session : null;

    /// <summary><paramref name="local"/> is in element pixels, which is also the page's own coordinate space since the page is laid out at the element's size</summary>
    public void SendMouse(GtWebElement element, string type, Point local, string button,
                          int clickCount, int modifiers) =>
        _ = SessionFor(element)?.MouseAsync(type, local.X, local.Y, button, clickCount, modifiers);

    public void SendWheel(GtWebElement element, Point local, double deltaX, double deltaY, int modifiers) =>
        _ = SessionFor(element)?.WheelAsync(local.X, local.Y, deltaX, deltaY, modifiers);

    public void SendKey(GtWebElement element, string type, string? text, string key, string code,
                        int windowsVirtualKeyCode, int modifiers) =>
        _ = SessionFor(element)?.KeyAsync(type, text, key, code, windowsVirtualKeyCode, modifiers);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();

        // the editor is going away, so the browsers are killed outright rather than handed to a background task that may not outlive the process
        foreach (var view in _views.Values)
        {
            view.Session?.Kill();
            view.Session = null;
            view.Frame?.Dispose();
            view.Frame = null;
        }
        _views.Clear();
    }
}
