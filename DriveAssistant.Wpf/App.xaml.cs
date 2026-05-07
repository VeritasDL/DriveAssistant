using System.Configuration;
using System.Data;
using System.Windows;

namespace FATXTools.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        WpfTheme.Apply(AppSettings.Load().Theme);
        base.OnStartup(e);
    }
}

