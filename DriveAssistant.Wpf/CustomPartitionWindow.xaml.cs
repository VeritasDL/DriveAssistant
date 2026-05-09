using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FATXTools.Wpf;

public partial class CustomPartitionWindow : Window
{
    private readonly long _imageLength;

    public CustomPartitionWindow(string imagePath)
    {
        InitializeComponent();
        _imageLength = File.Exists(imagePath) ? new FileInfo(imagePath).Length : 0;
        StatusTextBlock.Text = _imageLength > 0
            ? $"Image length: 0x{_imageLength:X} bytes."
            : "Image length is unknown; the partition will still be validated against positive length.";
    }

    public string PartitionName { get; private set; } = "CustomPartition";

    public long PartitionOffset { get; private set; }

    public long PartitionLength { get; private set; }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseInteger(OffsetTextBox.Text, out var offset) || offset < 0)
        {
            StatusTextBlock.Text = "Enter a valid non-negative offset.";
            return;
        }

        if (!TryParseInteger(LengthTextBox.Text, out var length) || length <= 0)
        {
            StatusTextBlock.Text = "Enter a valid positive length.";
            return;
        }

        if (_imageLength > 0 && offset >= _imageLength)
        {
            StatusTextBlock.Text = $"Start offset must be before EOF (0x{_imageLength:X}).";
            return;
        }

        if (_imageLength > 0 && length > _imageLength)
        {
            StatusTextBlock.Text = $"Partition length cannot be longer than the image (0x{_imageLength:X} bytes).";
            return;
        }

        if (_imageLength > 0 && length > _imageLength - offset)
        {
            StatusTextBlock.Text = "Partition range extends beyond the image length.";
            return;
        }

        PartitionName = string.IsNullOrWhiteSpace(NameTextBox.Text)
            ? $"Custom_{offset:X}"
            : NameTextBox.Text.Trim();
        PartitionOffset = offset;
        PartitionLength = length;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static bool TryParseInteger(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
