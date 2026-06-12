using System.Drawing;
using Modern.Forms.Renderers;
using SkiaSharp;

namespace Modern.Forms
{
    /// <summary>
    /// Represents a PropertyGrid control for editing object properties at runtime.
    /// This is a stub implementation that displays a placeholder — full property editing
    /// is not yet implemented.
    /// </summary>
    public class PropertyGrid : Control
    {
        private object? _selected_object;

        /// <inheritdoc/>
        protected override Size DefaultSize => new Size (300, 400);

        /// <inheritdoc/>
        public new static readonly ControlStyle DefaultStyle = new ControlStyle (Control.DefaultStyle,
            (style) => {
                style.Border.Width = 1;
                style.BackgroundColor = Theme.ControlLowColor;
            });

        /// <inheritdoc/>
        public override ControlStyle Style { get; } = new ControlStyle (DefaultStyle);

        /// <summary>Gets or sets the object for which the grid displays properties.</summary>
        public object? SelectedObject {
            get => _selected_object;
            set {
                _selected_object = value;
                Invalidate ();
            }
        }

        /// <summary>Gets or sets the objects for which the grid displays properties.</summary>
        public object[]? SelectedObjects {
            get => _selected_object == null ? null : new[] { _selected_object };
            set => SelectedObject = value?.Length > 0 ? value[0] : null;
        }

        /// <summary>Refreshes the displayed properties.</summary>
        public new void Refresh () => Invalidate ();

        /// <inheritdoc/>
        protected override void OnPaint (PaintEventArgs e)
        {
            base.OnPaint (e);

            var g = e.Canvas;
            var r = ClientRectangle;

            g.DrawText ("PropertyGrid", Theme.UIFont, 12, r, Theme.ForegroundColor,
                ContentAlignment.MiddleCenter);

            if (_selected_object != null) {
                var type_name = _selected_object.GetType ().Name;
                var dim = new SKColor (Theme.ForegroundColor.Red, Theme.ForegroundColor.Green,
                    Theme.ForegroundColor.Blue, 128);
                g.DrawText ($"[{type_name}]", Theme.UIFont, 10,
                    new Rectangle (r.X, r.Y + 24, r.Width, 20),
                    dim, ContentAlignment.MiddleCenter);
            }
        }
    }
}
