using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Continuum.Forms.Backends;
using SkiaSharp;
using SkiaSharp.Views.Windows;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using CFControl = Continuum.Forms.Control;
using WinPoint = Windows.Foundation.Point;

namespace Continuum.Forms.Uno
{
    /// <summary>
    /// An Uno (Skia) control that embeds a Continuum.Forms scene inside an existing Uno application.
    /// Drop it into your Uno visual tree (XAML or code) and assign <see cref="Content"/> a
    /// Continuum.Forms control tree; it renders through an <c>SKXamlCanvas</c> into the host window and
    /// forwards Uno pointer/keyboard input into the Continuum.Forms pipeline. No top-level OS window is
    /// created. Popups (combo dropdowns, menus, tooltips) opened from the embedded content attach their
    /// overlay to this presenter's XamlRoot.
    ///
    /// Targets Uno 6.0+ on the SkiaSharp backend.
    /// </summary>
    public sealed class ContinuumFormsPresenter : Microsoft.UI.Xaml.Controls.Grid, IWindowBackend, IUnoHostSurface, INativeControlHostBackend
    {
        private readonly HostedSurface _host;
        private readonly CursorCanvas _canvas;
        private bool _painting;
        private bool _invalidatePending;
        private readonly System.Collections.Generic.Dictionary<Continuum.Forms.NativeControlHost, UIElement> _overlays = new ();

        /// <summary>
        /// Initializes a new instance of the <see cref="ContinuumFormsPresenter"/> class.
        /// </summary>
        public ContinuumFormsPresenter ()
        {
            // Ensure the Uno backend is the active platform backend, and register this surface so popups
            // opened by the embedded content attach to our XamlRoot rather than spawning OS windows.
            // NOTE: read the backend defensively — the getter auto-resolves to the Avalonia backend by
            // reflection, which throws in an Uno-only app where that assembly isn't referenced.
            UnoPlatformBackend? backend = null;
            try { backend = Platform.Backend as UnoPlatformBackend; } catch { /* no backend configured yet */ }

            if (backend is null) {
                backend = new UnoPlatformBackend ();
                Platform.Backend = backend;
            }
            backend.Initialize ();
            backend.RegisterHostSurface (this);

            _canvas = new CursorCanvas ();
            _canvas.PaintSurface += OnPaintSurface;
            _canvas.Loaded += (_, _) => TryFocus ();
            Children.Add (_canvas);

            WireInput ();

            _host = new HostedSurface (this);
        }

        /// <summary>
        /// Gets or sets the root Continuum.Forms control hosted by this presenter. Setting it docks the
        /// control to fill the presenter.
        /// </summary>
        public CFControl? Content {
            get => _host.Content;
            set => _host.Content = value;
        }

        /// <summary>Gets the underlying hosted surface (advanced scenarios: multiple roots, events).</summary>
        public HostedSurface Surface => _host;

        /// <summary>
        /// Gets or sets whether the embedded scene automatically follows the host Uno application's theme
        /// (light/dark + accent). Defaults to true. Because Continuum's Theme is global, the last presenter
        /// to sync wins when several are present with differing host themes.
        /// </summary>
        public bool SyncTheme { get; set; } = true;

        // ── IUnoHostSurface (popup anchoring) ────────────────────────────────────
        public Microsoft.UI.Xaml.XamlRoot? CanvasXamlRoot => _canvas.XamlRoot;
        public double HostScaling => _canvas.XamlRoot?.RasterizationScale ?? 1.0;

        // For popup offset math the presenter reports the window origin; PointToScreen already bakes in
        // the presenter's own offset within the window, so popups land at (presenterOffset + controlPos).
        Point IUnoHostSurface.Location => Point.Empty;

        // ── Rendering ─────────────────────────────────────────────────────────────

        private void OnPaintSurface (object? sender, SKPaintSurfaceEventArgs e)
        {
            if (SyncTheme)
                ContinuumFormsTheme.FollowHost (this);

            var info = e.Info;
            var scaling = Scaling;

            _painting = true;
            _invalidatePending = false;
            try {
                // Transparent so the host's own background shows through where the embedded scene draws
                // nothing (HostedSurface defaults to a transparent background).
                e.Surface.Canvas.Clear (SKColors.Transparent);
                _host.RenderFrame (e.Surface.Canvas, info.Width, info.Height, scaling);
            } finally {
                _painting = false;
            }

            if (_invalidatePending)
                _canvas.DispatcherQueue?.TryEnqueue (() => _canvas.Invalidate ());
        }

        // ── Input ─────────────────────────────────────────────────────────────────

        private long _lastPointerRightTicks;

        private void WireInput ()
        {
            _canvas.PointerPressed += (_, e) => { TryFocus (); DispatchPointer (e, _host.HandlePointerPressed); };
            _canvas.PointerReleased += (_, e) => {
                if (DispatchPointer (e, _host.HandlePointerReleased) == MouseButtons.Right)
                    _lastPointerRightTicks = Environment.TickCount64;
            };
            _canvas.PointerMoved += (_, e) => DispatchPointer (e, _host.HandlePointerMoved);
            _canvas.PointerExited += (_, e) => DispatchPointer (e, _host.HandlePointerExited);
            _canvas.PointerWheelChanged += (_, e) => DispatchWheel (e);

            _canvas.AddHandler (UIElement.KeyDownEvent,
                new Microsoft.UI.Xaml.Input.KeyEventHandler ((_, e) => { if (!e.Handled && _host.HandleKeyDown (UnoKeyInterop.ToKeys (e.Key))) e.Handled = true; }),
                handledEventsToo: true);
            _canvas.AddHandler (UIElement.KeyUpEvent,
                new Microsoft.UI.Xaml.Input.KeyEventHandler ((_, e) => { if (!e.Handled && _host.HandleKeyUp (UnoKeyInterop.ToKeys (e.Key))) e.Handled = true; }),
                handledEventsToo: true);
            _canvas.CharacterReceived += (_, e) => { if (_host.HandleTextInput (e.Character.ToString ())) e.Handled = true; };

            _canvas.ContextRequested += OnContextRequested;
        }

        private void OnContextRequested (UIElement sender, ContextRequestedEventArgs e)
        {
            if (Environment.TickCount64 - _lastPointerRightTicks < 300) {
                e.Handled = true;
                return;
            }

            if (!e.TryGetPosition (_canvas, out var pos))
                return;

            var scaling = Scaling;
            var x = (int) (pos.X * scaling);
            var y = (int) (pos.Y * scaling);
            _host.HandlePointerPressed (MouseButtons.Right, x, y, Keys.None);
            _host.HandlePointerReleased (MouseButtons.Right, x, y, Keys.None);
            e.Handled = true;
        }

        private delegate void PointerAction (MouseButtons button, int x, int y, Keys keys);

        private MouseButtons DispatchPointer (PointerRoutedEventArgs e, PointerAction action)
        {
            var scaling = Scaling;
            var point = e.GetCurrentPoint (_canvas);
            var x = (int) (point.Position.X * scaling);
            var y = (int) (point.Position.Y * scaling);
            var button = UnoKeyInterop.ToButton (point.Properties);
            action (button, x, y, Keys.None);
            return button;
        }

        private void DispatchWheel (PointerRoutedEventArgs e)
        {
            var scaling = Scaling;
            var point = e.GetCurrentPoint (_canvas);
            var x = (int) (point.Position.X * scaling);
            var y = (int) (point.Position.Y * scaling);
            _host.HandlePointerWheel (MouseButtons.None, x, y, new Point (0, point.Properties.MouseWheelDelta), Keys.None);
        }

        private void TryFocus ()
        {
            try { _canvas.Focus (FocusState.Programmatic); } catch { }
        }

        // ── IWindowBackend ─────────────────────────────────────────────────────────

        // The presenter's offset within the host window, in logical units.
        private WinPoint WindowOffsetLogical ()
        {
            if (_canvas.XamlRoot?.Content is UIElement root) {
                try { return _canvas.TransformToVisual (root).TransformPoint (new WinPoint (0, 0)); }
                catch { }
            }
            return new WinPoint (0, 0);
        }

        public Point Location {
            get {
                var off = WindowOffsetLogical ();
                var s = Scaling;
                return new Point ((int) (off.X * s), (int) (off.Y * s));
            }
            set { /* position is owned by the host layout */ }
        }

        public Size Size {
            get => new Size ((int) ActualWidth, (int) ActualHeight);
            set { /* size is owned by the host layout */ }
        }

        public Size ClientSize => new Size ((int) ActualWidth, (int) ActualHeight);

        public double Scaling => _canvas.XamlRoot?.RasterizationScale ?? 1.0;

        public void Show () { /* shown by the host visual tree */ }
        public void ShowDialog (IWindowBackend? owner) { /* embedded surfaces are not shown modally */ }
        public void Hide () => Visibility = Visibility.Collapsed;
        public void Close () { /* lifetime owned by the host */ }
        public void Activate () => TryFocus ();

        public string Title { set { } }
        public bool Topmost { get; set; }
        public void SetSystemDecorations (bool useSystemDecorations) { }
        public void SetCursor (CursorType cursor) => _canvas.SetCursorShape (UnoKeyInterop.ToCursorShape (cursor));
        public void SetIcon (byte[]? iconPng) { }
        public Size MinimumSize { set { } }
        public Size MaximumSize { set { } }
        public bool CanResize { get; set; }
        public bool ShowInTaskbar { get; set; }
        public double Opacity { get => base.Opacity; set => base.Opacity = value; }
        public FormWindowState WindowState { get; set; } = FormWindowState.Normal;
        public bool Enabled { get; set; } = true;

        public Point PointToClient (Point screen)
        {
            var pos = Location;
            var s = Scaling;
            return new Point ((int) ((screen.X - pos.X) / s), (int) ((screen.Y - pos.Y) / s));
        }

        public Point PointToScreen (Point client)
        {
            var pos = Location;
            var s = Scaling;
            return new Point (pos.X + (int) (client.X * s), pos.Y + (int) (client.Y * s));
        }

        public void BeginMoveDrag () { }
        public void BeginResizeDrag (WindowEdge edge) { }

        // ── INativeControlHostBackend (native Uno UIElements hosted inside the Continuum scene) ────────

        void INativeControlHostBackend.AttachNativeControl (Continuum.Forms.NativeControlHost host, object nativeControl)
        {
            if (nativeControl is not UIElement element)
                return;

            if (_overlays.TryGetValue (host, out var existing) && !ReferenceEquals (existing, element))
                Children.Remove (existing);

            _overlays[host] = element;
            if (!Children.Contains (element))
                Children.Add (element);
        }

        void INativeControlHostBackend.UpdateNativeControl (Continuum.Forms.NativeControlHost host, System.Drawing.Rectangle logicalBounds, System.Drawing.Rectangle clipBounds, bool visible)
        {
            if (!_overlays.TryGetValue (host, out var element) || element is not FrameworkElement fe)
                return;

            fe.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            fe.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
            fe.Margin = new Thickness (logicalBounds.X, logicalBounds.Y, 0, 0);
            fe.Width = logicalBounds.Width;
            fe.Height = logicalBounds.Height;
            fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            // Clip to the visible viewport (local to the element). Null when fully visible.
            fe.Clip = clipBounds == logicalBounds
                ? null
                : new Microsoft.UI.Xaml.Media.RectangleGeometry {
                    Rect = new Windows.Foundation.Rect (
                        clipBounds.X - logicalBounds.X, clipBounds.Y - logicalBounds.Y,
                        clipBounds.Width, clipBounds.Height)
                };
        }

        void INativeControlHostBackend.DetachNativeControl (Continuum.Forms.NativeControlHost host)
        {
            if (_overlays.Remove (host, out var element))
                Children.Remove (element);
        }

        public void Invalidate ()
        {
            if (_painting) {
                _invalidatePending = true;
                return;
            }
            _canvas.Invalidate ();
        }

        // ── File/folder pickers (best-effort; embedded heads may require a window handle we don't own) ──
        public async Task<string[]> ShowOpenFileDialog (OpenFileRequest request)
        {
            try {
                var picker = new Windows.Storage.Pickers.FileOpenPicker ();
                ApplyFilters (picker.FileTypeFilter, request.Filters);

                if (request.AllowMultiple) {
                    var files = await picker.PickMultipleFilesAsync ();
                    var paths = new System.Collections.Generic.List<string> ();
                    foreach (var f in files)
                        if (f is not null) paths.Add (f.Path);
                    return paths.ToArray ();
                }

                var file = await picker.PickSingleFileAsync ();
                return file is null ? Array.Empty<string> () : new[] { file.Path };
            } catch {
                return Array.Empty<string> ();
            }
        }

        public async Task<string?> ShowSaveFileDialog (SaveFileRequest request)
        {
            try {
                var picker = new Windows.Storage.Pickers.FileSavePicker ();
                if (!string.IsNullOrEmpty (request.SuggestedFileName))
                    picker.SuggestedFileName = request.SuggestedFileName;
                var file = await picker.PickSaveFileAsync ();
                return file?.Path;
            } catch {
                return null;
            }
        }

        public async Task<string?> ShowOpenFolderDialog (FolderDialogRequest request)
        {
            try {
                var picker = new Windows.Storage.Pickers.FolderPicker ();
                picker.FileTypeFilter.Add ("*");
                var folder = await picker.PickSingleFolderAsync ();
                return folder?.Path;
            } catch {
                return null;
            }
        }

        private static void ApplyFilters (System.Collections.Generic.IList<string> target, System.Collections.Generic.IReadOnlyList<FileDialogFilter> filters)
        {
            foreach (var filter in filters)
                foreach (var pattern in filter.Patterns) {
                    var ext = pattern.StartsWith ("*.", StringComparison.Ordinal) ? pattern[1..] : pattern;
                    if (!target.Contains (ext))
                        target.Add (ext);
                }

            if (target.Count == 0)
                target.Add ("*");
        }

        // SKXamlCanvas with a settable cursor and keyboard focus.
        private sealed class CursorCanvas : SKXamlCanvas
        {
            public CursorCanvas ()
            {
                IsTabStop = true;
                UseSystemFocusVisuals = false;
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled;
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
            }

            public void SetCursorShape (Microsoft.UI.Input.InputSystemCursorShape shape)
            {
                try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create (shape); } catch { }
            }
        }
    }
}
