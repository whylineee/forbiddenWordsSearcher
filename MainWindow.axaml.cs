using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.IO;
using System.Linq;

namespace ForbiddenWordsSearcher;

public partial class MainWindow : Window
{
    private SearchService _searchService;
    private int _logLines = 0;
    private const int MaxLogLines = 100; // prevent memory bloat
    private string _loadedWordsFilePath = "";

    public MainWindow()
    {
        InitializeComponent();
        _searchService = new SearchService();
        _searchService.LogMessage += OnLogMessage;
        _searchService.ProgressUpdated += OnProgressUpdated;
        _searchService.SearchFinished += OnSearchFinished;
    }

    private void OnLogMessage(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (LogTextBlock.Text == null) LogTextBlock.Text = "";
            LogTextBlock.Text += $"{DateTime.Now:HH:mm:ss} - {message}\n";
            _logLines++;
            
            if (_logLines > MaxLogLines)
            {
                var lines = LogTextBlock.Text.Split('\n');
                LogTextBlock.Text = string.Join('\n', lines.Skip(lines.Length - MaxLogLines));
                _logLines = MaxLogLines;
            }
            LogScrollViewer.ScrollToEnd();
        });
    }

    private void OnProgressUpdated(int processed, int totalFound)
    {
        Dispatcher.UIThread.Post(() =>
        {
            MainProgressBar.Maximum = totalFound == 0 ? 1 : totalFound;
            MainProgressBar.Value = processed;
            StatsText.Text = $"Files: {processed} / {totalFound}";
        });
    }

    private void OnSearchFinished()
    {
        Dispatcher.UIThread.Post(() =>
        {
            StartBtn.IsEnabled = true;
            PauseBtn.IsEnabled = false;
            ResumeBtn.IsEnabled = false;
            StopBtn.IsEnabled = false;
            WordsTextBox.IsEnabled = true;
            LoadWordsBtn.IsEnabled = true;
            BrowseFolderBtn.IsEnabled = true;
            ProgressText.Text = "Finished or Stopped.";
            MainProgressBar.Value = MainProgressBar.Maximum; // complete the bar visually
        });
    }

    private async void LoadWordsBtn_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Forbidden Words File",
            AllowMultiple = false
        });

        if (files.Count >= 1)
        {
            try
            {
                _loadedWordsFilePath = files[0].Path.LocalPath;
                var content = await File.ReadAllTextAsync(_loadedWordsFilePath);
                // split by commas, newlines or spaces
                var words = content.Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                WordsTextBox.Text = string.Join(", ", words);
            }
            catch (Exception ex)
            {
                OnLogMessage($"Error loading file: {ex.Message}");
            }
        }
    }

    private async void BrowseFolderBtn_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var folders = await topLevel!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Destination Folder"
        });

        if (folders.Count >= 1)
        {
            DestinationTextBox.Text = folders[0].Path.LocalPath;
        }
    }

    private void StartBtn_Click(object? sender, RoutedEventArgs e)
    {
        var wordsText = WordsTextBox.Text ?? "";
        var words = wordsText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(w => w.Trim())
                             .Where(w => !string.IsNullOrEmpty(w))
                             .ToArray();

        var destination = DestinationTextBox.Text;

        if (words.Length == 0)
        {
            OnLogMessage("Please enter or load some forbidden words.");
            return;
        }

        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
        {
            OnLogMessage("Please select a valid destination folder.");
            return;
        }

        LogTextBlock.Text = ""; // clear log
        _logLines = 0;
        MainProgressBar.Value = 0;
        StatsText.Text = "Files: 0 / 0";
        ProgressText.Text = "Scanning...";

        // UI state
        StartBtn.IsEnabled = false;
        PauseBtn.IsEnabled = true;
        ResumeBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        WordsTextBox.IsEnabled = false;
        LoadWordsBtn.IsEnabled = false;
        BrowseFolderBtn.IsEnabled = false;

        _searchService.Start(words, destination, _loadedWordsFilePath);
    }

    private void PauseBtn_Click(object? sender, RoutedEventArgs e)
    {
        PauseBtn.IsEnabled = false;
        ResumeBtn.IsEnabled = true;
        ProgressText.Text = "Paused.";
        _searchService.Pause();
    }

    private void ResumeBtn_Click(object? sender, RoutedEventArgs e)
    {
        ResumeBtn.IsEnabled = false;
        PauseBtn.IsEnabled = true;
        ProgressText.Text = "Scanning...";
        _searchService.Resume();
    }

    private void StopBtn_Click(object? sender, RoutedEventArgs e)
    {
        StopBtn.IsEnabled = false;
        PauseBtn.IsEnabled = false;
        ResumeBtn.IsEnabled = false;
        ProgressText.Text = "Stopping...";
        _searchService.Stop();
    }
}