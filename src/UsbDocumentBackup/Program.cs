using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
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
                "USB 문서 백업",
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
