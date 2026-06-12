using Avalonia.Platform.Storage;

namespace Modern.Forms
{
    /// <summary>
    /// Represents a dialog for choosing a file system directory.
    /// </summary>
    public class FolderBrowserDialog : FileSystemDialog
    {
        /// <summary>
        /// Gets or sets the selected folder path.
        /// </summary>
        public string? SelectedPath { get; set; }

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

            var options = new FolderPickerOpenOptions {
                AllowMultiple = false,
                SuggestedStartLocation = startLocation,
                Title = Title
            };

            var result = await parent.OpenFolderPickerAsync (options);

            var paths = result.Select (f => f.GetFullPath ()).WhereNotNull ();

            SelectedPath = paths?.FirstOrDefault ();

            return SelectedPath is null ? DialogResult.Cancel : DialogResult.OK;
        }
    }
}
