using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Localization;

[Collection("WpfApplicationIsolation")]
public sealed class LocalizedWindowTests
{
    [Fact]
    public Task FailureGuidanceChangesLanguageWhileRawErrorDetailsStayVerbatim() => SharedStaDispatcherHost.RunAsync(() =>
    {
        var language = new LocalizationService("en");
        const string raw = "Settings saved successfully.\r\nC:\\Private\\settings.json (0x80070005)";
        var error = new IOException(raw);
        var request = AppDialogRequest.Acknowledge(SettingsFailure.ForDialog(error, new("Message_FailedToSaveSettings")),
            DialogText.Captions.Error, AppDialogKind.Error);
        var window = new AppDialogWindow(request, language);
        try
        {
            Assert.EndsWith(raw, window.MessageText.Text, StringComparison.Ordinal);
            language.SetLanguage("es");
            Assert.StartsWith(language["Message_FailedToSaveSettings"], window.MessageText.Text, StringComparison.Ordinal);
            Assert.EndsWith(raw, window.MessageText.Text, StringComparison.Ordinal);
            Assert.Equal(raw, error.Message);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task RepeatedLanguageChangesDoNotRetainClosedDialogs()
    {
        var localization = new LocalizationService("en");
        var dialogs = new WeakReference[12];
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            for (int index = 0; index < dialogs.Length; index++)
                dialogs[index] = SwitchLanguageAndClose(localization);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(dialogs, dialog => Assert.False(dialog.IsAlive));
        GC.KeepAlive(localization);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference SwitchLanguageAndClose(LocalizationService localization)
        {
            var window = new AppDialogWindow(AppDialogRequest.ConfirmResetPerAppAudio(), localization);
            try
            {
                localization.SetLanguage("es");
                Assert.Equal("Restablecer audio por aplicación", window.Title);
                localization.SetLanguage("en");
            }
            finally { window.Close(); }
            return new WeakReference(window);
        }
    }

    [Fact]
    public Task OpenImportDialogUpdatesItsSummaryAndKeepsItsActions() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var localization = new LocalizationService("en");
        const string fileName = "Device order 'A'.zip";
        var current = new Settings();
        current.DeviceSwitching.Output.CycleDevices.Add(new CycleDevice { Id = "device", Name = "Device order" });
        var preview = AppSettingsTransferCoordinator.DescribeImport(current, new Settings());
        string PresentImport(LocalizationService language) => language.FormatKey("Message_Importing0WillReplaceYourCurrentSaved", fileName)
            + "\n\n" + preview(language);
        var request = new AppDialogRequest(
            PresentImport(localization),
            "Import Settings", AppDialogKind.Warning,
            [new("_Import", AppDialogResult.Confirmed, AppDialogActionStyle.Destructive),
             new("_Cancel", AppDialogResult.Declined, isDefault: true, isCancel: true)],
            allowCopy: true,
            summary: "You also have unsaved local edits. Continuing will discard those edits.")
        {
            MessagePresentation = PresentImport,
        };
        var window = new AppDialogWindow(request, localization);
        try
        {
            Button cancel = Assert.IsType<Button>(window.ActionsPanel.Children[1]);
            var copy = Assert.IsType<Button>(window.FindName("CopyButton"));
            Assert.StartsWith("Copies only the displayed dialog message", AutomationProperties.GetHelpText(copy), StringComparison.Ordinal);
            window.MessageText.Select(4, 6);
            localization.SetLanguage("es");
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal("Importar ajustes", window.Title);
            Assert.StartsWith("Copia al portapapeles", AutomationProperties.GetHelpText(copy), StringComparison.Ordinal);
            Assert.Contains($"«{fileName}»", window.MessageText.Text, StringComparison.Ordinal);
            Assert.Contains("Orden de salida: Device order → Ninguno", window.MessageText.Text, StringComparison.Ordinal);
            Assert.StartsWith("También tienes cambios", window.SummaryText.Text, StringComparison.Ordinal);
            Assert.Equal(4, window.MessageText.SelectionStart);
            Assert.Equal(6, window.MessageText.SelectionLength);
            Assert.Same(cancel, window.ActionsPanel.Children[1]);
            Assert.True(cancel.IsDefault);
            localization.SetLanguage("en");
            Assert.StartsWith("Importing", window.MessageText.Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task PickerChangesLanguageDuringAnAcceptedSwitch_WithoutRebuildingRowsOrSendingAgain() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var localization = new LocalizationService("en");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        QuickDevicePickerItem[] devices = [new("output", "Original hardware name", null, true, true, false, false)];
        int sends = 0;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = new ResourceDictionary { Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative) };
        application.Resources.MergedDictionaries.Add(theme);
        var window = new QuickDevicePickerWindow(_ => Task.FromResult(new QuickDevicePickerSnapshot(devices)),
            _ => { sends++; started.SetResult(); return completion.Task; }, 0, localization: localization);
        try
        {
            await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
            var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
            model.Search = "Original";
            var rows = model.Items;
            var selected = model.Selected;
            Task operation = TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            localization.SetLanguage("es");
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal("Selector de dispositivos", window.Title);
            Assert.Same(rows, model.Items);
            Assert.Same(selected, model.Selected);
            Assert.Equal("Original", model.Search);
            Assert.True(model.Busy);
            Assert.False(operation.IsCompleted);
            Assert.Equal(1, sends);
            Assert.Equal("Cambiando…", localization.Format(model.MessageText));
            Assert.Equal("Original hardware name", model.Items[0].Name);
            completion.SetResult(false);
            await operation;
            Assert.Equal("No se pudo cambiar. Comprueba la conexión del dispositivo e inténtalo de nuevo.", localization.Format(model.MessageText));
        }
        finally
        {
            completion.TrySetResult(false);
            window.Close();
            application.Resources.MergedDictionaries.Remove(theme);
        }
    });

    [Fact]
    public Task OpenConfirmationUpdatesItsText_WhileKeepingItsActionObjectsAndSafeDefault() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var localization = new LocalizationService("en");
        var window = new AppDialogWindow(AppDialogRequest.ConfirmResetPerAppAudio(), localization);
        try
        {
            Button confirm = Assert.IsType<Button>(window.ActionsPanel.Children[0]);
            Button cancel = Assert.IsType<Button>(window.ActionsPanel.Children[1]);
            localization.SetLanguage("es");
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal("Restablecer audio por aplicación", window.Title);
            Assert.Contains("¿Quieres continuar?", window.MessageText.Text, StringComparison.Ordinal);
            Assert.StartsWith("Diálogo de advertencia:", AutomationProperties.GetName(window), StringComparison.Ordinal);
            Assert.Same(confirm, window.ActionsPanel.Children[0]);
            Assert.Same(cancel, window.ActionsPanel.Children[1]);
            Assert.Equal("_Borrar asignaciones", confirm.Content);
            Assert.Equal("_Cancelar", cancel.Content);
            Assert.False(confirm.IsDefault);
            Assert.True(cancel.IsDefault);
            window.Complete(AppDialogResult.Declined);
            Assert.Equal(AppDialogResult.Declined, window.Result);
            localization.SetLanguage("en");
            Assert.Equal(AppDialogResult.Declined, window.Result);
        }
        finally { window.Close(); }
    });
}
