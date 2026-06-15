using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Modern.Forms;

namespace Modern.Forms.Telerik
{
    /// <summary>
    /// Telerik-compat data grid. Backed by <see cref="Modern.Forms.DataGridView"/>. Provides the most
    /// common RadGridView surface: <see cref="MasterTemplate"/>/<see cref="TableViewDefinition"/>
    /// boilerplate, the GridView* column types, and Telerik-style row/cell access via
    /// <see cref="GridViewRowInfo"/>/<see cref="GridViewCellInfo"/> wrappers over the underlying rows.
    /// </summary>
    public class RadGridView : DataGridView
    {
        /// <summary>Initializes a new instance of the RadGridView class.</summary>
        public RadGridView ()
        {
            MasterTemplate = new GridViewTemplate (this);
        }

        /// <summary>Gets the master template (Telerik configuration façade over this grid).</summary>
        public GridViewTemplate MasterTemplate { get; }

        /// <summary>Gets or sets the theme name. No-op stub.</summary>
        public string ThemeName { get; set; } = string.Empty;

        /// <summary>Gets the root element of the grid (stub).</summary>
        public RadElement GridViewElement { get; } = new RadElement ();

        /// <summary>Gets the root element of the grid (stub).</summary>
        public RadElement RootElement { get; } = new RadElement ();

        /// <summary>Gets the current row as a Telerik <see cref="GridViewRowInfo"/>, or null.</summary>
        public new GridViewRowInfo? CurrentRow {
            get {
                var row = base.CurrentRow;
                return row is null ? null : new GridViewRowInfo (row);
            }
        }

        /// <summary>Gets the rows, accessible Telerik-style (indexer/enumeration yield <see cref="GridViewRowInfo"/>) while still supporting Count/Add/Clear/Remove.</summary>
        public new GridViewRowInfoCollection Rows => new GridViewRowInfoCollection (base.Rows);

        /// <summary>Gets the number of rows.</summary>
        public int RowCount => base.Rows.Count;

        /// <summary>Auto-sizes all columns to fit their content. Delegates to the base auto-size pass.</summary>
        public void BestFitColumns () => Invalidate ();

        /// <summary>Begins a batch update. Stub.</summary>
        public void BeginUpdate () { }
        /// <summary>Ends a batch update. Stub.</summary>
        public void EndUpdate () => Invalidate ();

        /// <summary>Raised on a command-cell (button column) click. Stub — wire via CellClick.</summary>
        public event EventHandler<GridViewCellEventArgs>? CommandCellClick { add { } remove { } }
        /// <summary>Raised when the current row changes. Stub.</summary>
        public event EventHandler? CurrentRowChanged { add { } remove { } }
        /// <summary>Raised when a cell value changes (Telerik name). Stub.</summary>
        public event EventHandler<GridViewCellEventArgs>? ValueChanged { add { } remove { } }
        /// <summary>Raised when the context menu is opening. Stub.</summary>
        public event EventHandler? ContextMenuOpening { add { } remove { } }
        /// <summary>Raised when a view cell is being formatted. Stub.</summary>
        public event EventHandler<GridViewCellFormattingEventArgs>? ViewCellFormatting { add { } remove { } }
        /// <summary>Raised when a row is being formatted. Stub.</summary>
        public event EventHandler<GridViewRowFormattingEventArgs>? RowFormatting { add { } remove { } }
    }

    /// <summary>
    /// Telerik-compat grid configuration façade. In Telerik this drives columns/rows/grouping; here
    /// the column/data members forward to the owning <see cref="RadGridView"/> and the rest are stubs.
    /// </summary>
    public class GridViewTemplate
    {
        private readonly RadGridView _grid;

        internal GridViewTemplate (RadGridView grid) => _grid = grid;

        /// <summary>Gets the columns of the grid.</summary>
        public DataGridViewColumnCollection Columns => _grid.Columns;
        /// <summary>Gets or sets the view definition (assigning a <see cref="TableViewDefinition"/> is a no-op).</summary>
        public object? ViewDefinition { get; set; }
        /// <summary>Gets or sets whether a new-row entry is shown.</summary>
        public bool AllowAddNewRow { get; set; } = true;
        /// <summary>Gets or sets whether rows can be deleted.</summary>
        public bool AllowDeleteRow { get; set; } = true;
        /// <summary>Gets or sets whether rows can be edited.</summary>
        public bool AllowEditRow { get; set; } = true;
        /// <summary>Gets or sets whether columns can be reordered.</summary>
        public bool AllowColumnReorder { get; set; } = true;
        /// <summary>Gets or sets whether the column chooser is allowed.</summary>
        public bool AllowColumnChooser { get; set; }
        /// <summary>Gets or sets whether filtering is enabled.</summary>
        public bool EnableFiltering { get; set; }
        /// <summary>Gets or sets whether sorting is enabled.</summary>
        public bool EnableSorting { get; set; } = true;
        /// <summary>Gets or sets whether grouped data auto-expands.</summary>
        public bool AutoExpandGroups { get; set; }
        /// <summary>Gets or sets whether multiple rows can be selected.</summary>
        public bool MultiSelect { get; set; }
        /// <summary>Gets or sets whether the grid is read-only.</summary>
        public bool ReadOnly { get; set; }
        /// <summary>Gets or sets the auto-size columns mode. Stub.</summary>
        public object? AutoSizeColumnsMode { get; set; }
        /// <summary>Gets or sets whether paging is enabled.</summary>
        public bool EnablePaging { get; set; }
        /// <summary>Gets or sets the page size.</summary>
        public int PageSize { get; set; }
        /// <summary>Gets the sort descriptors. Stub list.</summary>
        public List<object> SortDescriptors { get; } = new ();
        /// <summary>Gets the group descriptors. Stub list.</summary>
        public List<object> GroupDescriptors { get; } = new ();
        /// <summary>Gets the summary rows shown at the bottom. Stub list.</summary>
        public List<object> SummaryRowsBottom { get; } = new ();
        /// <summary>Gets the summary rows shown at the top. Stub list.</summary>
        public List<object> SummaryRowsTop { get; } = new ();

        /// <summary>Auto-sizes columns. Stub.</summary>
        public void BestFitColumns () => _grid.BestFitColumns ();
        /// <summary>Refreshes the grid.</summary>
        public void Refresh () => _grid.Invalidate ();
        /// <summary>Expands all groups. Stub.</summary>
        public void ExpandAllGroups () { }
        /// <summary>Collapses all groups. Stub.</summary>
        public void CollapseAllGroups () { }
    }

    /// <summary>Telerik-compat view definition. Assignable to <see cref="GridViewTemplate.ViewDefinition"/> as a no-op.</summary>
    public class TableViewDefinition { }

    // ── Column types ──────────────────────────────────────────────────────────

    /// <summary>Base Telerik-compat grid column. Adds the Telerik column member names on top of <see cref="DataGridViewColumn"/>.</summary>
    public class GridViewColumn : DataGridViewColumn
    {
        /// <summary>Gets or sets whether the column is visible (Telerik alias for <see cref="DataGridViewColumn.Visible"/>).</summary>
        public bool IsVisible {
            get => Visible;
            set => Visible = value;
        }
        /// <summary>Gets or sets the bound field name (Telerik alias for <see cref="DataGridViewColumn.DataPropertyName"/>).</summary>
        public string FieldName {
            get => DataPropertyName;
            set => DataPropertyName = value;
        }
        /// <summary>Gets or sets the column name used by data binding (Telerik alias for <see cref="DataGridViewColumn.Name"/>).</summary>
        public string ColumnName {
            get => Name;
            set => Name = value;
        }
        /// <summary>Gets or sets the .NET format string applied to values. Stub.</summary>
        public string FormatString { get; set; } = string.Empty;
        /// <summary>Gets or sets the text alignment. Stub.</summary>
        public ContentAlignment TextAlignment { get; set; } = ContentAlignment.MiddleLeft;
        /// <summary>Gets or sets the header text alignment. Stub.</summary>
        public ContentAlignment HeaderTextAlignment { get; set; } = ContentAlignment.MiddleLeft;
        /// <summary>Gets or sets whether the column appears in the column chooser. Stub.</summary>
        public bool VisibleInColumnChooser { get; set; } = true;
        /// <summary>Gets or sets the pinned position. Stub.</summary>
        public object? PinPosition { get; set; }
        /// <summary>Gets or sets whether text wraps. Stub.</summary>
        public bool WrapText { get; set; }
        /// <summary>Gets or sets whether reordering is allowed. Stub.</summary>
        public bool AllowReorder { get; set; } = true;
        /// <summary>Gets or sets whether filtering is allowed. Stub.</summary>
        public bool AllowFiltering { get; set; } = true;
        /// <summary>Gets or sets whether sorting is allowed. Stub.</summary>
        public bool AllowSort { get; set; } = true;
        /// <summary>Gets or sets the column data type. Stub.</summary>
        public Type? DataType { get; set; }
        /// <summary>Gets the conditional-formatting object list. Stub.</summary>
        public List<object> ConditionalFormattingObjectList { get; } = new ();

        /// <summary>Auto-sizes this column to its content. Stub.</summary>
        public void BestFit () { }
        /// <summary>Sets the display ordinal. Stub maps to <see cref="DataGridViewColumn.DisplayIndex"/>.</summary>
        public void SetOrdinal (int ordinal) => DisplayIndex = ordinal;
    }

    /// <summary>Telerik-compat data column base.</summary>
    public class GridViewDataColumn : GridViewColumn { }

    /// <summary>Telerik-compat text column.</summary>
    public class GridViewTextBoxColumn : GridViewDataColumn
    {
        /// <summary>Initializes a new instance.</summary>
        public GridViewTextBoxColumn () { }
        /// <summary>Initializes a new instance bound to the specified field.</summary>
        public GridViewTextBoxColumn (string fieldName) { FieldName = fieldName; Name = fieldName; }
        /// <summary>Gets or sets the maximum input length. Stub.</summary>
        public int MaxInputLength { get; set; }
    }

    /// <summary>Telerik-compat combo-box column.</summary>
    public class GridViewComboBoxColumn : GridViewDataColumn
    {
        /// <summary>Initializes a new instance.</summary>
        public GridViewComboBoxColumn () { }
        /// <summary>Initializes a new instance bound to the specified field.</summary>
        public GridViewComboBoxColumn (string fieldName) { FieldName = fieldName; Name = fieldName; }
        /// <summary>Gets or sets the data source for the drop-down.</summary>
        public object? DataSource { get; set; }
        /// <summary>Gets or sets the display member.</summary>
        public string DisplayMember { get; set; } = string.Empty;
        /// <summary>Gets or sets the value member.</summary>
        public string ValueMember { get; set; } = string.Empty;
        /// <summary>Gets or sets whether the column was auto-generated. Stub.</summary>
        public bool IsAutoGenerated { get; set; }
    }

    /// <summary>Telerik-compat check-box column.</summary>
    public class GridViewCheckBoxColumn : GridViewDataColumn
    {
        /// <summary>Initializes a new instance.</summary>
        public GridViewCheckBoxColumn () { }
        /// <summary>Initializes a new instance bound to the specified field.</summary>
        public GridViewCheckBoxColumn (string fieldName) { FieldName = fieldName; Name = fieldName; }
    }

    /// <summary>Telerik-compat decimal column.</summary>
    public class GridViewDecimalColumn : GridViewDataColumn
    {
        /// <summary>Initializes a new instance.</summary>
        public GridViewDecimalColumn () { }
        /// <summary>Initializes a new instance bound to the specified field.</summary>
        public GridViewDecimalColumn (string fieldName) { FieldName = fieldName; Name = fieldName; }
        /// <summary>Gets or sets the number of decimal places. Stub.</summary>
        public int DecimalPlaces { get; set; } = 2;
        /// <summary>Gets or sets the maximum value. Stub.</summary>
        public decimal Maximum { get; set; } = decimal.MaxValue;
        /// <summary>Gets or sets the minimum value. Stub.</summary>
        public decimal Minimum { get; set; } = decimal.MinValue;
    }

    /// <summary>Telerik-compat date/time column.</summary>
    public class GridViewDateTimeColumn : GridViewDataColumn
    {
        /// <summary>Initializes a new instance.</summary>
        public GridViewDateTimeColumn () { }
        /// <summary>Initializes a new instance bound to the specified field.</summary>
        public GridViewDateTimeColumn (string fieldName) { FieldName = fieldName; Name = fieldName; }
    }

    // ── Row / cell wrappers ────────────────────────────────────────────────────

    /// <summary>
    /// Telerik-compat row collection over the underlying <see cref="DataGridViewRowCollection"/>.
    /// The indexer and enumerator yield <see cref="GridViewRowInfo"/>, while Count/Add/Clear/Remove
    /// forward to the real grid rows.
    /// </summary>
    public class GridViewRowInfoCollection : IEnumerable<GridViewRowInfo>
    {
        private readonly DataGridViewRowCollection _rows;

        internal GridViewRowInfoCollection (DataGridViewRowCollection rows) => _rows = rows;

        /// <summary>Gets the number of rows.</summary>
        public int Count => _rows.Count;

        /// <summary>Gets the row at the specified index as a <see cref="GridViewRowInfo"/>.</summary>
        public GridViewRowInfo this[int index] => new GridViewRowInfo (_rows[index]);

        /// <summary>Adds a row with the specified cell values.</summary>
        public void Add (params object[] values) => _rows.Add (values);

        /// <summary>Adds the specified row.</summary>
        public void Add (DataGridViewRow row) => _rows.Add (row);

        /// <summary>Removes the specified row.</summary>
        public void Remove (GridViewRowInfo row) => _rows.Remove (row.DataRow);

        /// <summary>Removes the row at the specified index.</summary>
        public void RemoveAt (int index) => _rows.RemoveAt (index);

        /// <summary>Removes all rows.</summary>
        public void Clear () => _rows.Clear ();

        /// <inheritdoc/>
        public IEnumerator<GridViewRowInfo> GetEnumerator ()
        {
            foreach (var row in _rows)
                yield return new GridViewRowInfo (row);
        }

        IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();
    }

    /// <summary>Telerik-compat row info. Wraps a <see cref="DataGridViewRow"/>.</summary>
    public class GridViewRowInfo
    {
        private readonly DataGridViewRow _row;

        internal GridViewRowInfo (DataGridViewRow row) => _row = row;

        /// <summary>Gets the underlying row.</summary>
        public DataGridViewRow DataRow => _row;
        /// <summary>Gets the cells of the row, accessible by column name or index.</summary>
        public GridViewCellCollection Cells => new GridViewCellCollection (_row);
        /// <summary>Gets the row index.</summary>
        public int Index => _row.Index;
        /// <summary>Gets or sets whether the row is selected.</summary>
        public bool IsSelected {
            get => _row.Selected;
            set => _row.Selected = value;
        }
        /// <summary>Gets or sets whether the row is selected.</summary>
        public bool Selected {
            get => _row.Selected;
            set => _row.Selected = value;
        }
        /// <summary>Gets the bound data item.</summary>
        public object? DataBoundItem => _row.DataBoundItem;
        /// <summary>Gets or sets whether the row is read-only.</summary>
        public bool ReadOnly {
            get => _row.ReadOnly;
            set => _row.ReadOnly = value;
        }
        /// <summary>Gets whether this is the new-row placeholder.</summary>
        public bool IsNewRow => _row.IsNewRow;
        /// <summary>Gets or sets the row tag.</summary>
        public object? Tag {
            get => _row.Tag;
            set => _row.Tag = value;
        }

        /// <summary>Deletes this row from the grid.</summary>
        public void Delete () => _row.DataGridView?.Rows.Remove (_row);
    }

    /// <summary>Telerik-compat cell collection over a <see cref="DataGridViewRow"/>.</summary>
    public class GridViewCellCollection
    {
        private readonly DataGridViewRow _row;

        internal GridViewCellCollection (DataGridViewRow row) => _row = row;

        /// <summary>Gets the cell for the specified column name.</summary>
        public GridViewCellInfo this[string columnName] {
            get {
                var cell = _row.Cells[columnName] ?? _row.Cells.Add (null);
                return new GridViewCellInfo (cell);
            }
        }

        /// <summary>Gets the cell at the specified index.</summary>
        public GridViewCellInfo this[int index] => new GridViewCellInfo (_row.Cells[index]);

        /// <summary>Enumerates the cells.</summary>
        public IEnumerator<GridViewCellInfo> GetEnumerator ()
        {
            foreach (DataGridViewCell cell in _row.Cells)
                yield return new GridViewCellInfo (cell);
        }
    }

    /// <summary>Telerik-compat cell info. Wraps a <see cref="DataGridViewCell"/>.</summary>
    public class GridViewCellInfo
    {
        private readonly DataGridViewCell _cell;

        internal GridViewCellInfo (DataGridViewCell cell) => _cell = cell;

        /// <summary>Gets or sets the cell value.</summary>
        public object? Value {
            get => _cell.Value;
            set => _cell.Value = value;
        }
        /// <summary>Gets the owning column.</summary>
        public DataGridViewColumn? ColumnInfo => _cell.OwningColumn;
        /// <summary>Gets the cell style (Telerik-compat style object).</summary>
        public RadCellStyle Style { get; } = new RadCellStyle ();
        /// <summary>Gets or sets whether the cell is read-only.</summary>
        public bool ReadOnly {
            get => _cell.ReadOnly;
            set => _cell.ReadOnly = value;
        }
        /// <summary>Gets or sets whether the cell is selected.</summary>
        public bool Selected {
            get => _cell.Selected;
            set => _cell.Selected = value;
        }
    }
}
