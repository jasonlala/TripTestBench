// ============================================================================
// 文件：HistoryView.xaml.cs
// 作用：数据查询界面视图的代码后置。
//       查询、筛选、导出 Excel、打印等逻辑均在 HistoryViewModel 中，
//       此处只处理一件纯视图交互：双击记录行查看曲线截图。
// ============================================================================

using System.Windows.Controls;   // UserControl
using System.Windows.Input;      // MouseButtonEventArgs
using TripTestBench.ViewModels;  // HistoryViewModel

namespace TripTestBench.Views;

/// <summary>数据查询（历史记录）视图。</summary>
public partial class HistoryView : UserControl
{
    /// <summary>构造视图：加载 XAML 定义的控件树。</summary>
    public HistoryView()
    {
        InitializeComponent();
    }

    /// <summary>双击记录行：弹出该条记录保存的曲线截图（无截图时底部提示）。</summary>
    private void RecordGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not HistoryViewModel viewModel)
        {
            return;
        }

        // 只有点在数据行上才响应，点表头 / 空白区域不弹窗
        if (RecordGrid.SelectedItem is null)
        {
            return;
        }

        if (viewModel.ViewChartCommand.CanExecute(RecordGrid.SelectedItem))
        {
            viewModel.ViewChartCommand.Execute(RecordGrid.SelectedItem);
        }
    }
}
