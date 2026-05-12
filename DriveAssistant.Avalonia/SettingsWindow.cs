using global::Avalonia.Controls;
using global::Avalonia.Interactivity;
using global::Avalonia.Platform.Storage;
using DriveAssistant.Avalonia.Core;

namespace DriveAssistant.Avalonia;

public sealed class SettingsWindow : Window
{
    private readonly TextBox _keyPathBox = new();
    private readonly AppSettings _settings;
    private bool _saved;

    private SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "Settings";
        Width = 620;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _keyPathBox.Text = settings.KeyPath ?? string.Empty;
        _keyPathBox.Watermark = "Optional key folder or key file";

        var browseFolderButton = new Button { Content = "Folder", Classes = { "secondary" }, MinWidth = 82 };
        browseFolderButton.Click += BrowseFolder_Click;
        var browseFileButton = new Button { Content = "File", Classes = { "secondary" }, MinWidth = 70 };
        browseFileButton.Click += BrowseFile_Click;
        var clearButton = new Button { Content = "Clear", MinWidth = 70 };
        clearButton.Click += (_, _) => _keyPathBox.Text = string.Empty;

        var saveButton = new Button { Content = "Save", Classes = { "primary" }, MinWidth = 86 };
        saveButton.Click += Save_Click;
        var cancelButton = new Button { Content = "Cancel", MinWidth = 86 };
        cancelButton.Click += (_, _) => Close();

        Content = new Border
        {
            Padding = new global::Avalonia.Thickness(18),
            Background = global::Avalonia.Application.Current?.FindResource("PanelBgBrush") as global::Avalonia.Media.IBrush,
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(1, GridUnitType.Star),
                    new RowDefinition(GridLength.Auto)
                },
                Children =
                {
                    new TextBlock
                    {
                        Text = "Settings",
                        FontSize = 22,
                        FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                        Margin = new global::Avalonia.Thickness(0, 0, 0, 14)
                    },
                    BuildKeyPathRow(browseFolderButton, browseFileButton, clearButton),
                    BuildShortcutHeader(),
                    BuildShortcutList(),
                    BuildFooter(cancelButton, saveButton)
                }
            }
        };

        Grid.SetRow((Control)((Grid)((Border)Content).Child!).Children[1], 1);
        Grid.SetRow((Control)((Grid)((Border)Content).Child!).Children[2], 2);
        Grid.SetRow((Control)((Grid)((Border)Content).Child!).Children[3], 3);
        Grid.SetRow((Control)((Grid)((Border)Content).Child!).Children[4], 4);
    }

    public static async Task<AppSettings?> ShowAsync(Window owner, AppSettings settings)
    {
        var dialog = new SettingsWindow(settings);
        await dialog.ShowDialog(owner);
        return dialog._saved ? dialog._settings : null;
    }

    private Grid BuildKeyPathRow(Button browseFolderButton, Button browseFileButton, Button clearButton)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            Margin = new global::Avalonia.Thickness(0, 0, 0, 18)
        };
        grid.Children.Add(_keyPathBox);
        Grid.SetColumn(browseFolderButton, 1);
        Grid.SetColumn(browseFileButton, 2);
        Grid.SetColumn(clearButton, 3);
        browseFolderButton.Margin = new global::Avalonia.Thickness(8, 0, 0, 0);
        browseFileButton.Margin = new global::Avalonia.Thickness(8, 0, 0, 0);
        clearButton.Margin = new global::Avalonia.Thickness(8, 0, 0, 0);
        grid.Children.Add(browseFolderButton);
        grid.Children.Add(browseFileButton);
        grid.Children.Add(clearButton);
        return grid;
    }

    private static TextBlock BuildShortcutHeader()
    {
        return new TextBlock
        {
            Text = "Shortcuts",
            FontSize = 16,
            FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
            Margin = new global::Avalonia.Thickness(0, 0, 0, 8)
        };
    }

    private static ScrollViewer BuildShortcutList()
    {
        var stack = new StackPanel { Spacing = 6 };
        foreach (var definition in ShortcutCatalog.All)
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(1, GridUnitType.Star),
                    new ColumnDefinition(new GridLength(110))
                }
            };
            row.Children.Add(new TextBlock { Text = definition.Category, Foreground = global::Avalonia.Application.Current?.FindResource("MutedBrush") as global::Avalonia.Media.IBrush });
            var name = new TextBlock { Text = definition.Name };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var gesture = new TextBlock { Text = definition.DefaultGesture, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
            Grid.SetColumn(gesture, 2);
            row.Children.Add(gesture);
            stack.Children.Add(new Border
            {
                Background = global::Avalonia.Application.Current?.FindResource("PanelBg2Brush") as global::Avalonia.Media.IBrush,
                BorderBrush = global::Avalonia.Application.Current?.FindResource("LineSoftBrush") as global::Avalonia.Media.IBrush,
                BorderThickness = new global::Avalonia.Thickness(1),
                CornerRadius = new global::Avalonia.CornerRadius(6),
                Padding = new global::Avalonia.Thickness(10),
                Child = row
            });
        }

        return new ScrollViewer { Content = stack };
    }

    private static StackPanel BuildFooter(Button cancelButton, Button saveButton)
    {
        return new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new global::Avalonia.Thickness(0, 14, 0, 0),
            Children = { cancelButton, saveButton }
        };
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select optional key folder",
            AllowMultiple = false
        });
        _keyPathBox.Text = folders.FirstOrDefault()?.TryGetLocalPath() ?? _keyPathBox.Text;
    }

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select optional key file",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        _keyPathBox.Text = files.FirstOrDefault()?.TryGetLocalPath() ?? _keyPathBox.Text;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings.KeyPath = string.IsNullOrWhiteSpace(_keyPathBox.Text) ? null : _keyPathBox.Text.Trim();
        _settings.Shortcuts = ShortcutCatalog.DefaultMap();
        _settings.Save();
        _saved = true;
        Close();
    }
}
