using Avalonia;

namespace Modern.Forms
{
    /// <summary>
    /// Represents a popup window used for things like ComboBoxes and context menus.
    /// </summary>
    public class PopupWindow : WindowBase
    {
        private readonly Form parent_form;

        /// <summary>
        /// Initializes a new instance of the PopupWindow class.
        /// </summary>
        public PopupWindow (Form parentForm)
        {
            InitWindow (new ModernFormsPopupWindowHost (this));

            StartPosition = FormStartPosition.Manual;

            parent_form = parentForm;
            parent_form.Deactivated += (o, e) => Hide ();
        }

        /// <inheritdoc/>
        protected override System.Drawing.Size DefaultSize => new System.Drawing.Size (100, 100);

        /// <summary>Gets the default style for all controls of this type.</summary>
        public new static ControlStyle DefaultStyle = new ControlStyle (Control.DefaultStyle,
            (style) => {
                style.BackgroundColor = Theme.ControlMidColor;
            });

        /// <summary>Show the PopupWindow at the specified screen coordinates.</summary>
        public void Show (int x, int y)
        {
            AvWindow.Position = new PixelPoint (x, y);
            AvWindow.Width = Size.Width;
            AvWindow.Height = Size.Height;

            Application.ActivePopupWindow = this;

            Show ();
        }

        /// <summary>Show the PopupWindow at the specified screen coordinates.</summary>
        public void Show (System.Drawing.Point screenLocation) => Show (screenLocation.X, screenLocation.Y);

        /// <summary>Show the PopupWindow at the specified coordinates relative to the provided Control.</summary>
        public void Show (Control control, int x, int y)
        {
            var pos = control.GetPositionInForm ();

            Show (parent_form.PointToScreen (new System.Drawing.Point (pos.X + x, pos.Y + y)));
        }

        /// <summary>Gets or sets the unscaled size of the window.</summary>
        public new System.Drawing.Size Size { get; set; }

        /// <summary>Gets the ControlStyle properties for this instance of the Control.</summary>
        public override ControlStyle Style { get; } = new ControlStyle (DefaultStyle);
    }
}
