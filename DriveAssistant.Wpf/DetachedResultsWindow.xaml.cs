using System;
using System.Windows;
using System.Windows.Input;

namespace FATXTools.Wpf;

public partial class DetachedResultsWindow : Window
{
    public DetachedResultsWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? DockRequested;

    private void Dock_Click(object sender, RoutedEventArgs e)
    {
        DockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw if the mouse state changes during the drag.
            }
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximized();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
