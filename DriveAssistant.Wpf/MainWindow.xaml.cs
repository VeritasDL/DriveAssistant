// Add this method to MainWindow.xaml.cs

private void Xbox360HddRecovery_Click(object sender, RoutedEventArgs e)
{
    var window = new Xbox360HddRecoveryWindow
    {
        Owner = this,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };
    window.ShowDialog();
}
