using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Medallion.App.ViewModels;

namespace Medallion.App.Views;

public partial class LibraryView : UserControl
{
    private readonly LibraryViewModel _viewModel = new();

    public LibraryView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public void Refresh() => _viewModel.Refresh();

    private void OnThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItemViewModel item)
            item.PlayCommand.Execute(null);
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipItemViewModel item) return;

        switch (e.Key)
        {
            case Key.Enter:
                item.CommitRenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                item.CancelRenameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipItemViewModel item) return;

        var answer = MessageBox.Show(
            $"Delete \"{item.Name}\" permanently?",
            "Delete clip", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.OK) item.DeleteCommand.Execute(null);
    }
}
