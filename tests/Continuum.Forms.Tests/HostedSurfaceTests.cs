using System;
using System.Drawing;
using System.Threading.Tasks;
using Continuum.Forms.Backends;
using SkiaSharp;
using Xunit;

namespace Continuum.Forms.Tests;

/// <summary>
/// Verifies that <see cref="HostedSurface"/> — the embeddable root used by the Avalonia and Uno
/// presenters — renders its content and routes input through the standard pipeline when bound to an
/// arbitrary <see cref="IWindowBackend"/> supplied by a host toolkit.
/// </summary>
public class HostedSurfaceTests
{
    // A minimal stand-in for a host toolkit's presenter (Avalonia/Uno). Geometry is fixed; everything
    // else is a no-op, mirroring what an embedding control provides over the IWindowBackend seam.
    // Also records native-overlay calls to verify the NativeControlHost airspace seam.
    private sealed class FakeHostBackend : IWindowBackend, INativeControlHostBackend
    {
        public Size ClientSizeValue = new (200, 100);

        public int Attaches;
        public int Detaches;
        public object? LastAttached;
        public Rectangle LastBounds;
        public Rectangle LastClip;
        public bool LastVisible;

        public void AttachNativeControl (NativeControlHost host, object nativeControl)
        {
            Attaches++;
            LastAttached = nativeControl;
        }

        public void UpdateNativeControl (NativeControlHost host, Rectangle logicalBounds, Rectangle clipBounds, bool visible)
        {
            LastBounds = logicalBounds;
            LastClip = clipBounds;
            LastVisible = visible;
        }

        public void DetachNativeControl (NativeControlHost host) => Detaches++;

        public Point Location { get; set; }
        public Size Size { get; set; } = new (200, 100);
        public Size ClientSize => ClientSizeValue;
        public double Scaling => 1.0;

        public void Show () { }
        public void ShowDialog (IWindowBackend? owner) { }
        public void Hide () { }
        public void Close () { }
        public void Activate () { }

        public string Title { set { } }
        public bool Topmost { get; set; }
        public void SetSystemDecorations (bool useSystemDecorations) { }
        public void SetCursor (CursorType cursor) { }
        public void SetIcon (byte[]? iconPng) { }
        public Size MinimumSize { set { } }
        public Size MaximumSize { set { } }
        public bool CanResize { get; set; }
        public bool ShowInTaskbar { get; set; }
        public double Opacity { get; set; } = 1.0;
        public FormWindowState WindowState { get; set; } = FormWindowState.Normal;
        public bool Enabled { get; set; } = true;

        public Point PointToClient (Point screen) => screen;
        public Point PointToScreen (Point client) => client;
        public void BeginMoveDrag () { }
        public void BeginResizeDrag (WindowEdge edge) { }
        public void Invalidate () { }

        public Task<string[]> ShowOpenFileDialog (OpenFileRequest request) => Task.FromResult (Array.Empty<string> ());
        public Task<string?> ShowSaveFileDialog (SaveFileRequest request) => Task.FromResult<string?> (null);
        public Task<string?> ShowOpenFolderDialog (FolderDialogRequest request) => Task.FromResult<string?> (null);
    }

    private static byte[] RenderToPng (HostedSurface surface, int w, int h)
    {
        using var sk = SKSurface.Create (new SKImageInfo (w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        sk.Canvas.Clear (SKColors.Transparent);
        surface.RenderFrame (sk.Canvas, w, h, 1.0);
        sk.Canvas.Flush ();
        using var img = sk.Snapshot ();
        using var data = img.Encode (SKEncodedImageFormat.Png, 100);
        return data.ToArray ();
    }

    private static int NonTransparentPixelCount (HostedSurface surface, int w, int h)
    {
        using var sk = SKSurface.Create (new SKImageInfo (w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        sk.Canvas.Clear (SKColors.Transparent);
        surface.RenderFrame (sk.Canvas, w, h, 1.0);
        sk.Canvas.Flush ();
        using var img = sk.Snapshot ();
        using var pixmap = img.PeekPixels ();

        var count = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (pixmap.GetPixelColor (x, y).Alpha != 0)
                    count++;
        return count;
    }

    [Fact]
    public void Renders_Content ()
    {
        var surface = new HostedSurface (new FakeHostBackend ());
        var button = new Button { Text = "Embedded" };
        surface.Content = button;

        // A docked-fill button paints its background + text, so the embedded scene is non-empty.
        var painted = NonTransparentPixelCount (surface, 200, 100);
        Assert.True (painted > 0, "Hosted content should paint visible pixels.");
    }

    [Fact]
    public void Empty_Surface_Is_Transparent ()
    {
        // With no content and the default transparent background, nothing should be painted — the host's
        // own background shows through.
        var surface = new HostedSurface (new FakeHostBackend ());

        var painted = NonTransparentPixelCount (surface, 200, 100);
        Assert.Equal (0, painted);
    }

    [Fact]
    public void Routes_Click_To_Content ()
    {
        var surface = new HostedSurface (new FakeHostBackend ());
        var clicked = false;
        var button = new Button { Text = "Click" };
        button.Click += (_, _) => clicked = true;
        surface.Content = button;

        // Lay the scene out (RenderFrame performs layout so the docked button gets its bounds).
        _ = RenderToPng (surface, 200, 100);

        // Drive the neutral input path the presenters use (physical == logical at scale 1).
        surface.HandlePointerPressed (MouseButtons.Left, 100, 50, Keys.None);
        surface.HandlePointerReleased (MouseButtons.Left, 100, 50, Keys.None);

        Assert.True (clicked, "A click on the embedded surface should reach the hosted control.");
    }

    [Fact]
    public void NativeControlHost_Attaches_And_Positions ()
    {
        var backend = new FakeHostBackend ();
        var surface = new HostedSurface (backend);

        var panel = new Panel ();
        var nativeObject = new object ();
        var nativeHost = new NativeControlHost {
            Left = 30, Top = 40, Width = 120, Height = 50,
            NativeControl = nativeObject
        };
        panel.Controls.Add (nativeHost);
        surface.Content = panel;

        // A paint pass triggers the host's lazy attach + position sync through the backend seam.
        _ = RenderToPng (surface, 200, 100);

        Assert.Equal (1, backend.Attaches);
        Assert.Same (nativeObject, backend.LastAttached);
        Assert.True (backend.LastVisible);
        Assert.Equal (new Rectangle (30, 40, 120, 50), backend.LastBounds);
        // Fully visible (fits in the panel viewport) → clip equals bounds.
        Assert.Equal (backend.LastBounds, backend.LastClip);

        // Clearing the native control detaches it from the overlay.
        nativeHost.NativeControl = null;
        Assert.Equal (1, backend.Detaches);
    }

    [Fact]
    public void NativeControlHost_Clips_To_Ancestor_Viewport ()
    {
        var backend = new FakeHostBackend ();
        var surface = new HostedSurface (backend);

        var outer = new Panel ();
        surface.Content = outer;

        // A small inner container whose viewport is smaller than the host placed inside it.
        var inner = new Panel { Left = 10, Top = 10, Width = 100, Height = 60 };
        outer.Controls.Add (inner);

        var nativeHost = new NativeControlHost {
            Left = 0, Top = 0, Width = 200, Height = 200,
            NativeControl = new object ()
        };
        inner.Controls.Add (nativeHost);

        _ = RenderToPng (surface, 300, 200);

        // Host occupies (10,10,200,200) in form coords but is clipped to the inner panel's viewport.
        Assert.Equal (new Rectangle (10, 10, 200, 200), backend.LastBounds);
        Assert.Equal (new Rectangle (10, 10, 100, 60), backend.LastClip);
        Assert.NotEqual (backend.LastBounds, backend.LastClip);
        Assert.True (backend.LastVisible);
    }
}
