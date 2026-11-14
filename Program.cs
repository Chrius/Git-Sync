using System.Windows.Forms;

namespace GitSync;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Native dark mode for the Windows controls (TabControl, CheckBox...),
        // so the dialogs match the main window.
        Application.SetColorMode(SystemColorMode.Dark);

        try
        {
            Database.Prepare();
        }
        catch (Exception error)
        {
            // Without the database there is no program: better to say why than
            // to open an empty window that fails on every click. Happens with a
            // read-only folder, such as a write-protected pen drive.
            MessageBox.Show(
                "Could not open the database at\n" + AppPaths.DatabaseFile + "\n\n" + error.Message,
                "Git-Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm());
    }
}
