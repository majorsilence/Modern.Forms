using Modern.Forms;
using Modern.Forms.Telerik;

namespace ControlGallery.Panels
{
    // Showcases RadGridView (Modern.Forms.Telerik) — backed by Modern.Forms.DataGridView — using the
    // Telerik-style column types, MasterTemplate configuration, and Rows/Cells access.
    public class TelerikGridViewPanel : BasePanel
    {
        public TelerikGridViewPanel ()
        {
            Controls.Add (new Label {
                Text = "RadGridView with Telerik-style GridView* columns and MasterTemplate configuration.",
                Left = 10, Top = 10, Width = 760
            });

            var grid = new RadGridView {
                Left = 10, Top = 36, Width = 750, Height = 280
            };

            // Telerik configuration façade (no-op boilerplate that migrated designer code emits).
            grid.MasterTemplate.ViewDefinition = new TableViewDefinition ();
            grid.MasterTemplate.AllowAddNewRow = false;
            grid.MasterTemplate.EnableFiltering = true;

            // Telerik-style strongly-typed columns.
            grid.Columns.Add (new GridViewTextBoxColumn ("Name") { HeaderText = "Name", Width = 160 });
            grid.Columns.Add (new GridViewTextBoxColumn ("Age") { HeaderText = "Age", Width = 70 });
            grid.Columns.Add (new GridViewDecimalColumn ("Salary") { HeaderText = "Salary", Width = 120, DecimalPlaces = 2 });
            grid.Columns.Add (new GridViewComboBoxColumn ("Department") { HeaderText = "Department", Width = 160 });
            grid.Columns.Add (new GridViewCheckBoxColumn ("Active") { HeaderText = "Active", Width = 80 });

            grid.Rows.Add ("Alice Johnson", "32", "85000.00", "Engineering", "True");
            grid.Rows.Add ("Bob Smith", "45", "72500.00", "Design", "True");
            grid.Rows.Add ("Carol Williams", "28", "64000.00", "Support", "False");
            grid.Rows.Add ("David Brown", "51", "98000.00", "Engineering", "True");
            grid.Rows.Add ("Eve Davis", "39", "78000.00", "Legal", "True");

            Controls.Add (grid);

            var status = Controls.Add (new Label {
                Text = $"Telerik-style access: grid.Rows.Count = {grid.Rows.Count}; click 'Read row 0' to use grid.Rows(0).Cells(\"Name\").Value",
                Left = 10, Top = 326, Width = 760
            });

            var readButton = new Button {
                Text = "Read row 0",
                Left = 10, Top = 352, Width = 120
            };
            readButton.Click += (o, e) => {
                if (grid.Rows.Count > 0) {
                    var row = grid.Rows[0];
                    status.Text = $"grid.Rows(0): Name=\"{row.Cells["Name"].Value}\", Salary={row.Cells["Salary"].Value}";
                }
            };
            Controls.Add (readButton);
        }
    }
}
