using System.Text;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Scarab.Views;

public sealed class DiagnosticWindow : Window
{
    public DiagnosticWindow(DiagnosticReport report)
    {
        Title = "Scarab Modern — диагностика";
        Width = 850; Height = 620; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var status = new TextBlock
        {
            Text = report.SavedPath is null ? "Выделите и скопируйте текст или сохраните отчёт в файл." : "Отчёт автоматически сохранён: " + report.SavedPath,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        var details = new TextBox { Text = report.Text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Сохранить в .txt" };
        var copy = new Button { Content = "Копировать" };
        var close = new Button { Content = "Закрыть" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(save); buttons.Children.Add(copy); buttons.Children.Add(close);
        save.Click += async (_, _) =>
        {
            try
            {
                var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Сохранить отчёт Scarab Modern", SuggestedFileName = $"Scarab-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                    DefaultExtension = "txt", FileTypeChoices = new[] { new FilePickerFileType("Текстовый файл") { Patterns = new[] { "*.txt" } } }
                });
                if (target is null) return;
                await using var stream = await target.OpenWriteAsync();
                if (stream.CanSeek) { stream.SetLength(0); stream.Position = 0; }
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(report.Text);
                await writer.FlushAsync();
                status.Text = "Отчёт сохранён.";
            }
            catch (Exception e) { status.Text = "Не удалось сохранить: " + e.Message; }
        };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (Clipboard is null) { status.Text = "Буфер обмена недоступен; сохраните файл."; return; }
                await Clipboard.SetTextAsync(report.Text);
                status.Text = "Скопировано.";
            }
            catch (Exception e) { status.Text = "Не удалось скопировать: " + e.Message; }
        };
        close.Click += (_, _) => Close();
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(status, Dock.Top); panel.Children.Add(status);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(details); Content = panel;
    }

    public static void ShowError(Exception error, string operation, ISettings? settings, Window? owner)
    {
        var report = Diagnostics.Capture(error, operation, settings);
        Dispatcher.UIThread.Post(() =>
        {
            var window = new DiagnosticWindow(report);
            if (owner is { IsVisible: true }) window.Show(owner); else window.Show();
        });
    }
}
