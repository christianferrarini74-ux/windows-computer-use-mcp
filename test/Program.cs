using System.Windows;

namespace WindowsComputerUse.TestBench;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        app.Run(new MainWindow());
    }
}
