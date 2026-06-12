using Avalonia.Platform.Storage;

namespace Modern.Forms
{
    /// <summary>
    /// Represents a class for a file open dialog.
    /// </summary>
    public class OpenFileDialog : FileDialog
    {
        /// <summary>
        /// Gets or sets whether multiple files can be selected.
        /// </summary>
        public bool AllowMultiple { get; set; }

        /// <summary>
        /// Shows the dialog to the user.
        /// </summary>
        public async Task<DialogResult> ShowDialog (Form owner)
        {
            var parent = owner.AvWindow.StorageProvider;

            IStorageFolder? startLocation = null;
            var initPath = GetInitialDirectory ();
            if (initPath is not null)
                startLocation = await parent.TryGetFolderFromPathAsync (new Uri (initPath));

            var options = new FilePickerOpenOptions {
                AllowMultiple = AllowMultiple,
                SuggestedStartLocation = startLocation,
                Title = Title,
                FileTypeFilter = filters
            };

            var result = await parent.OpenFilePickerAsync (options);

            FileNames.Clear ();

            var files = result.Select (f => f.GetFullPath ()).WhereNotNull ();

            if (files.Any ())
                FileNames.AddRange (files);

            return FileNames.Count > 0 ? DialogResult.OK : DialogResult.Cancel;
        }
    }
}
