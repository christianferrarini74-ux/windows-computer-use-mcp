using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowsComputerUse.TestBench;

public partial class MainWindow : Window
{
    private int _dynamicActionCount;

    public MainWindow()
    {
        InitializeComponent();

        for (int i = 1; i <= 24; i++)
        {
            int row = i;
            var button = new Button
            {
                Content = $"Scroll row {row:00}",
                Tag = row,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(8, 5, 8, 5)
            };
            AutomationProperties.SetAutomationId(button, $"scrollRow{row:00}");
            AutomationProperties.SetName(button, $"Scroll row {row:00}");
            button.Click += ScrollRow_Click;
            ScrollItemsPanel.Children.Add(button);
        }

        TestComboBox.SelectedIndex = 0;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        OutputTextBox.Text = InputTextBox.Text;
        SetStatus($"Text applied: {InputTextBox.Text}");
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => ResetAll();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            ApplyButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void TestCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (StatusText is not null)
            SetStatus($"Checkbox is {(TestCheckBox.IsChecked == true ? "checked" : "unchecked")}.");
    }

    private void TestComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusText is not null && TestComboBox.SelectedItem is ComboBoxItem item)
            SetStatus($"ComboBox selected: {item.Content}");
    }

    private void TestListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusText is not null && TestListBox.SelectedItem is ListBoxItem item)
            SetStatus($"List row selected: {item.Content}");
    }

    private void TestExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (StatusText is not null && TestExpander is not null)
            SetStatus($"Expander is {(TestExpander.IsExpanded ? "expanded" : "collapsed")}.");
    }

    private void TestSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderValueText is not null)
        {
            SliderValueText.Text = $"Slider value: {TestSlider.Value:0}";
            SetStatus($"Slider set to {TestSlider.Value:0}.");
        }
    }

    private void CreateDynamicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dynamicActionCount > 0)
        {
            SetStatus("The dynamic control already exists. Use Reset all to remove it.");
            return;
        }

        _dynamicActionCount++;
        var button = new Button
        {
            Content = "Dynamic action ready",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 10, 5)
        };
        AutomationProperties.SetAutomationId(button, "dynamicActionButton");
        AutomationProperties.SetName(button, "Dynamic action ready");
        button.Click += (_, _) => SetStatus("Dynamic UIA action invoked.");
        DynamicItemsPanel.Children.Add(button);
        SetStatus("Dynamic UIA control created; wait for dynamicActionButton.");
    }

    private void OpenDialogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TestDialog { Owner = this };
        bool? result = dialog.ShowDialog();
        SetStatus(result == true ? "Modal dialog closed with its button." : "Modal dialog closed.");
    }

    private void ScrollRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int row)
            SetStatus($"Clicked scroll row {row:00}.");
    }

    private void ResetAll()
    {
        InputTextBox.Text = "Computer Use test text";
        OutputTextBox.Text = "(nothing applied yet)";
        TestCheckBox.IsChecked = false;
        TestComboBox.SelectedIndex = 0;
        TestListBox.SelectedIndex = -1;
        TestExpander.IsExpanded = false;
        TestSlider.Value = 50;
        TestScrollViewer.ScrollToTop();
        DynamicItemsPanel.Children.Clear();
        _dynamicActionCount = 0;
        SetStatus("Ready — reset returns every control to this state.");
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
