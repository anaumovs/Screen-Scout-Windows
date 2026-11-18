namespace ScreenScout;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--verify-editor") { Environment.ExitCode = EditorVerifier.Run(Path.GetFullPath(args[1])); return; }
        using var lease = new Mutex(true, @"Local\ScreenScout.Windows.SingleInstance", out bool acquired);
        if (!acquired) { MessageBox.Show("Another Screen Scout instance is open. Close it before starting this one.", "Screen Scout"); return; }
        try
        {
            using var window = new MainForm(args);
            Application.ThreadException += (_, e) => { window.EmergencyStop(); MessageBox.Show(window, e.Exception.Message, "Screen Scout stopped"); };
            Application.Run(window);
        }
        finally { lease.ReleaseMutex(); }
    }
}
