using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Bpm.App.ViewModels;
using Bpm.Audio;

namespace Bpm.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        FileList.SelectionChanged += (_, _) =>
        {
            if (Vm is not null)
            {
                Vm.SelectedFile = FileList.SelectedItem as AudioFileItem;
            }
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private static FilePickerFileType AudioFileType => new("Audio")
    {
        Patterns = FfmpegAudioDecoder.SupportedExtensions.Select(e => "*" + e).ToArray(),
    };

    private async void OnAddFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add audio files",
            AllowMultiple = true,
            FileTypeFilter = [AudioFileType],
        });

        Vm.AddFiles(files.Select(f => f.Path.LocalPath));
    }

    private async void OnAddFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder of audio files",
            AllowMultiple = true,
        });

        foreach (var folder in folders)
        {
            Vm.AddFolder(folder.Path.LocalPath);
        }
    }

    private async void OnRescan(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            await Vm.AnalyzeAllAsync(onlyPending: false);
        }
    }

    private async void OnRescanPending(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            await Vm.AnalyzeAllAsync(onlyPending: true);
        }
    }

    private void OnRemove(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var selected = FileList.SelectedItems?.OfType<AudioFileItem>().ToList() ?? [];
        Vm.Remove(selected);
    }

    private void OnRemoveRow(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is not null && (sender as Button)?.DataContext is AudioFileItem item)
        {
            Vm.Remove([item]);
        }
    }

    private void OnCancelRow(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is not null && (sender as Button)?.DataContext is AudioFileItem item)
        {
            Vm.CancelItem(item);
        }
    }

    private void OnClear(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.Clear();

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.Cancel();

    private async void OnExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export results",
            SuggestedFileName = "bpm-results.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });

        if (file is not null)
        {
            await File.WriteAllTextAsync(file.Path.LocalPath, Vm.BuildCsv());
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var items = e.Data.GetFiles();
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            var path = item.Path.LocalPath;
            if (Directory.Exists(path))
            {
                Vm.AddFolder(path);
            }
            else
            {
                Vm.AddFiles([path]);
            }
        }

        e.Handled = true;
    }
}
