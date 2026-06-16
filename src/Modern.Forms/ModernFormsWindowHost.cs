using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvImage = Avalonia.Controls.Image;
using Avalonia.Threading;
using SkiaSharp;
using System.Drawing;

using AvKey = Avalonia.Input.Key;
using AvKeyModifiers = Avalonia.Input.KeyModifiers;
using AvPointerPressedEventArgs = Avalonia.Input.PointerPressedEventArgs;
using AvPointerReleasedEventArgs = Avalonia.Input.PointerReleasedEventArgs;
using AvPointerEventArgs = Avalonia.Input.PointerEventArgs;
using AvPointerWheelChangedEventArgs = Avalonia.Input.PointerWheelEventArgs;
using AvKeyEventArgs = Avalonia.Input.KeyEventArgs;
using AvTextInputEventArgs = Avalonia.Input.TextInputEventArgs;

namespace Modern.Forms
{
    /// <summary>
    /// Internal Avalonia 12 Window that hosts Modern.Forms rendering and forwards
    /// Avalonia input events into the Modern.Forms event pipeline.
    ///
    /// Rendering strategy: a <see cref="WriteableBitmap"/> is locked each frame,
    /// Skia draws the Modern.Forms scene directly into the framebuffer, and an
    /// <see cref="Avalonia.Controls.Image"/> control displays the result. This mirrors the original
    /// native-framebuffer approach and is reliable across all Avalonia 12 platforms.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage ("Design", "CA1001", Justification = "_framebuffer is disposed in OnClosed; Window lifecycle manages the call.")]
    internal class ModernFormsWindowHost : Window, Modern.Forms.Backends.IWindowBackend
    {
        private readonly WindowBase _owner;

        internal AvPointerPressedEventArgs? LastPointerPressed;

        // The bitmap is the "framebuffer". We create it at physical pixel size;
        // Avalonia displays it at logical size via the Image control.
        private WriteableBitmap? _framebuffer;
        private readonly AvImage _surface;
        private DispatcherTimer? _renderTimer;

        // Set by InvalidateVisual() calls so the timer knows to repaint.
        internal bool IsDirty = true;

        internal ModernFormsWindowHost (WindowBase owner)
        {
            _owner = owner;

            // Modern.Forms draws its own decorations on Windows/Linux.
            // macOS uses native chrome (set in Form constructor).
            WindowDecorations = WindowDecorations.None;
            ExtendClientAreaToDecorationsHint = true;

            // Surface image fills the window. Stretch = Fill maps the framebuffer
            // pixels 1:1 with the window client area at logical pixel resolution.
            _surface = new AvImage {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Stretch = Stretch.Fill
            };

            // Grid stretches its children to fill available space.
            var grid = new Grid {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            grid.Children.Add (_surface);
            Content = grid;

            Closing += OnWindowClosing;

            // Resize → recreate framebuffer to match new logical size.
            _surface.SizeChanged += OnSurfaceSizeChanged;

            Opened += (_, _) => {
                EnsureFramebuffer ();
                StartRenderTimer ();
            };
            Closed += (_, _) => StopRenderTimer ();
        }

        private void OnWindowClosing (object? sender, WindowClosingEventArgs e) { }

        private void OnSurfaceSizeChanged (object? sender, SizeChangedEventArgs e) => EnsureFramebuffer ();

        // Creates or resizes the framebuffer so its PHYSICAL pixel size always matches the surface's
        // current logical size × the current render scaling. Called both on layout/size changes and
        // every frame, so it self-corrects when the render scaling changes after the window opens
        // (e.g. a popup shown before its DPI has settled, or a window dragged between displays of
        // different scale). Returns true if a usable framebuffer exists.
        private bool EnsureFramebuffer ()
        {
            var scaling = _owner.Scaling;
            if (scaling <= 0)
                scaling = 1;

            // Prefer the image's laid-out logical size; fall back to the window client size
            // before the first layout pass has run.
            var logicalW = _surface.Bounds.Width  > 0 ? _surface.Bounds.Width  : ClientSize.Width;
            var logicalH = _surface.Bounds.Height > 0 ? _surface.Bounds.Height : ClientSize.Height;

            var physW = Math.Max (1, (int)Math.Round (logicalW * scaling));
            var physH = Math.Max (1, (int)Math.Round (logicalH * scaling));

            if (_framebuffer is null || _framebuffer.PixelSize.Width != physW || _framebuffer.PixelSize.Height != physH) {
                _framebuffer?.Dispose ();
                _framebuffer = new WriteableBitmap (
                    new PixelSize (physW, physH),
                    new Vector (96 * scaling, 96 * scaling),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);
                _surface.Source = _framebuffer;
                IsDirty = true;
            }

            return _framebuffer is not null;
        }

        private void StartRenderTimer ()
        {
            _renderTimer = new DispatcherTimer (DispatcherPriority.Render) {
                Interval = TimeSpan.FromMilliseconds (16) // ~60 FPS
            };
            _renderTimer.Tick += (_, _) => PaintFrame ();
            _renderTimer.Start ();
        }

        private void StopRenderTimer ()
        {
            _renderTimer?.Stop ();
            _renderTimer = null;
        }

        // ── Framebuffer paint ──────────────────────────────────────────────────

        private void PaintFrame ()
        {
            // Self-correcting: recreates the framebuffer if the surface size or render scaling
            // changed since the last frame (fixes blurry/half-resolution popups on HiDPI displays
            // where the scaling settles after the window has already opened).
            if (!EnsureFramebuffer () || _framebuffer is null)
                return;

            // Skip if nothing needs painting.
            var adapter = _owner.adapter;
            bool frameDirty = IsDirty || adapter.NeedsPaint;

            if (!frameDirty)
                return;

            IsDirty = false;

            try {
                using var fb = _framebuffer.Lock ();

                var scaling = _owner.Scaling;

                // fb.Size is in PHYSICAL pixels (the bitmap was created at physical size).
                var physW = fb.Size.Width;
                var physH = fb.Size.Height;

                var skInfo = new SKImageInfo (physW, physH, SKColorType.Bgra8888, SKAlphaType.Premul);

                using var surface = SKSurface.Create (skInfo, fb.Address, fb.RowBytes);

                if (surface is null)
                    return;

                // The Modern.Forms paint pipeline is backend-neutral (SkiaSharp); it lives on WindowBase.
                _owner.RenderFrame (surface.Canvas, physW, physH, scaling);
            } catch (Exception ex) {
                Console.Error.WriteLine ($"[MF] PaintFrame error: {ex.Message}");
            }

            _surface.InvalidateVisual ();
        }

        // ── Input forwarding ──────────────────────────────────────────────────

        // These overrides own the Avalonia → Modern.Forms input translation (positions scaled to
        // physical pixels, buttons/keys mapped), then hand neutral values to the owner. No Avalonia
        // input types cross into WindowBase.

        protected override void OnPointerPressed (AvPointerPressedEventArgs e)
        {
            LastPointerPressed = e;
            var pos = e.GetPosition (this);
            var props = e.GetCurrentPoint (this).Properties;
            _owner.HandlePointerPressed (
                AvaloniaKeyInterop.PressedButton (props.PointerUpdateKind),
                (int)(pos.X * RenderScaling), (int)(pos.Y * RenderScaling),
                AvaloniaKeyInterop.ModifiersOnly (e.KeyModifiers));
            base.OnPointerPressed (e);
        }

        protected override void OnPointerReleased (AvPointerReleasedEventArgs e)
        {
            var pos = e.GetPosition (this);
            var props = e.GetCurrentPoint (this).Properties;
            _owner.HandlePointerReleased (
                AvaloniaKeyInterop.ReleasedButton (props.PointerUpdateKind),
                (int)(pos.X * RenderScaling), (int)(pos.Y * RenderScaling),
                AvaloniaKeyInterop.ModifiersOnly (e.KeyModifiers));
            base.OnPointerReleased (e);
        }

        protected override void OnPointerMoved (AvPointerEventArgs e)
        {
            var pos = e.GetPosition (this);
            var props = e.GetCurrentPoint (this).Properties;
            _owner.HandlePointerMoved (
                AvaloniaKeyInterop.ToMouseButtons (props),
                (int)(pos.X * RenderScaling), (int)(pos.Y * RenderScaling),
                AvaloniaKeyInterop.ModifiersOnly (e.KeyModifiers));
            base.OnPointerMoved (e);
        }

        protected override void OnPointerWheelChanged (AvPointerWheelChangedEventArgs e)
        {
            var pos = e.GetPosition (this);
            var props = e.GetCurrentPoint (this).Properties;
            _owner.HandlePointerWheel (
                AvaloniaKeyInterop.ToMouseButtons (props),
                (int)(pos.X * RenderScaling), (int)(pos.Y * RenderScaling),
                new System.Drawing.Point ((int)e.Delta.X, (int)e.Delta.Y),
                AvaloniaKeyInterop.ModifiersOnly (e.KeyModifiers));
            base.OnPointerWheelChanged (e);
        }

        protected override void OnPointerExited (AvPointerEventArgs e)
        {
            var pos = e.GetPosition (this);
            var props = e.GetCurrentPoint (this).Properties;
            _owner.HandlePointerExited (
                AvaloniaKeyInterop.ToMouseButtons (props),
                (int)(pos.X * RenderScaling), (int)(pos.Y * RenderScaling),
                AvaloniaKeyInterop.ModifiersOnly (e.KeyModifiers));
            base.OnPointerExited (e);
        }

        protected override void OnKeyDown (AvKeyEventArgs e)
        {
            if (_owner.HandleKeyDown (AvaloniaKeyInterop.AddModifiers (AvaloniaKeyInterop.ToFormsKey (e.Key), e.KeyModifiers)))
                e.Handled = true;
            base.OnKeyDown (e);
        }

        protected override void OnKeyUp (AvKeyEventArgs e)
        {
            if (_owner.HandleKeyUp (AvaloniaKeyInterop.AddModifiers (AvaloniaKeyInterop.ToFormsKey (e.Key), e.KeyModifiers)))
                e.Handled = true;
            base.OnKeyUp (e);
        }

        protected override void OnTextInput (AvTextInputEventArgs e)
        {
            if (_owner.HandleTextInput (e.Text ?? string.Empty))
                e.Handled = true;
            base.OnTextInput (e);
        }

        // ── Helpers for Form to trigger OS-level drag ─────────────────────────

        internal void StartMoveDrag ()
        {
            if (LastPointerPressed is not null)
                BeginMoveDrag (LastPointerPressed);
        }

        internal void StartResizeDrag (WindowEdge edge)
        {
            if (LastPointerPressed is not null)
                BeginResizeDrag (edge, LastPointerPressed);
        }

        // ── IWindowBackend (explicit: avoids name collisions with the Avalonia Window base) ──────────

        System.Drawing.Point Backends.IWindowBackend.Location {
            get => new System.Drawing.Point (Position.X, Position.Y);
            set => Position = new PixelPoint (value.X, value.Y);
        }

        System.Drawing.Size Backends.IWindowBackend.Size {
            get => new System.Drawing.Size ((int)Width, (int)Height);
            set { Width = value.Width; Height = value.Height; }
        }

        System.Drawing.Size Backends.IWindowBackend.ClientSize
            => new System.Drawing.Size ((int)ClientSize.Width, (int)ClientSize.Height);

        double Backends.IWindowBackend.Scaling => RenderScaling;

        void Backends.IWindowBackend.Show () => Show ();

        void Backends.IWindowBackend.ShowDialog (Backends.IWindowBackend? owner)
        {
            if (owner is ModernFormsWindowHost ownerHost)
                _ = ShowDialog (ownerHost);
            else
                Show ();
        }

        void Backends.IWindowBackend.Hide () => Hide ();

        void Backends.IWindowBackend.Close () => Close ();

        void Backends.IWindowBackend.Activate () => Activate ();

        string Backends.IWindowBackend.Title { set => Title = value; }

        bool Backends.IWindowBackend.Topmost {
            get => Topmost;
            set => Topmost = value;
        }

        void Backends.IWindowBackend.SetSystemDecorations (bool useSystemDecorations)
        {
            WindowDecorations = useSystemDecorations ? WindowDecorations.Full : WindowDecorations.None;
            ExtendClientAreaToDecorationsHint = !useSystemDecorations;
        }

        void Backends.IWindowBackend.SetCursor (object? cursor) => Cursor = cursor as Avalonia.Input.Cursor;

        System.Drawing.Point Backends.IWindowBackend.PointToClient (System.Drawing.Point screen)
        {
            var p = this.PointToClient (new PixelPoint (screen.X, screen.Y));
            return new System.Drawing.Point ((int)p.X, (int)p.Y);
        }

        System.Drawing.Point Backends.IWindowBackend.PointToScreen (System.Drawing.Point client)
        {
            var p = this.PointToScreen (new Avalonia.Point (client.X, client.Y));
            return new System.Drawing.Point (p.X, p.Y);
        }

        void Backends.IWindowBackend.BeginMoveDrag () => StartMoveDrag ();

        void Backends.IWindowBackend.BeginResizeDrag (Backends.WindowEdge edge) => StartResizeDrag (edge switch {
            Backends.WindowEdge.North => WindowEdge.North,
            Backends.WindowEdge.NorthEast => WindowEdge.NorthEast,
            Backends.WindowEdge.East => WindowEdge.East,
            Backends.WindowEdge.SouthEast => WindowEdge.SouthEast,
            Backends.WindowEdge.South => WindowEdge.South,
            Backends.WindowEdge.SouthWest => WindowEdge.SouthWest,
            Backends.WindowEdge.West => WindowEdge.West,
            _ => WindowEdge.NorthWest
        });

        void Backends.IWindowBackend.Invalidate () => IsDirty = true;
    }

    /// <summary>
    /// Popup-specific host with no owner-window chrome.
    /// </summary>
    internal sealed class ModernFormsPopupWindowHost : ModernFormsWindowHost
    {
        internal ModernFormsPopupWindowHost (WindowBase owner) : base (owner)
        {
            Topmost = true;

            // On macOS, a borderless window with ExtendClientAreaToDecorationsHint = true (inherited
            // from the base host) is rendered with a translucent "vibrancy" backdrop. For a menu or
            // combo-box popup that shows up as a grey, blurry square instead of the menu. The main
            // Form avoids this by switching to native decorations on macOS; a popup has no chrome,
            // so we just make it a plain opaque borderless window.
            if (OperatingSystem.IsMacOS ()) {
                ExtendClientAreaToDecorationsHint = false;
                TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            }
        }
    }
}
