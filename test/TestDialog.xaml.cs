using System.Windows;

namespace WindowsComputerUse.TestBench;

public partial class TestDialog : Window
{
    public TestDialog()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
