using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Modern.Forms
{
    /// <summary>
    /// Class for interacting with an operating system's clipboard.
    /// </summary>
    public static class Clipboard
    {
        private static IClipboard? GetClipboard ()
            => Application.OpenForms.FirstOrDefault ()?.AvWindow?.Clipboard;

        /// <summary>Gets the contents of the clipboard as text.</summary>
        public static async Task<string?> GetTextAsync ()
        {
            var cb = GetClipboard ();
            return cb is not null ? await cb.TryGetTextAsync ().ConfigureAwait (false) : null;
        }

        /// <summary>Sets the text contents of the clipboard.</summary>
        public static async Task SetTextAsync (string text)
        {
            var cb = GetClipboard ();
            if (cb is not null)
                await cb.SetValueAsync (DataFormat.Text, text).ConfigureAwait (false);
        }

        /// <summary>Clears the contents of the clipboard.</summary>
        public static async Task ClearAsync ()
        {
            var cb = GetClipboard ();
            if (cb is not null)
                await cb.ClearAsync ().ConfigureAwait (false);
        }
    }
}
