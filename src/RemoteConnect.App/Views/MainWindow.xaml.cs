using System.ComponentModel;
using System.Windows;
using RemoteConnect.App.ViewModels;

namespace RemoteConnect.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Connect.PasswordFilled += password => ConnectPassword.Password = password;
        viewModel.Host.PermanentPasswordSaved += () => PermanentPassword.Clear();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.Sessions.IsActive &&
            MessageBox.Show(this, "Идёт сессия. Завершить её и выйти?", "RemoteConnect", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void ConnectPassword_PasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Connect.Password = ConnectPassword.Password;

    private void PermanentPassword_PasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Host.NewPermanentPassword = PermanentPassword.Password;
}
