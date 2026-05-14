using FATXTools.Utilities;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace FATXTools.Wpf;

public partial class AboutWindow : Window
{
    private const string ProjectUrl = "https://github.com/rain0x06/DriveAssistant";
    private const string OriginalProjectUrl = "https://github.com/aerosoul94/FATXTools";

    public AboutWindow()
    {
        InitializeComponent();
        BuildInfoTextBlock.Text =
            $"Commit {BuildInfo.CommitHash}  |  Build {BuildInfo.BuildDate:yyyy-MM-dd HH:mm:ss}";
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
                // DragMove can throw if mouse capture changes during the drag.
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

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ProjectHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(ProjectUrl);
    }

    private void OriginalProjectHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(OriginalProjectUrl);
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
