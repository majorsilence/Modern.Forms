using System;
using System.Threading;
using Avalonia.Threading;

namespace Modern.Forms.Backends
{
    /// <summary>
    /// The default <see cref="IPlatformBackend"/>: hosts Modern.Forms on Avalonia 12. Application
    /// bootstrap and the message loop are delegated to Avalonia's <see cref="Dispatcher"/>.
    /// </summary>
    public sealed class AvaloniaPlatformBackend : IPlatformBackend
    {
        /// <inheritdoc/>
        public string Name => "Avalonia";

        /// <inheritdoc/>
        public void Initialize ()
        {
            AvaloniaBootstrap.EnsureInitialized ();
            AvaloniaSynchronizationContext.InstallIfNeeded ();
        }

        /// <inheritdoc/>
        public void RunMainLoop (CancellationToken token) => Dispatcher.UIThread.MainLoop (token);

        /// <inheritdoc/>
        public void Stop () { /* Loop exit is driven by the cancellation token passed to RunMainLoop. */ }

        /// <inheritdoc/>
        public void Post (Action action) => Dispatcher.UIThread.Post (action);

        /// <inheritdoc/>
        public void Invoke (Action action)
        {
            if (Dispatcher.UIThread.CheckAccess ())
                action ();
            else
                Dispatcher.UIThread.InvokeAsync (action).GetAwaiter ().GetResult ();
        }

        /// <inheritdoc/>
        public bool CheckAccess () => Dispatcher.UIThread.CheckAccess ();

        /// <inheritdoc/>
        public void DoEvents () => Dispatcher.UIThread.RunJobs ();

        /// <inheritdoc/>
        public IWindowBackend CreateWindow (WindowBase owner, bool isPopup)
            => isPopup ? new ModernFormsPopupWindowHost (owner) : new ModernFormsWindowHost (owner);
    }
}
