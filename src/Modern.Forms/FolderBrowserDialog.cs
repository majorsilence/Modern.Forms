using Modern.Forms.Backends;

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

        /// <summary>Gets or sets the descriptive text above the tree view. Stub in Modern.Forms (used as window title).</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gets or sets whether a New Folder button is shown. Stub in Modern.Forms.</summary>
        public bool ShowNewFolderButton { get; set; } = true;

        /// <summary>Gets or sets whether the description is used as the dialog title. Stub in Modern.Forms.</summary>
        public bool UseDescriptionForTitle { get; set; }

        /// <summary>Gets or sets the root folder at which to start browsing. Stub in Modern.Forms.</summary>
        public Environment.SpecialFolder RootFolder { get; set; } = Environment.SpecialFolder.Desktop;

        /// <summary>Shows the dialog synchronously (blocking call).</summary>
        public DialogResult ShowDialog () => AsyncHelper.RunSync (() => ShowDialog (Application.OpenForms.LastOrDefault ()!));

        /// <summary>Shows the dialog with an IWin32Window owner. Synchronous wrapper.</summary>
        public DialogResult ShowDialog (IWin32Window owner) => ShowDialog ();

        /// <summary>
        /// Shows the dialog to the user.
        /// </summary>
        public async Task<DialogResult> ShowDialog (Form owner)
        {
            var request = new FolderDialogRequest {
                InitialDirectory = GetInitialDirectory (),
                Title = Title
            };

            SelectedPath = await owner.Backend.ShowOpenFolderDialog (request);

            return SelectedPath is null ? DialogResult.Cancel : DialogResult.OK;
        }
    }
}
