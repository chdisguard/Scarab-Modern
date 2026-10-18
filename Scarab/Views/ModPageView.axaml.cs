using System.Diagnostics;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
// Resources is a field in Avalonia UserControls, so alias it for brevity
using Localization = Scarab.Resources;

namespace Scarab.Views;

[UsedImplicitly]
public partial class ModPageView : ReactiveUserControl<ModPageViewModel>
{
    private WindowNotificationManager? _notify;

    public ModPageView()
    {
        InitializeComponent();

        UserControl.KeyDown += OnKeyDown;

        this.WhenActivatedVM((vm, d) =>
        {
            vm.CompletedAction += OnComplete;
            vm.ExceptionRaised += OnError;
            Disposable.Create(() =>
            {
                vm.CompletedAction -= OnComplete;
                vm.ExceptionRaised -= OnError;
            }).DisposeWith(d);

            this.WhenAnyValue(x => x.TagBox.SelectionBoxItem)
                .Subscribe(x => vm.SelectedTag = (Tag) (x ?? Models.Tag.All))
                .DisposeWith(d);
        });
    }

    private void OnError(ModPageViewModel.ModAction act, Exception e, ModItem? m)
    {
        Dispatcher.UIThread.Post(() => DiagnosticWindow.ShowError(e, $"{act}: {m?.Name ?? "Modding API"}", ViewModel?.SettingsForDiagnostics,
            TopLevel.GetTopLevel(this) as Window));
    }

    private void OnComplete(ModPageViewModel.ModAction act, ModItem mod)
    {
        string act_s = act switch
        {
            ModPageViewModel.ModAction.Install => Localization.NOTIFY_Installed,
            ModPageViewModel.ModAction.Update => Localization.NOTIFY_Updated,
            ModPageViewModel.ModAction.Uninstall => Localization.NOTIFY_Uninstalled,
            // We don't display notifications for toggling - but keep an explicit arm for the sake of total matching
            ModPageViewModel.ModAction.Toggle => throw new ArgumentOutOfRangeException(nameof(act), act, null),
            _ => throw new ArgumentOutOfRangeException(nameof(act), act, null)
        };

        _notify?.Show(new Notification(
            "Success!",
            $"{act_s} {mod.Name}!",
            NotificationType.Success
        ));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var tl = TopLevel.GetTopLevel(this);

        _notify = new WindowNotificationManager(tl) { MaxItems = 3 };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            Search.Focus();
            e.Handled = true;
        }
    }
}