using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DriveAssistant.Avalonia;

public sealed partial class DetachedResultsWindow : Window
{
    public DetachedResultsWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? DockRequested;

    private void Dock_Click(object? sender, RoutedEventArgs e)
    {
        DockRequested?.Invoke(this, EventArgs.Empty);
    }
}
