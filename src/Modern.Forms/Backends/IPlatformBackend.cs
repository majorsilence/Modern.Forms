using System;
using System.Threading;

namespace Modern.Forms.Backends
{
    /// <summary>
    /// Abstraction over the host UI platform (windowing toolkit + message loop) that Modern.Forms
    /// runs on. Modern.Forms does all of its own drawing with SkiaSharp; a backend only provides
    /// application bootstrap, the UI thread / message loop, and (in later phases) windows and input.
    ///
    /// The default backend is Avalonia (<see cref="AvaloniaPlatformBackend"/>); a Uno backend can be
    /// substituted via <see cref="Platform.Backend"/> before the first window is created.
    /// </summary>
    public interface IPlatformBackend
    {
        /// <summary>A short identifier for the backend (e.g. "Avalonia", "Uno").</summary>
        string Name { get; }

        /// <summary>Performs one-time platform initialization. Safe to call repeatedly (idempotent).</summary>
        void Initialize ();

        /// <summary>Runs the UI message loop, blocking until the token is cancelled.</summary>
        void RunMainLoop (CancellationToken token);

        /// <summary>Requests the running message loop to exit.</summary>
        void Stop ();

        /// <summary>Posts an action to run asynchronously on the UI thread.</summary>
        void Post (Action action);

        /// <summary>Runs an action synchronously on the UI thread (marshalling if needed).</summary>
        void Invoke (Action action);

        /// <summary>Returns whether the calling thread is the UI thread.</summary>
        bool CheckAccess ();

        /// <summary>Processes pending UI work without exiting the loop (WinForms Application.DoEvents).</summary>
        void DoEvents ();

        /// <summary>
        /// Creates a native window for the given owner. <paramref name="isPopup"/> requests a
        /// borderless, top-most popup (e.g. a ComboBox dropdown or context menu) rather than a
        /// top-level window.
        /// </summary>
        IWindowBackend CreateWindow (WindowBase owner, bool isPopup);
    }
}
