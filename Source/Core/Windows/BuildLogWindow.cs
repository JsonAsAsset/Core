using System.Collections.Specialized;
using System.Linq;
using System.Text;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Core.Framework.Models;
using Core.Models.Plugins;
using Core.WindowModels;

namespace Core.Windows;

/* ~~~ BuildLogWindow ~~~ */
public partial class BuildLogWindow : WindowBase<BuildLogWindowModel>
{
    public BuildLogWindowModel WM => WindowModel;

    public BuildLogWindow(UnrealProject project) : base(new BuildLogWindowModel(), initializeModel: false)
    {
        InitializeComponent();

        WindowModel.Project = project;
        DataContext = WindowModel;

        project.BuildLog.CollectionChanged += OnLogChanged;
        Closed += (_, _) => project.BuildLog.CollectionChanged -= OnLogChanged;

        ScrollToEnd();
    }

    /* Follows the tail while the build runs, unless the reader turned that off */
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (!WM.AutoScroll) return;

        /* Queued behind the item container the collection change is about to create */
        Dispatcher.UIThread.Post(() =>
        {
            if (WM.Project.BuildLog.LastOrDefault() is { } last)
            {
                LogList.ScrollIntoView(last);
            }
        }, DispatcherPriority.Background);
    }

    private void CopyLog(object? sender, RoutedEventArgs e)
    {
        var builder = new StringBuilder();

        foreach (var line in WM.Project.BuildLog)
        {
            if (line.HasCounter)
            {
                builder.Append('[').Append(line.Counter).Append("] ");
            }

            builder.AppendLine(line.Text);
        }

        App.CopyText(builder.ToString());
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
