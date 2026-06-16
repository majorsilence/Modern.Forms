namespace Modern.Forms.Backends
{
    /// <summary>
    /// Holds the active <see cref="IPlatformBackend"/>. Defaults to the Avalonia backend. Assign a
    /// different backend (e.g. a Uno backend) before the first window is created or
    /// <see cref="Modern.Forms.Application.Run(Form)"/> is called.
    /// </summary>
    public static class Platform
    {
        private static IPlatformBackend? _backend;

        /// <summary>Gets or sets the active platform backend. Defaults to <see cref="AvaloniaPlatformBackend"/>.</summary>
        public static IPlatformBackend Backend {
            get => _backend ??= new AvaloniaPlatformBackend ();
            set => _backend = value;
        }
    }
}
