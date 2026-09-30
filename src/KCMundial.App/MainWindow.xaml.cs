using System.Windows;

namespace KCMundial.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 y similares no cierran: se pide confirmación (en un evento no se debe cerrar por error).
        e.Cancel = true;
        if (DataContext is ViewModels.ShellViewModel shell)
            shell.Close();
    }
}
