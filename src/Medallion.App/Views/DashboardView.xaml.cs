using System.Windows.Controls;
using System.Windows.Input;
using Medallion.App.ViewModels;

namespace Medallion.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private void OnOpenFolder(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.OpenFolderCommand.Execute(null);
    }
}
