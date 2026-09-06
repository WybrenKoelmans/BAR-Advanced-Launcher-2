using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class DialogService : IDialogService
{
    private readonly ILogger<DialogService> _logger;
    private readonly WindowContext _window;
    private readonly IUiDispatcher _dispatcher;

    public DialogService(ILogger<DialogService> logger, WindowContext window, IUiDispatcher dispatcher)
    {
        _logger = logger;
        _window = window;
        _dispatcher = dispatcher;
    }

    public Task<string?> PickFolderAsync(string? settingsIdentifier = null) =>
        OnUiThreadAsync<string?>(async () =>
        {
            if (_window.Window is null)
            {
                _logger.LogError("Cannot show the folder picker before the window exists.");
                return null;
            }

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };

            if (!string.IsNullOrEmpty(settingsIdentifier))
            {
                picker.SettingsIdentifier = settingsIdentifier;
            }

            // A FolderPicker with no filter throws on PickSingleFolderAsync.
            picker.FileTypeFilter.Add("*");

            // PLAN.md §3.1.2: an unpackaged WinUI app must hand the picker its HWND.
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window.Window));

            try
            {
                StorageFolder? folder = await picker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The folder picker failed.");
                return null;
            }
        });

    public Task<string?> PickFileAsync(
        IReadOnlyList<string> fileTypes,
        string? settingsIdentifier = null) =>
        OnUiThreadAsync<string?>(async () =>
        {
            if (_window.Window is null)
            {
                _logger.LogError("Cannot show the file picker before the window exists.");
                return null;
            }

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };

            if (!string.IsNullOrEmpty(settingsIdentifier))
            {
                picker.SettingsIdentifier = settingsIdentifier;
            }

            foreach (string fileType in fileTypes)
            {
                picker.FileTypeFilter.Add(fileType);
            }

            // PLAN.md §3.1.2: an unpackaged WinUI app must hand the picker its HWND.
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window.Window));

            try
            {
                StorageFile? file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The file picker failed.");
                return null;
            }
        });

    public Task ShowMessageAsync(string title, string message, string closeButtonText = "OK") =>
        OnUiThreadAsync<object?>(async () =>
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = closeButtonText,
            });

            return null;
        });

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "Yes",
        string closeButtonText = "Cancel") =>
        OnUiThreadAsync(async () =>
        {
            ContentDialogResult result = await ShowDialogAsync(new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = closeButtonText,
                DefaultButton = ContentDialogButton.Close,
            });

            return result == ContentDialogResult.Primary;
        });

    public Task<string?> PromptForTextAsync(
        string title,
        string prompt,
        string? initialValue = null,
        string primaryButtonText = "OK",
        Func<string, string?>? validate = null) =>
        OnUiThreadAsync<string?>(async () =>
        {
            var input = new TextBox
            {
                Text = initialValue ?? string.Empty,
                SelectionStart = (initialValue ?? string.Empty).Length,
                AcceptsReturn = false,
            };

            var error = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0),
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            panel.Children.Add(error);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            void Revalidate()
            {
                string? message = validate?.Invoke(input.Text);
                error.Text = message ?? string.Empty;
                error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = message is null;
            }

            input.TextChanged += (_, _) => Revalidate();
            Revalidate();

            // Enter confirms, which is what a one-field dialog should do.
            input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);

            ContentDialogResult result = await ShowDialogAsync(dialog);

            return result == ContentDialogResult.Primary ? input.Text.Trim() : null;
        });

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        XamlRoot? root = _window.XamlRoot;
        if (root is null)
        {
            _logger.LogError("Cannot show the dialog '{Title}': the shell has no XamlRoot yet.", dialog.Title);
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = root;

        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Two dialogs open at once throws; that must not take the app down.
            _logger.LogError(ex, "Could not show the dialog '{Title}'.", dialog.Title);
            return ContentDialogResult.None;
        }
    }

    /// <summary>Runs a UI-affine async operation on the UI thread and awaits its result.</summary>
    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>();

        _dispatcher.Post(async () =>
        {
            try
            {
                completion.SetResult(await operation());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task;
    }
}
