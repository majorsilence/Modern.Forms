using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Continuum.Forms;
using Continuum.Forms.Renderers;
using SkiaSharp;

namespace Continuum.Forms.Telerik
{
    /// <summary>
    /// Telerik-compat data grid. Backed by <see cref="Continuum.Forms.DataGridView"/>. Provides the most
    /// common RadGridView surface: <see cref="MasterTemplate"/>/<see cref="TableViewDefinition"/>
    /// boilerplate, the GridView* column types, and Telerik-style row/cell access via
    /// <see cref="GridViewRowInfo"/>/<see cref="GridViewCellInfo"/> wrappers over the underlying rows.
    ///
    /// In addition to the data surface, the control implements the interactive end-user features of
    /// RadGridView: in-grid column <b>filtering</b> (funnel popup with a distinct-value checklist and a
    /// condition), header-click <b>sorting</b>, <b>grouping</b> (right-click "Group by", drag a header
    /// into the group panel, expand/collapse groups), <b>drag-to-reorder</b> columns, and
    /// <see cref="SaveLayout(string)"/>/<see cref="LoadLayout(string)"/> persistence to Telerik-shaped XML.
    /// </summary>
    public class RadGridView : DataGridView
    {
        // The full, untransformed set of data rows. base.Rows holds the *display* projection
        // (filtered + sorted + grouped, with injected group-header rows); _master is the source of truth.
        private readonly List<DataGridViewRow> _master = new ();
        // Guards re-entrancy: true while we (not the user) are rewriting base.Rows.
        private bool _applyingView;
        private bool _suspendRebuild;
        // Group path keys (see BuildGroupKey) that the user has collapsed.
        private readonly HashSet<string> _collapsed = new (StringComparer.Ordinal);

        // ── Interaction state (read by RadGridViewRenderer) ──
        private int _headerDragColumn = -1;
        private Point _dragStart;
        internal bool DragActive { get; private set; }
        internal int DragColumn => _headerDragColumn;
        internal Point DragLocation { get; private set; }
        internal int DragTargetColumn { get; private set; } = -1;
        internal bool DragOverGroupPanel { get; private set; }

        // Layout rectangles published by the renderer for hit-testing.
        internal readonly List<GroupPillLayout> GroupPillLayouts = new ();
        internal readonly Dictionary<int, Rectangle> FilterGlyphRects = new ();

        /// <summary>Logical height of the group panel band shown above the column headers.</summary>
        internal const int GroupPanelLogicalHeight = 34;

        static RadGridView ()
        {
            // RadGridView gets its own renderer (group panel, filter glyphs, group-header rows). The
            // renderer derives from DataGridViewRenderer but re-declares its Type as RadGridView.
            RenderManager.SetRenderer<RadGridView> (new RadGridViewRenderer ());
        }

        /// <summary>Initializes a new instance of the RadGridView class.</summary>
        public RadGridView ()
        {
            MasterTemplate = new GridViewTemplate (this);

            // Descriptor changes (add/remove/clear) rebuild the displayed view.
            SortDescriptors.Changed = () => { SyncSortGlyphs (); RebuildView (); };
            GroupDescriptors.Changed = () => { RefreshLayout (); RebuildView (); };
            FilterDescriptors.Changed = RebuildView;

            // Forward the real (raised) base events to their Telerik-shaped equivalents so migrated
            // grid handlers actually run. CellClick also drives CommandCellClick (Telerik's button-
            // column command pattern dispatches by ColumnInfo.Name), and selection changes drive both
            // SelectionChanged and CurrentRowChanged.
            base.CellClick += (_, e) => {
                var args = BuildCellArgs (e.ColumnIndex, e.RowIndex);
                _cellClick?.Invoke (this, args);
                _commandCellClick?.Invoke (this, args);
            };
            base.CellValueChanged += (_, e) => {
                var args = BuildCellArgs (e.ColumnIndex, e.RowIndex);
                _cellValueChanged?.Invoke (this, args);
                _valueChanged?.Invoke (this, args);

                // A value change in a grouped/filtered/sorted column must re-flow the view.
                if (HasViewTransform)
                    RebuildView ();
            };
            base.CellEndEdit += (_, e) => _cellEndEdit?.Invoke (this, BuildCellArgs (e.ColumnIndex, e.RowIndex));
            base.CellBeginEdit += (_, e) => _cellBeginEdit?.Invoke (this, new GridViewCellCancelEventArgs { RowIndex = e.RowIndex, ColumnIndex = e.ColumnIndex, Row = RowAt (e.RowIndex) });
            base.SelectionChanged += (_, e) => {
                _selectionChanged?.Invoke (this, e);
                _currentRowChanged?.Invoke (this, e);
            };
            // Base CellDoubleClick is not raised; derive it from the control's double-click + current cell.
            DoubleClick += (_, _) => {
                var cell = base.CurrentCell;
                if (cell is not null)
                    _cellDoubleClick?.Invoke (this, BuildCellArgs (cell.ColumnIndex, cell.RowIndex));
            };
        }

        /// <summary>Gets the master template (Telerik configuration façade over this grid).</summary>
        public GridViewTemplate MasterTemplate { get; }

        /// <summary>Gets or sets the theme name. No-op stub.</summary>
        public string ThemeName { get; set; } = string.Empty;

        /// <summary>Gets the root element of the grid (stub).</summary>
        public RadElement GridViewElement { get; } = new RadElement ();

        /// <summary>Gets the root element of the grid (stub).</summary>
        public RadElement RootElement { get; } = new RadElement ();

        /// <summary>Gets the current row as a Telerik <see cref="GridViewRowInfo"/>, or null (group-header rows return null).</summary>
        public new GridViewRowInfo? CurrentRow {
            get {
                var row = base.CurrentRow;
                return row is null || IsGroupRow (row) ? null : new GridViewRowInfo (row);
            }
        }

        /// <summary>Gets the data rows, accessible Telerik-style (indexer/enumeration yield <see cref="GridViewRowInfo"/>). Injected group-header rows are not included.</summary>
        public new GridViewRowInfoCollection Rows => new GridViewRowInfoCollection (base.Rows);

        /// <summary>Gets the number of data rows (excludes injected group-header rows).</summary>
        public int RowCount {
            get {
                var count = 0;
                foreach (var row in base.Rows)
                    if (!IsGroupRow (row))
                        count++;
                return count;
            }
        }

        // ── Telerik config surface (forwarded to MasterTemplate so grid.X and grid.MasterTemplate.X agree) ──

        /// <summary>Gets or sets whether a new-row entry is shown.</summary>
        public bool AllowAddNewRow { get => MasterTemplate.AllowAddNewRow; set => MasterTemplate.AllowAddNewRow = value; }
        /// <summary>Gets or sets whether rows can be deleted.</summary>
        public bool AllowDeleteRow { get => MasterTemplate.AllowDeleteRow; set => MasterTemplate.AllowDeleteRow = value; }
        /// <summary>Gets or sets whether rows can be edited.</summary>
        public bool AllowEditRow { get => MasterTemplate.AllowEditRow; set => MasterTemplate.AllowEditRow = value; }
        /// <summary>Gets or sets whether columns can be reordered by dragging their headers.</summary>
        public bool AllowColumnReorder { get => MasterTemplate.AllowColumnReorder; set => MasterTemplate.AllowColumnReorder = value; }
        /// <summary>Gets or sets whether the column chooser is allowed.</summary>
        public bool AllowColumnChooser { get => MasterTemplate.AllowColumnChooser; set => MasterTemplate.AllowColumnChooser = value; }
        /// <summary>Gets or sets whether multiple rows can be selected.</summary>
        public new bool MultiSelect { get => MasterTemplate.MultiSelect; set { MasterTemplate.MultiSelect = value; base.MultiSelect = value; } }

        /// <summary>Gets or sets whether the in-grid column filtering UI (funnel popups) is enabled.</summary>
        public bool EnableFiltering {
            get => MasterTemplate.EnableFiltering;
            set { MasterTemplate.EnableFiltering = value; Invalidate (); }
        }

        /// <summary>Gets or sets whether sorting is enabled. When false, header-click sorting is disabled for every column.</summary>
        public bool EnableSorting {
            get => MasterTemplate.EnableSorting;
            set { MasterTemplate.EnableSorting = value; ApplyEnableSorting (); }
        }

        // When sorting is disabled grid-wide, force every column non-sortable (the base only sorts
        // columns whose Sortable flag is true). Applied on set and whenever the column set changes.
        private void ApplyEnableSorting ()
        {
            // MasterTemplate may be null if a base-ctor column change fires before our ctor body runs.
            if (MasterTemplate is null || MasterTemplate.EnableSorting)
                return;

            foreach (DataGridViewColumn column in base.Columns)
                column.Sortable = false;
        }

        /// <inheritdoc/>
        internal override void OnColumnsChanged ()
        {
            base.OnColumnsChanged ();
            ApplyEnableSorting ();
        }

        /// <summary>Gets or sets whether grouped data auto-expands. Stub retained for compatibility.</summary>
        public bool AutoExpandGroups { get => MasterTemplate.AutoExpandGroups; set => MasterTemplate.AutoExpandGroups = value; }

        private bool _showGroupPanel;
        /// <summary>Gets or sets whether the group panel (drag a column header here to group) is shown.</summary>
        public bool ShowGroupPanel {
            get => _showGroupPanel;
            set {
                if (_showGroupPanel == value)
                    return;
                _showGroupPanel = value;
                RefreshLayout ();
            }
        }

        private bool _enableGrouping;
        /// <summary>Gets or sets whether grouping (group panel, right-click "group by", drag-to-group) is enabled.</summary>
        public bool EnableGrouping {
            get => _enableGrouping;
            set {
                _enableGrouping = value;
                if (!value)
                    ClearGrouping ();
            }
        }

        /// <summary>Gets or sets whether alternating rows are colored. Stub.</summary>
        public bool EnableAlternatingRowColor { get; set; }

        // ── Descriptors (also surfaced on MasterTemplate) ──

        /// <summary>Gets the sort descriptors. Changing the collection re-sorts the view.</summary>
        public GridDescriptorCollection<SortDescriptor> SortDescriptors { get; } = new ();
        /// <summary>Gets the group descriptors. Changing the collection re-groups the view.</summary>
        public GridDescriptorCollection<GroupDescriptor> GroupDescriptors { get; } = new ();
        /// <summary>Gets the filter descriptors. Changing the collection re-filters the view.</summary>
        public GridDescriptorCollection<FilterDescriptor> FilterDescriptors { get; } = new ();

        /// <inheritdoc/>
        protected override int ContentTopOffset => ShowGroupPanel ? LogicalToDeviceUnits (GroupPanelLogicalHeight) : 0;

        /// <summary>True when the displayed rows differ from the raw master (any filter, sort, or group applied).</summary>
        internal bool HasViewTransform =>
            GroupDescriptors.Count > 0 || SortDescriptors.Count > 0 || FilterDescriptors.Any (f => f.IsActive);

        /// <summary>Returns whether the row is an injected group-header row.</summary>
        internal static bool IsGroupRow (DataGridViewRow row) => row.Tag is GridGroupRow;

        /// <summary>Returns whether the column currently has an active filter (used by the renderer to highlight the funnel).</summary>
        internal bool ColumnHasActiveFilter (DataGridViewColumn column)
            => FilterDescriptors.Any (f => f.IsActive && NameMatches (f.PropertyName, column));

        /// <summary>Device-pixel height of the group panel band (0 when hidden). Used by the renderer.</summary>
        internal int GroupPanelBandHeight => ShowGroupPanel ? LogicalToDeviceUnits (GroupPanelLogicalHeight) : 0;

        // ── Public grouping / filtering API ──

        /// <summary>Groups the grid by the named column (appended as the innermost group level).</summary>
        public void GroupByColumn (string columnName, ListSortDirection direction = ListSortDirection.Ascending)
        {
            if (string.IsNullOrEmpty (columnName) || GroupDescriptors.Any (g => NameMatches (g.PropertyName, columnName)))
                return;

            GroupDescriptors.Add (new GroupDescriptor (columnName, direction));
        }

        /// <summary>Removes the grouping for the named column.</summary>
        public void UngroupColumn (string columnName)
        {
            var existing = GroupDescriptors.Find (g => NameMatches (g.PropertyName, columnName));
            if (existing is not null)
                GroupDescriptors.Remove (existing);
        }

        /// <summary>Removes all grouping.</summary>
        public void ClearGrouping ()
        {
            if (GroupDescriptors.Count == 0)
                return;
            _collapsed.Clear ();
            GroupDescriptors.Clear ();
        }

        /// <summary>Clears the filter for the named column.</summary>
        public void ClearColumnFilter (string columnName)
        {
            var existing = FilterDescriptors.Find (f => NameMatches (f.PropertyName, columnName));
            if (existing is not null)
                FilterDescriptors.Remove (existing);
        }

        /// <summary>Expands every group.</summary>
        public void ExpandAllGroups ()
        {
            if (_collapsed.Count == 0)
                return;
            _collapsed.Clear ();
            RebuildView ();
        }

        /// <summary>Collapses every group.</summary>
        public void CollapseAllGroups ()
        {
            if (GroupDescriptors.Count == 0)
                return;

            // Build the full (all-expanded) projection to enumerate every group key, then collapse them.
            var all = BuildDisplayRows (respectCollapse: false);
            foreach (var row in all)
                if (row.Tag is GridGroupRow g)
                    _collapsed.Add (g.Key);

            RebuildView ();
        }

        /// <summary>Auto-sizes every visible column to fit its header and (formatted) cell content.</summary>
        public void BestFitColumns ()
        {
            const int padding = 18;
            var fontSize = Theme.ItemFontSize;

            foreach (DataGridViewColumn column in base.Columns) {
                if (!column.Visible)
                    continue;

                var width = (int)TextMeasurer.MeasureText (column.HeaderText ?? string.Empty, Theme.UIFontBold, fontSize).Width;
                var columnIndex = column.Index;

                // Check-box columns render a fixed-size glyph, so size to the header only.
                if (!column.DisplaysAsCheckBox && columnIndex >= 0) {
                    foreach (var row in base.Rows) {
                        if (IsGroupRow (row) || columnIndex >= row.Cells.Count)
                            continue;

                        var text = ComputeFormattedText (row.Cells[columnIndex], column);
                        var w = (int)TextMeasurer.MeasureText (text, Theme.UIFont, fontSize).Width;

                        if (w > width)
                            width = w;
                    }
                }

                column.Width = Math.Max (column.MinimumWidth, width + padding);
            }

            Invalidate ();
        }

        /// <summary>Begins a batch update. Suspends view rebuilds until <see cref="EndUpdate"/>.</summary>
        public void BeginUpdate () => _suspendRebuild = true;
        /// <summary>Ends a batch update and rebuilds the view.</summary>
        public void EndUpdate () { _suspendRebuild = false; RebuildView (); Invalidate (); }

        // ── View pipeline ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        internal override void OnRowsChanged ()
        {
            base.OnRowsChanged ();

            // _master is null only if a base-ctor row change races our field initializers.
            if (_applyingView || _master is null)
                return;

            // The user (or data binding) mutated base.Rows. Recapture the master data rows and re-flow.
            SyncMasterFromDisplay ();

            if (HasViewTransform)
                RebuildView ();
        }

        // Captures the current non-group display rows as the new master set.
        private void SyncMasterFromDisplay ()
        {
            _master.Clear ();
            foreach (var row in base.Rows)
                if (!IsGroupRow (row))
                    _master.Add (row);
        }

        // Rebuilds base.Rows from the master set by applying filters, sorting and grouping.
        internal void RebuildView ()
        {
            if (_suspendRebuild || _master is null)
                return;

            var display = HasViewTransform ? BuildDisplayRows (respectCollapse: true) : new List<DataGridViewRow> (_master);

            _applyingView = true;
            try {
                base.Rows.ReplaceAll (display);
            } finally {
                _applyingView = false;
            }
        }

        // Produces the displayed row list: filtered, sorted, with group-header rows interleaved.
        private List<DataGridViewRow> BuildDisplayRows (bool respectCollapse)
        {
            IEnumerable<DataGridViewRow> rows = _master;

            // Filter
            var activeFilters = FilterDescriptors.Where (f => f.IsActive).ToList ();
            if (activeFilters.Count > 0)
                rows = rows.Where (r => PassesFilters (r, activeFilters));

            var list = rows.ToList ();

            // Sort (group keys first so groups are contiguous, then explicit sort descriptors). Stable.
            if (GroupDescriptors.Count > 0 || SortDescriptors.Count > 0)
                list = StableSort (list);

            if (GroupDescriptors.Count == 0)
                return list;

            var result = new List<DataGridViewRow> ();
            BuildGroupLevel (list, 0, string.Empty, respectCollapse, result);
            return result;
        }

        private bool PassesFilters (DataGridViewRow row, List<FilterDescriptor> filters)
        {
            foreach (var filter in filters) {
                var idx = ColumnIndexByName (filter.PropertyName);
                if (idx < 0)
                    continue;
                if (!filter.Matches (GetCellDisplay (row, idx)))
                    return false;
            }
            return true;
        }

        private List<DataGridViewRow> StableSort (List<DataGridViewRow> rows)
        {
            var indexed = rows.Select ((r, i) => (r, i)).ToList ();
            indexed.Sort ((a, b) => {
                var cmp = CompareRows (a.r, b.r);
                return cmp != 0 ? cmp : a.i.CompareTo (b.i);
            });
            return indexed.Select (t => t.r).ToList ();
        }

        private int CompareRows (DataGridViewRow a, DataGridViewRow b)
        {
            foreach (var g in GroupDescriptors) {
                var idx = ColumnIndexByName (g.PropertyName);
                if (idx < 0)
                    continue;
                var cmp = FilterDescriptor.Compare (GetCellDisplay (a, idx), GetCellDisplay (b, idx));
                if (g.Direction == ListSortDirection.Descending)
                    cmp = -cmp;
                if (cmp != 0)
                    return cmp;
            }

            foreach (var s in SortDescriptors) {
                var idx = ColumnIndexByName (s.PropertyName);
                if (idx < 0)
                    continue;
                var cmp = FilterDescriptor.Compare (GetCellDisplay (a, idx), GetCellDisplay (b, idx));
                if (s.Direction == ListSortDirection.Descending)
                    cmp = -cmp;
                if (cmp != 0)
                    return cmp;
            }

            return 0;
        }

        // Recursively emits group-header rows and (when expanded) their children.
        private void BuildGroupLevel (List<DataGridViewRow> rows, int level, string parentKey, bool respectCollapse, List<DataGridViewRow> result)
        {
            if (level >= GroupDescriptors.Count) {
                result.AddRange (rows);
                return;
            }

            var descriptor = GroupDescriptors[level];
            var colIndex = ColumnIndexByName (descriptor.PropertyName);
            var header = ColumnHeaderByName (descriptor.PropertyName);

            // rows are pre-sorted by the group keys, so equal values are contiguous.
            var i = 0;
            while (i < rows.Count) {
                var value = GetCellDisplay (rows[i], colIndex);
                var members = new List<DataGridViewRow> ();

                while (i < rows.Count && string.Equals (GetCellDisplay (rows[i], colIndex), value, StringComparison.CurrentCultureIgnoreCase)) {
                    members.Add (rows[i]);
                    i++;
                }

                var key = BuildGroupKey (parentKey, level, value);
                var collapsed = respectCollapse && _collapsed.Contains (key);

                var groupRow = new DataGridViewRow {
                    Tag = new GridGroupRow {
                        Field = descriptor.PropertyName,
                        HeaderText = header,
                        Value = value,
                        Count = members.Count,
                        Level = level,
                        Key = key,
                        Collapsed = collapsed
                    }
                };
                result.Add (groupRow);

                if (!collapsed)
                    BuildGroupLevel (members, level + 1, key, respectCollapse, result);
            }
        }

        private static string BuildGroupKey (string parentKey, int level, string value)
            => $"{parentKey}/{level}:{value}";

        // ── Name / value helpers ──

        internal int ColumnIndexByName (string name)
        {
            for (var i = 0; i < base.Columns.Count; i++)
                if (NameMatches (name, base.Columns[i]))
                    return i;
            return -1;
        }

        private DataGridViewColumn? ColumnByName (string name)
        {
            var idx = ColumnIndexByName (name);
            return idx >= 0 ? base.Columns[idx] : null;
        }

        private string ColumnHeaderByName (string name)
        {
            var col = ColumnByName (name);
            return col is null ? name : (string.IsNullOrEmpty (col.HeaderText) ? col.Name : col.HeaderText);
        }

        private static bool NameMatches (string name, DataGridViewColumn column)
            => string.Equals (column.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals (column.DataPropertyName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals (column.HeaderText, name, StringComparison.OrdinalIgnoreCase);

        private static bool NameMatches (string a, string b) => string.Equals (a, b, StringComparison.OrdinalIgnoreCase);

        // The display text used for filtering/sorting/grouping (combo FK→name, format strings applied).
        internal string GetCellDisplay (DataGridViewRow row, int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= row.Cells.Count)
                return string.Empty;
            var column = columnIndex < base.Columns.Count ? base.Columns[columnIndex] : null;
            return ComputeFormattedText (row.Cells[columnIndex], column);
        }

        // Distinct display values for a column (used to populate the filter popup checklist).
        internal List<string> DistinctValues (int columnIndex)
            => _master.Select (r => GetCellDisplay (r, columnIndex))
                      .Distinct (StringComparer.CurrentCultureIgnoreCase)
                      .OrderBy (v => v, StringComparer.CurrentCultureIgnoreCase)
                      .ToList ();

        private void RefreshLayout ()
        {
            // ContentTopOffset / column set changed: recompute scroll metrics and repaint.
            OnColumnsChanged ();
            RebuildView ();
            Invalidate ();
        }

        // ── Sorting ──

        private void ToggleSort (int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= base.Columns.Count)
                return;

            var column = base.Columns[columnIndex];
            if (!EnableSorting || !column.Sortable)
                return;

            var name = !string.IsNullOrEmpty (column.Name) ? column.Name : column.HeaderText;
            var existing = SortDescriptors.Find (s => NameMatches (s.PropertyName, column));

            _suspendRebuild = true;
            SortDescriptors.Clear ();   // single-column sort

            if (existing is null)
                SortDescriptors.Add (new SortDescriptor (name, ListSortDirection.Ascending));
            else if (existing.Direction == ListSortDirection.Ascending)
                SortDescriptors.Add (new SortDescriptor (name, ListSortDirection.Descending));
            // else: was descending → leave cleared (unsorted)

            _suspendRebuild = false;
            SyncSortGlyphs ();
            RebuildView ();
        }

        // Keeps each column's SortOrder (drawn as the header glyph) in step with the sort descriptors.
        private void SyncSortGlyphs ()
        {
            foreach (DataGridViewColumn column in base.Columns)
                column.SortOrder = SortOrder.None;

            foreach (var s in SortDescriptors) {
                var col = ColumnByName (s.PropertyName);
                if (col is not null)
                    col.SortOrder = s.Direction == ListSortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending;
            }
        }

        // ── Column reorder ──

        private void MoveColumn (int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= base.Columns.Count || to >= base.Columns.Count)
                return;

            var column = base.Columns[from];

            _suspendRebuild = true;
            _applyingView = true;   // suppress master resync while we shuffle cells
            try {
                base.Columns.RemoveAt (from);
                var target = to > from ? to - 1 : to;
                base.Columns.Insert (target, column);

                foreach (var row in _master) {
                    if (from >= row.Cells.Count)
                        continue;
                    var cell = row.Cells[from];
                    row.Cells.RemoveAt (from);
                    row.Cells.Insert (Math.Min (target, row.Cells.Count), cell);
                }
            } finally {
                _applyingView = false;
                _suspendRebuild = false;
            }

            RebuildView ();
            Invalidate ();
        }

        // ── Mouse interaction ──

        private const int DragThreshold = 4;

        /// <inheritdoc/>
        protected override void OnMouseDown (MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left) {
                var content = GetContentArea ();

                // 1) Group panel band (above the header).
                if (ShowGroupPanel && e.Location.Y < content.Top) {
                    HandleGroupPanelMouseDown (e.Location);
                    return;
                }

                // 2) Column header row.
                if (ColumnHeadersVisible) {
                    var headerRect = new Rectangle (content.Left, content.Top, content.Width, ScaledHeaderHeight);

                    if (headerRect.Contains (e.Location)) {
                        // Resize zones still belong to the base grid.
                        if (AllowUserToResizeColumns && NearColumnEdge (e.Location)) {
                            base.OnMouseDown (e);
                            return;
                        }

                        var col = GetColumnAtLocation (e.Location);
                        if (col >= 0) {
                            if (EnableFiltering && FilterGlyphRects.TryGetValue (col, out var glyph) && glyph.Contains (e.Location)) {
                                ShowFilterPopup (col);
                                return;
                            }

                            // Defer: a plain click sorts on mouse-up; a drag groups/reorders.
                            _headerDragColumn = col;
                            _dragStart = e.Location;
                            DragActive = false;
                        }
                        return;
                    }
                }

                // 3) Group-header row → toggle expand/collapse.
                var rowIndex = GetRowAtLocation (e.Location);
                if (rowIndex >= 0 && rowIndex < base.Rows.Count && IsGroupRow (base.Rows[rowIndex])) {
                    ToggleGroupRow (base.Rows[rowIndex]);
                    return;
                }
            }

            base.OnMouseDown (e);
        }

        /// <inheritdoc/>
        protected override void OnMouseMove (MouseEventArgs e)
        {
            if (_headerDragColumn >= 0 && e.Button == MouseButtons.Left) {
                if (!DragActive && (Math.Abs (e.Location.X - _dragStart.X) > LogicalToDeviceUnits (DragThreshold)
                                 || Math.Abs (e.Location.Y - _dragStart.Y) > LogicalToDeviceUnits (DragThreshold)))
                    DragActive = true;

                if (DragActive) {
                    DragLocation = e.Location;
                    var content = GetContentArea ();
                    DragOverGroupPanel = ShowGroupPanel && EnableGrouping && e.Location.Y < content.Top;
                    DragTargetColumn = DragOverGroupPanel ? -1 : GetColumnAtLocation (e.Location);
                    Invalidate ();
                    return;
                }
            }

            base.OnMouseMove (e);
        }

        /// <inheritdoc/>
        protected override void OnMouseUp (MouseEventArgs e)
        {
            if (_headerDragColumn >= 0) {
                var col = _headerDragColumn;
                _headerDragColumn = -1;

                if (DragActive) {
                    DragActive = false;

                    if (DragOverGroupPanel) {
                        var column = col < base.Columns.Count ? base.Columns[col] : null;
                        if (column is not null)
                            GroupByColumn (!string.IsNullOrEmpty (column.Name) ? column.Name : column.HeaderText);
                    } else if (AllowColumnReorder && DragTargetColumn >= 0 && DragTargetColumn != col) {
                        MoveColumn (col, DragTargetColumn);
                    }

                    DragTargetColumn = -1;
                    DragOverGroupPanel = false;
                    Invalidate ();
                } else {
                    ToggleSort (col);   // plain header click
                }
                return;
            }

            base.OnMouseUp (e);
        }

        // True when the point is within a few pixels of a visible column's right edge (resize handle).
        private bool NearColumnEdge (Point location)
        {
            var zone = LogicalToDeviceUnits (4);
            foreach (DataGridViewColumn column in base.Columns) {
                if (!column.Visible || column.HeaderBounds.IsEmpty)
                    continue;
                if (Math.Abs (location.X - column.HeaderBounds.Right) <= zone)
                    return true;
            }
            return false;
        }

        private void ToggleGroupRow (DataGridViewRow row)
        {
            if (row.Tag is not GridGroupRow info)
                return;

            if (!_collapsed.Remove (info.Key))
                _collapsed.Add (info.Key);

            RebuildView ();
        }

        private void HandleGroupPanelMouseDown (Point location)
        {
            foreach (var pill in GroupPillLayouts) {
                if (pill.CloseBounds.Contains (location)) {
                    UngroupColumn (pill.Descriptor.PropertyName);
                    return;
                }
                if (pill.Bounds.Contains (location)) {
                    // Toggle this group's direction.
                    pill.Descriptor.Direction = pill.Descriptor.Direction == ListSortDirection.Ascending
                        ? ListSortDirection.Descending : ListSortDirection.Ascending;
                    RebuildView ();
                    return;
                }
            }
        }

        private void ShowFilterPopup (int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= base.Columns.Count)
                return;

            var column = base.Columns[columnIndex];
            var name = !string.IsNullOrEmpty (column.Name) ? column.Name : column.HeaderText;
            var current = FilterDescriptors.Find (f => NameMatches (f.PropertyName, column));

            // Anchor under the funnel glyph (fall back to the header's bottom-left).
            var anchor = FilterGlyphRects.TryGetValue (columnIndex, out var glyph)
                ? new Point (glyph.Left, column.HeaderBounds.Bottom)
                : new Point (column.HeaderBounds.Left, column.HeaderBounds.Bottom);

            RadGridFilterPopup.Show (this, name, DistinctValues (columnIndex), current,
                PointToScreen (anchor), descriptor => {
                    if (current is not null)
                        FilterDescriptors.Remove (current);
                    if (descriptor is not null) {
                        descriptor.PropertyName = name;
                        FilterDescriptors.Add (descriptor);
                    } else {
                        RebuildView ();
                    }
                    Invalidate ();
                });
        }

        // ── Layout persistence (Telerik-shaped XML) ──

        /// <summary>Serializes the column layout, sort/group/filter descriptors to a Telerik-shaped XML string.</summary>
        public string SaveLayoutToString ()
        {
            var doc = new XElement ("RadGridView",
                new XElement ("Columns",
                    base.Columns.Select ((c, i) => new XElement ("Column",
                        new XAttribute ("Name", c.Name ?? string.Empty),
                        new XAttribute ("HeaderText", c.HeaderText ?? string.Empty),
                        new XAttribute ("Width", c.Width),
                        new XAttribute ("Index", i),
                        new XAttribute ("IsVisible", c.Visible),
                        new XAttribute ("SortOrder", c.SortOrder)))),
                new XElement ("SortDescriptors",
                    SortDescriptors.Select (s => new XElement ("SortDescriptor",
                        new XAttribute ("PropertyName", s.PropertyName),
                        new XAttribute ("Direction", s.Direction)))),
                new XElement ("GroupDescriptors",
                    GroupDescriptors.Select (g => new XElement ("GroupDescriptor",
                        new XAttribute ("PropertyName", g.PropertyName),
                        new XAttribute ("Direction", g.Direction)))),
                new XElement ("FilterDescriptors",
                    FilterDescriptors.Select (f => new XElement ("FilterDescriptor",
                        new XAttribute ("PropertyName", f.PropertyName),
                        new XAttribute ("Operator", f.Operator),
                        new XAttribute ("Value", f.Value?.ToString () ?? string.Empty),
                        f.SelectedValues is null ? null : new XElement ("SelectedValues",
                            f.SelectedValues.Select (v => new XElement ("Value", v)))))));

            return new XDocument (doc).ToString ();
        }

        /// <summary>Saves the layout (see <see cref="SaveLayoutToString"/>) to the specified file.</summary>
        public void SaveLayout (string fileName) => File.WriteAllText (fileName, SaveLayoutToString ());

        /// <summary>Loads layout XML previously produced by <see cref="SaveLayoutToString"/>.</summary>
        public void LoadLayoutFromString (string xml)
        {
            if (string.IsNullOrWhiteSpace (xml))
                return;

            XElement root;
            try {
                root = XDocument.Parse (xml).Root!;
            } catch {
                return;
            }
            if (root is null)
                return;

            _suspendRebuild = true;
            try {
                // Columns: width / visibility / order.
                var cols = root.Element ("Columns")?.Elements ("Column").ToList () ?? new List<XElement> ();
                foreach (var ce in cols) {
                    var col = ColumnByName ((string?)ce.Attribute ("Name") ?? string.Empty);
                    if (col is null)
                        continue;
                    if (int.TryParse ((string?)ce.Attribute ("Width"), out var w))
                        col.Width = w;
                    if (bool.TryParse ((string?)ce.Attribute ("IsVisible"), out var vis))
                        col.Visible = vis;
                }

                // Re-order columns to match the saved Index order.
                var order = cols.OrderBy (ce => (int?)ce.Attribute ("Index") ?? 0)
                                .Select (ce => (string?)ce.Attribute ("Name") ?? string.Empty)
                                .ToList ();
                ApplyColumnOrder (order);

                // Descriptors.
                SortDescriptors.Clear ();
                foreach (var se in root.Element ("SortDescriptors")?.Elements ("SortDescriptor") ?? Enumerable.Empty<XElement> ())
                    SortDescriptors.Add (new SortDescriptor (
                        (string?)se.Attribute ("PropertyName") ?? string.Empty,
                        ParseDirection ((string?)se.Attribute ("Direction"))));

                GroupDescriptors.Clear ();
                foreach (var ge in root.Element ("GroupDescriptors")?.Elements ("GroupDescriptor") ?? Enumerable.Empty<XElement> ())
                    GroupDescriptors.Add (new GroupDescriptor (
                        (string?)ge.Attribute ("PropertyName") ?? string.Empty,
                        ParseDirection ((string?)ge.Attribute ("Direction"))));

                FilterDescriptors.Clear ();
                foreach (var fe in root.Element ("FilterDescriptors")?.Elements ("FilterDescriptor") ?? Enumerable.Empty<XElement> ()) {
                    var descriptor = new FilterDescriptor {
                        PropertyName = (string?)fe.Attribute ("PropertyName") ?? string.Empty,
                        Operator = Enum.TryParse<FilterOperator> ((string?)fe.Attribute ("Operator"), out var op) ? op : FilterOperator.None,
                        Value = (string?)fe.Attribute ("Value")
                    };
                    var values = fe.Element ("SelectedValues");
                    if (values is not null)
                        descriptor.SelectedValues = new HashSet<string> (
                            values.Elements ("Value").Select (v => v.Value), StringComparer.CurrentCultureIgnoreCase);
                    FilterDescriptors.Add (descriptor);
                }
            } finally {
                _suspendRebuild = false;
            }

            SyncSortGlyphs ();
            RefreshLayout ();
        }

        /// <summary>Loads layout XML from the specified file.</summary>
        public void LoadLayout (string fileName)
        {
            if (File.Exists (fileName))
                LoadLayoutFromString (File.ReadAllText (fileName));
        }

        private static ListSortDirection ParseDirection (string? value)
            => string.Equals (value, "Descending", StringComparison.OrdinalIgnoreCase)
                ? ListSortDirection.Descending : ListSortDirection.Ascending;

        private void ApplyColumnOrder (List<string> namesInOrder)
        {
            for (var desired = 0; desired < namesInOrder.Count; desired++) {
                var current = ColumnIndexByName (namesInOrder[desired]);
                if (current >= 0 && current != desired)
                    MoveColumn (current, desired);
            }
        }

        // ── Events (forwarded from the base grid; see ctor) ──

        private EventHandler<GridViewCellEventArgs>? _cellClick, _commandCellClick, _cellDoubleClick, _cellValueChanged, _valueChanged, _cellEndEdit;
        private EventHandler<GridViewCellCancelEventArgs>? _cellBeginEdit;
        private EventHandler? _selectionChanged, _currentRowChanged;

        /// <summary>Raised when a cell is clicked.</summary>
        public new event EventHandler<GridViewCellEventArgs>? CellClick { add => _cellClick += value; remove => _cellClick -= value; }
        /// <summary>Raised on a command-cell (button column) click. Fires alongside <see cref="CellClick"/>.</summary>
        public event EventHandler<GridViewCellEventArgs>? CommandCellClick { add => _commandCellClick += value; remove => _commandCellClick -= value; }
        /// <summary>Raised when a cell is double-clicked.</summary>
        public new event EventHandler<GridViewCellEventArgs>? CellDoubleClick { add => _cellDoubleClick += value; remove => _cellDoubleClick -= value; }
        /// <summary>Raised when a cell value changes.</summary>
        public new event EventHandler<GridViewCellEventArgs>? CellValueChanged { add => _cellValueChanged += value; remove => _cellValueChanged -= value; }
        /// <summary>Raised when a cell value changes (Telerik alias).</summary>
        public event EventHandler<GridViewCellEventArgs>? ValueChanged { add => _valueChanged += value; remove => _valueChanged -= value; }
        /// <summary>Raised when a cell edit completes.</summary>
        public new event EventHandler<GridViewCellEventArgs>? CellEndEdit { add => _cellEndEdit += value; remove => _cellEndEdit -= value; }
        /// <summary>Raised before a cell enters edit mode.</summary>
        public new event EventHandler<GridViewCellCancelEventArgs>? CellBeginEdit { add => _cellBeginEdit += value; remove => _cellBeginEdit -= value; }
        /// <summary>Raised when the selection changes.</summary>
        public new event EventHandler? SelectionChanged { add => _selectionChanged += value; remove => _selectionChanged -= value; }
        /// <summary>Raised when the current row changes (fires alongside <see cref="SelectionChanged"/>).</summary>
        public event EventHandler? CurrentRowChanged { add => _currentRowChanged += value; remove => _currentRowChanged -= value; }

        private EventHandler<GridViewCellFormattingEventArgs>? _cellFormatting, _viewCellFormatting;
        private EventHandler<GridViewRowFormattingEventArgs>? _rowFormatting;

        /// <summary>Raised when a cell is being formatted. Set <c>e.CellElement.BackColor</c>/<c>ForeColor</c> to color the cell.</summary>
        public new event EventHandler<GridViewCellFormattingEventArgs>? CellFormatting { add => _cellFormatting += value; remove => _cellFormatting -= value; }
        /// <summary>Raised when a view cell is being formatted (alias for <see cref="CellFormatting"/>).</summary>
        public event EventHandler<GridViewCellFormattingEventArgs>? ViewCellFormatting { add => _viewCellFormatting += value; remove => _viewCellFormatting -= value; }
        /// <summary>Raised when a row is being formatted. Set <c>e.RowElement.BackColor</c> (with <c>DrawFill=true</c>) to color the row.</summary>
        public event EventHandler<GridViewRowFormattingEventArgs>? RowFormatting { add => _rowFormatting += value; remove => _rowFormatting -= value; }
        private EventHandler<ContextMenuOpeningEventArgs>? _contextMenuOpening;

        /// <summary>
        /// Raised on right-click before the context menu is shown. Handlers populate
        /// <c>e.ContextMenu.Items</c> (with <see cref="RadMenuItem"/>s); the populated menu is then shown.
        /// </summary>
        public event EventHandler<ContextMenuOpeningEventArgs>? ContextMenuOpening { add => _contextMenuOpening += value; remove => _contextMenuOpening -= value; }

        /// <inheritdoc/>
        protected override void OnClick (MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) {
                var content = GetContentArea ();
                var onHeader = ColumnHeadersVisible
                    && new Rectangle (content.Left, content.Top, content.Width, ScaledHeaderHeight).Contains (e.Location);

                // Built-in header menu (group / sort / filter / best-fit), unless the consumer drives the menu.
                if (onHeader && _contextMenuOpening is null) {
                    ShowHeaderContextMenu (GetColumnAtLocation (e.Location), e.Location);
                    return;
                }

                // Telerik raises ContextMenuOpening on right-click so handlers can build the menu; show it.
                if (_contextMenuOpening is not null) {
                    var args = new ContextMenuOpeningEventArgs { RowElement = RowElementUnder (e.Location) };
                    _contextMenuOpening.Invoke (this, args);

                    if (args.ContextMenu.Items.Count > 0) {
                        var menu = new ContextMenu ();
                        foreach (var item in args.ContextMenu.Items)
                            if (item is MenuItem menuItem)
                                menu.Items.Add (menuItem);

                        if (menu.Items.Count > 0) {
                            menu.Show (this, PointToScreen (e.Location));
                            return;
                        }
                    }
                }
            }

            base.OnClick (e);
        }

        private void ShowHeaderContextMenu (int columnIndex, Point location)
        {
            if (columnIndex < 0 || columnIndex >= base.Columns.Count)
                return;

            var column = base.Columns[columnIndex];
            var name = !string.IsNullOrEmpty (column.Name) ? column.Name : column.HeaderText;
            var menu = new ContextMenu ();

            if (EnableSorting && column.Sortable) {
                menu.Items.Add (new MenuItem ("Sort Ascending", (SKBitmap?)null, (_, _) => SetSort (name, ListSortDirection.Ascending)));
                menu.Items.Add (new MenuItem ("Sort Descending", (SKBitmap?)null, (_, _) => SetSort (name, ListSortDirection.Descending)));
                menu.Items.Add (new MenuItem ("Clear Sorting", (SKBitmap?)null, (_, _) => { SortDescriptors.Clear (); SyncSortGlyphs (); }));
            }

            if (EnableGrouping) {
                var isGrouped = GroupDescriptors.Any (g => NameMatches (g.PropertyName, name));
                menu.Items.Add (new MenuItem (isGrouped ? "Ungroup This Column" : "Group By This Column", (SKBitmap?)null,
                    (_, _) => { if (isGrouped) UngroupColumn (name); else GroupByColumn (name); }));
                if (GroupDescriptors.Count > 0)
                    menu.Items.Add (new MenuItem ("Clear Grouping", (SKBitmap?)null, (_, _) => ClearGrouping ()));
            }

            if (EnableFiltering) {
                menu.Items.Add (new MenuItem ("Filter…", (SKBitmap?)null, (_, _) => ShowFilterPopup (columnIndex)));
                if (FilterDescriptors.Any (f => NameMatches (f.PropertyName, name)))
                    menu.Items.Add (new MenuItem ("Clear Filter", (SKBitmap?)null, (_, _) => ClearColumnFilter (name)));
            }

            menu.Items.Add (new MenuItem ("Best Fit Columns", (SKBitmap?)null, (_, _) => BestFitColumns ()));

            if (menu.Items.Count > 0)
                menu.Show (this, PointToScreen (location));
        }

        private void SetSort (string name, ListSortDirection direction)
        {
            _suspendRebuild = true;
            SortDescriptors.Clear ();
            SortDescriptors.Add (new SortDescriptor (name, direction));
            _suspendRebuild = false;
            SyncSortGlyphs ();
            RebuildView ();
        }

        // Best-effort hit-test: returns a row element for the row whose cell contains the point, or null.
        private GridViewRowElement? RowElementUnder (Point location)
        {
            foreach (var row in base.Rows) {
                if (IsGroupRow (row))
                    continue;
                foreach (DataGridViewCell cell in row.Cells)
                    if (cell.Bounds.Contains (location))
                        return new GridViewRowElement { RowInfo = new GridViewRowInfo (row) };
            }

            return null;
        }

        /// <inheritdoc/>
        protected override void OnDoubleClick (MouseEventArgs e)
        {
            // Don't begin editing on a group-header row; toggle it instead.
            var rowIndex = GetRowAtLocation (e.Location);
            if (rowIndex >= 0 && rowIndex < base.Rows.Count && IsGroupRow (base.Rows[rowIndex])) {
                ToggleGroupRow (base.Rows[rowIndex]);
                return;
            }

            base.OnDoubleClick (e);
        }

        /// <inheritdoc/>
        protected internal override void RaiseRowFormatting (DataGridViewRow row, int rowIndex)
        {
            // Group-header rows are drawn by the renderer; skip user formatting (they have no data cells).
            if (IsGroupRow (row))
                return;

            // Clear the per-cell colors and text overrides we manage so conditional formatting doesn't
            // leave stale values from a previous frame; alternating-row striping (drawn at row level)
            // still shows through.
            foreach (DataGridViewCell cell in row.Cells) {
                cell.Style.BackgroundColor = null;
                cell.Style.ForegroundColor = null;
                cell.FormattedTextOverride = null;
            }

            if (_rowFormatting is null)
                return;

            var element = new GridViewRowElement { RowInfo = new GridViewRowInfo (row) };
            _rowFormatting.Invoke (this, new GridViewRowFormattingEventArgs { RowElement = element, Row = element.RowInfo });

            if (element.BackColor != Color.Empty) {
                var sk = ToSK (element.BackColor);
                foreach (DataGridViewCell cell in row.Cells)
                    cell.Style.BackgroundColor = sk;
            }

            if (element.ForeColor != Color.Empty) {
                var sk = ToSK (element.ForeColor);
                foreach (DataGridViewCell cell in row.Cells)
                    cell.Style.ForegroundColor = sk;
            }
        }

        /// <inheritdoc/>
        protected internal override void RaiseCellFormatting (DataGridViewRow row, int rowIndex, int columnIndex)
        {
            if (IsGroupRow (row) || columnIndex < 0 || columnIndex >= row.Cells.Count)
                return;

            var cell = row.Cells[columnIndex];
            var column = columnIndex < base.Columns.Count ? base.Columns[columnIndex] : null;

            var originalText = cell.Value?.ToString () ?? string.Empty;
            var displayText = ComputeFormattedText (cell, column);

            // Raise the Telerik formatting events (handlers may further change Text / colors).
            if (_cellFormatting is not null || _viewCellFormatting is not null) {
                var element = new GridViewCellElement {
                    Value = cell.Value,
                    Text = displayText,
                    RowIndex = rowIndex,
                    ColumnInfo = column,
                    RowInfo = new GridViewRowInfo (row)
                };

                var args = new GridViewCellFormattingEventArgs {
                    CellElement = element, RowIndex = rowIndex, ColumnIndex = columnIndex,
                    Value = cell.Value, Row = element.RowInfo, Column = column
                };

                _cellFormatting?.Invoke (this, args);
                _viewCellFormatting?.Invoke (this, args);

                displayText = element.Text;

                // Cell formatting overrides any row-level color for this specific cell.
                if (element.BackColor != Color.Empty)
                    cell.Style.BackgroundColor = ToSK (element.BackColor);
                if (element.ForeColor != Color.Empty)
                    cell.Style.ForegroundColor = ToSK (element.ForeColor);
            }

            // Only override the displayed text when formatting actually changed it.
            if (!string.Equals (displayText, originalText, StringComparison.Ordinal))
                cell.FormattedTextOverride = displayText;
        }

        // Computes a cell's display text: a combo column resolves the stored value to its display text
        // (foreign-key → name); otherwise the column's FormatString is applied; else the raw value.
        private static string ComputeFormattedText (DataGridViewCell cell, DataGridViewColumn? column)
        {
            if (column is GridViewComboBoxColumn combo && combo.LookupDisplay (cell.Value) is string comboDisplay)
                return comboDisplay;
            if (column is GridViewColumn gvc && !string.IsNullOrEmpty (gvc.FormatString) && cell.Value is not null)
                return FormatValue (gvc.FormatString, cell.Value);
            return cell.Value?.ToString () ?? string.Empty;
        }

        // Applies a Telerik/.NET format string, supporting both composite ("{0:C2}") and bare ("C2") forms.
        private static string FormatValue (string format, object value)
        {
            try {
                return format.Contains ("{0")
                    ? string.Format (format, value)
                    : string.Format ("{0:" + format + "}", value);
            } catch {
                return value?.ToString () ?? string.Empty;
            }
        }

        private static SKColor ToSK (Color c) => new SKColor (c.R, c.G, c.B, c.A);

        // Builds a Telerik cell-event-args from row/column indices, reading the underlying grid.
        private GridViewCellEventArgs BuildCellArgs (int columnIndex, int rowIndex)
        {
            object? value = null;
            var rowInfo = RowAt (rowIndex);

            if (rowInfo is not null && columnIndex >= 0 && columnIndex < rowInfo.DataRow.Cells.Count)
                value = rowInfo.DataRow.Cells[columnIndex].Value;

            return new GridViewCellEventArgs {
                ColumnIndex = columnIndex,
                RowIndex = rowIndex,
                Value = value,
                Row = rowInfo,
                Column = columnIndex >= 0 && columnIndex < base.Columns.Count ? base.Columns[columnIndex] : null
            };
        }

        // Wraps the base row at the given index, or null if out of range / a group row.
        private GridViewRowInfo? RowAt (int rowIndex)
            => rowIndex >= 0 && rowIndex < base.Rows.Count && !IsGroupRow (base.Rows[rowIndex])
                ? new GridViewRowInfo (base.Rows[rowIndex]) : null;
    }

    /// <summary>Describes an injected group-header row (stored on <see cref="DataGridViewRow.Tag"/>).</summary>
    internal sealed class GridGroupRow
    {
        public string Field = string.Empty;
        public string HeaderText = string.Empty;
        public string Value = string.Empty;
        public int Count;
        public int Level;
        public string Key = string.Empty;
        public bool Collapsed;
    }

    /// <summary>Layout of a single group-panel "pill", published by the renderer for hit-testing.</summary>
    internal sealed class GroupPillLayout
    {
        public GroupDescriptor Descriptor = null!;
        public Rectangle Bounds;
        public Rectangle CloseBounds;
    }

    /// <summary>
    /// Telerik-compat grid configuration façade. The column/data members forward to the owning
    /// <see cref="RadGridView"/>; the descriptor collections are the grid's own.
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
        public bool AutoExpandGroups { get; set; } = true;
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
        /// <summary>Gets the sort descriptors (the grid's own collection).</summary>
        public GridDescriptorCollection<SortDescriptor> SortDescriptors => _grid.SortDescriptors;
        /// <summary>Gets the group descriptors (the grid's own collection).</summary>
        public GridDescriptorCollection<GroupDescriptor> GroupDescriptors => _grid.GroupDescriptors;
        /// <summary>Gets the filter descriptors (the grid's own collection).</summary>
        public GridDescriptorCollection<FilterDescriptor> FilterDescriptors => _grid.FilterDescriptors;
        /// <summary>Gets the summary rows shown at the bottom. Stub list.</summary>
        public List<object> SummaryRowsBottom { get; } = new ();
        /// <summary>Gets the summary rows shown at the top. Stub list.</summary>
        public List<object> SummaryRowsTop { get; } = new ();

        /// <summary>Auto-sizes columns.</summary>
        public void BestFitColumns () => _grid.BestFitColumns ();
        /// <summary>Refreshes the grid.</summary>
        public void Refresh () => _grid.Invalidate ();
        /// <summary>Expands all groups.</summary>
        public void ExpandAllGroups () => _grid.ExpandAllGroups ();
        /// <summary>Collapses all groups.</summary>
        public void CollapseAllGroups () => _grid.CollapseAllGroups ();
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
        /// <summary>Gets or sets the cell text alignment (forwards to the rendered cell alignment).</summary>
        public ContentAlignment TextAlignment {
            get => DefaultCellStyleAlignment;
            set => DefaultCellStyleAlignment = value;
        }
        /// <summary>Gets or sets the header text alignment (forwards to the rendered header alignment).</summary>
        public ContentAlignment HeaderTextAlignment {
            get => HeaderAlignment;
            set => HeaderAlignment = value;
        }
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
        /// <summary>Gets or sets whether this column can be sorted (forwards to the base sortable flag).</summary>
        public bool AllowSort {
            get => Sortable;
            set => Sortable = value;
        }
        /// <summary>Gets or sets whether this column can be grouped. Stub.</summary>
        public bool AllowGroup { get; set; } = true;
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

        private object? _cachedSource;
        private Dictionary<string, string>? _lookup;

        /// <summary>
        /// Resolves a stored cell value to its display text using <see cref="DataSource"/>,
        /// <see cref="ValueMember"/> and <see cref="DisplayMember"/> (the foreign-key-to-name lookup a
        /// Telerik combo column performs). Returns null if no mapping applies. Result is cached per
        /// DataSource reference.
        /// </summary>
        internal string? LookupDisplay (object? value)
        {
            if (value is null || DataSource is null || string.IsNullOrEmpty (ValueMember) || string.IsNullOrEmpty (DisplayMember))
                return null;

            if (!ReferenceEquals (_cachedSource, DataSource) || _lookup is null) {
                _lookup = BuildLookup ();
                _cachedSource = DataSource;
            }

            return _lookup is not null && _lookup.TryGetValue (value.ToString () ?? string.Empty, out var display) ? display : null;
        }

        private Dictionary<string, string>? BuildLookup ()
        {
            var items = AsEnumerable (DataSource);
            if (items is null)
                return null;

            var map = new Dictionary<string, string> ();

            foreach (var item in items) {
                if (item is null)
                    continue;

                var key = GetMemberValue (item, ValueMember)?.ToString ();
                var display = GetMemberValue (item, DisplayMember)?.ToString ();

                if (key is not null && display is not null)
                    map[key] = display;
            }

            return map;
        }

        // Normalizes the common DataSource shapes (DataTable / DataView / IListSource / IEnumerable) to items.
        private static IEnumerable? AsEnumerable (object? source) => source switch {
            System.Data.DataTable t => t.Rows.Cast<System.Data.DataRow> (),
            System.Data.DataView v => v.Cast<System.Data.DataRowView> (),
            System.ComponentModel.IListSource ls => ls.GetList (),
            IEnumerable e => e,
            _ => null
        };

        // Reads a named member, supporting DataRow/DataRowView columns and CLR properties.
        private static object? GetMemberValue (object item, string member)
        {
            try {
                switch (item) {
                    case System.Data.DataRowView drv:
                        return drv.Row.Table.Columns.Contains (member) ? drv[member] : null;
                    case System.Data.DataRow dr:
                        return dr.Table.Columns.Contains (member) ? dr[member] : null;
                    default:
                        return item.GetType ().GetProperty (member)?.GetValue (item);
                }
            } catch {
                return null;
            }
        }
    }

    /// <summary>Telerik-compat check-box column.</summary>
    public class GridViewCheckBoxColumn : GridViewDataColumn
    {
        /// <summary>Initializes a new instance.</summary>
        public GridViewCheckBoxColumn () { }
        /// <summary>Initializes a new instance bound to the specified field.</summary>
        public GridViewCheckBoxColumn (string fieldName) { FieldName = fieldName; Name = fieldName; }

        /// <inheritdoc/>
        protected internal override bool DisplaysAsCheckBox => true;
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
    /// The indexer and enumerator yield <see cref="GridViewRowInfo"/> for data rows only (injected
    /// group-header rows are skipped), while Count/Add/Clear/Remove operate on the real grid rows.
    /// </summary>
    public class GridViewRowInfoCollection : IEnumerable<GridViewRowInfo>
    {
        private readonly DataGridViewRowCollection _rows;

        internal GridViewRowInfoCollection (DataGridViewRowCollection rows) => _rows = rows;

        // Underlying data rows (group-header rows excluded), in display order.
        private List<DataGridViewRow> DataRows {
            get {
                var list = new List<DataGridViewRow> ();
                foreach (var row in _rows)
                    if (!RadGridView.IsGroupRow (row))
                        list.Add (row);
                return list;
            }
        }

        /// <summary>Gets the number of data rows.</summary>
        public int Count => DataRows.Count;

        /// <summary>Gets the data row at the specified index as a <see cref="GridViewRowInfo"/>.</summary>
        public GridViewRowInfo this[int index] => new GridViewRowInfo (DataRows[index]);

        /// <summary>Adds a row with the specified cell values.</summary>
        public void Add (params object[] values) => _rows.Add (values);

        /// <summary>Adds the specified row.</summary>
        public void Add (DataGridViewRow row) => _rows.Add (row);

        /// <summary>Removes the specified row.</summary>
        public void Remove (GridViewRowInfo row) => _rows.Remove (row.DataRow);

        /// <summary>Removes the data row at the specified index.</summary>
        public void RemoveAt (int index) => _rows.Remove (DataRows[index]);

        /// <summary>Removes all rows.</summary>
        public void Clear () => _rows.Clear ();

        /// <inheritdoc/>
        public IEnumerator<GridViewRowInfo> GetEnumerator ()
        {
            foreach (var row in DataRows)
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
                var existing = _row.Cells[columnName];
                if (existing is not null)
                    return new GridViewCellInfo (existing);

                // The cell doesn't exist yet. Pad the row with empty cells up to the column's index so
                // cells stay aligned with columns regardless of the order they're first accessed in.
                var dgv = _row.DataGridView;
                var colIndex = -1;
                if (dgv is not null) {
                    for (var i = 0; i < dgv.Columns.Count; i++) {
                        var col = dgv.Columns[i];
                        if (string.Equals (col.Name, columnName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals (col.DataPropertyName, columnName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals (col.HeaderText, columnName, StringComparison.OrdinalIgnoreCase)) {
                            colIndex = i;
                            break;
                        }
                    }
                }

                if (colIndex < 0)
                    colIndex = _row.Cells.Count;

                while (_row.Cells.Count <= colIndex)
                    _row.Cells.Add (string.Empty);

                return new GridViewCellInfo (_row.Cells[colIndex]);
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
