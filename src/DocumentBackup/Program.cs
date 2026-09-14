using DocumentBackup.Storage;
using DocumentBackup.Windows;

namespace DocumentBackup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Diagnostics run without the single-instance lock and without starting the tray, so the
        // report can be produced on a PC where the app is already resident.
        if (args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();
            var paths = AppPaths.CreateDefault();
            paths.EnsureCreated();
            var store = new SettingsStore(paths.SettingsFile);
            var loaded = store.Load();
            var resolved = loaded.HasCustomArchiveRoot ? paths.WithArchiveRoot(loaded.ArchiveRoot!) : paths;
            DiagnosticReport.WriteAndShow(resolved, loaded, new GoogleDrive.GoogleConnection(resolved, new Log(resolved.LogDirectory)));
            return 0;
        }

        using var instance = SingleInstance.TryAcquire();
        if (!instance.Acquired)
        {
            // Already resident for this user. Starting a second copy would mean two writers on
            // the same archive, so this one just goes away quietly.
            return 0;
        }

        ApplicationConfiguration.Initialize();

        AppHost host;
        try
        {
            host = AppHost.Create();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "백업 프로그램을 시작하지 못했습니다.\n\n" + ex.Message,
                "문서 백업",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        try
        {
            // Reconcile anything a crash or an unplugged stick left half-finished before the tray
            // starts reporting numbers, so the counts the user sees are already true.
            host.RecoveryService.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            host.Log.Error("Startup recovery failed.", ex);
        }

        try
        {
            // Left over from the former application name; it would point at an executable that no
            // longer exists. The current registration, if any, was written under the new name.
            if (AutoStart.RemoveFormerRegistration())
            {
                host.Log.Info("Removed the startup entry left by the previous application name.");
            }
        }
        catch (Exception ex)
        {
            host.Log.Warn("Could not tidy the old startup entry: " + ex.Message);
        }

        try
        {
            // A client_secret*.json dropped next to the executable configures the app without any
            // clicking. It still never signs in on its own; the user presses Connect.
            host.Google.TryAutoImportClientSecrets();

            // Silently pick up a previously authorised account so uploads resume on their own after
            // a reboot. This never opens a browser: if the stored token is no longer good the state
            // becomes "reconnect required" and the user decides when to deal with it.
            host.Google.TryRestoreAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            host.Log.Error("Restoring the Google connection failed.", ex);
        }

        using var context = new TrayApplicationContext(host);
        Application.Run(context);

        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }
}
