using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChatGPTCleanupTool.Controls;

// Only checkbox columns participate; no row source enumeration, deletion, or safety decisions here.
public static class GridCheckBehavior
{
    public static readonly DependencyProperty SingleClickProperty = DependencyProperty.RegisterAttached("SingleClick", typeof(bool), typeof(GridCheckBehavior), new PropertyMetadata(false, Changed));
    public static bool GetSingleClick(DependencyObject owner) => (bool)owner.GetValue(SingleClickProperty);
    public static void SetSingleClick(DependencyObject owner, bool value) => owner.SetValue(SingleClickProperty, value);
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    { if (owner is DataGrid grid) { grid.PreviewMouseLeftButtonDown -= Toggle; if ((bool)args.NewValue) grid.PreviewMouseLeftButtonDown += Toggle; } }
    private static void Toggle(object sender, MouseButtonEventArgs args)
    {
        if (sender is not DataGrid { IsReadOnly: false, IsEnabled: true } grid) return;
        var node = args.OriginalSource as DependencyObject;
        while (node is not null && node is not DataGridCell) node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is not DataGridCell { Column: DataGridCheckBoxColumn, IsReadOnly: false } cell) return;
        if (args.ClickCount > 1) { args.Handled = true; return; }
        grid.SelectedItem = cell.DataContext;
        grid.CurrentCell = new DataGridCellInfo(cell);
        cell.Focus();
        if (!grid.BeginEdit()) return;
        if (cell.Content is CheckBox { IsEnabled: true } box)
        {
            box.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, box.IsChecked != true);
            box.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateSource();
            grid.CommitEdit(DataGridEditingUnit.Cell, true); args.Handled = true;
        }
        else { grid.CancelEdit(DataGridEditingUnit.Cell); args.Handled = true; }
    }
}
