using System.Windows.Controls;
using KCMundial.App.ViewModels;
using KCMundial.Core.Models;

namespace KCMundial.App.Views;

public partial class AdminPanelView : UserControl
{
    public AdminPanelView()
    {
        InitializeComponent();
    }

    private void CameraComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        // Solo cuando el operador elige en la lista (no cuando el binding actualiza la selección).
        if (CameraComboBox.SelectedItem is CameraDevice device && DataContext is MainViewModel vm)
            vm.UserSelectedCamera(device);
    }
}
