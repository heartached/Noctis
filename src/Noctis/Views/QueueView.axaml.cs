using Avalonia.Controls;
using Noctis.Helpers;

namespace Noctis.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();

        // GitHub #189: files dropped on Up Next go where the line shows (MainWindow routes the
        // drag). Its rows are Up Next itself. Not rows dragged from the library: this list's own
        // rows are drag sources, and dropping one back would queue it twice.
        var drop = new QueueDropTarget(UpNextList, QueueDropLine,
            row => Math.Clamp(row, 0, UpNextList.ItemCount),
            index => index);
        AttachedToVisualTree += (_, _) => QueueDropTarget.Register(drop);
        DetachedFromVisualTree += (_, _) => QueueDropTarget.Unregister(drop);
    }
}
