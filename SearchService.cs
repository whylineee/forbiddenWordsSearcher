using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ForbiddenWordsSearcher;

public class SearchResultItem
{
    public string OriginalFilePath { get; set; } = "";
    public string CopiedOriginalPath { get; set; } = "";
    public string CensoredFilePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public int ReplacementsCount { get; set; }
}

public class SearchService
{
    private string[] _forbiddenWords = Array.Empty<string>();
    private string _destinationFolder = "";
    private string _ignoredFilePath = "";
    
    private CancellationTokenSource? _cts;
    private ManualResetEventSlim? _pauseEvent;
    
    public event Action<string>? LogMessage;
    public event Action<int, int>? ProgressUpdated; // Processed, Total Found
    public event Action? SearchFinished;

    // Statistics
    private ConcurrentBag<SearchResultItem> _foundFiles = new();
    private ConcurrentDictionary<string, int> _wordCounts = new();
    private int _filesProcessed = 0;
    private int _filesFoundForProcessing = 0;

    private readonly string[] _targetExtensions = { ".txt", ".md" };

    public void Start(string[] forbiddenWords, string destinationFolder, string ignoredFilePath = "")
    {
        _forbiddenWords = forbiddenWords.Select(w => w.Trim()).Where(w => !string.IsNullOrEmpty(w)).ToArray();
        _destinationFolder = destinationFolder;
        _ignoredFilePath = ignoredFilePath;

        _cts = new CancellationTokenSource();
        _pauseEvent = new ManualResetEventSlim(true);

        _foundFiles.Clear();
        _wordCounts.Clear();
        _filesProcessed = 0;
        _filesFoundForProcessing = 0;

        Task.Run(() => SearchTask(_cts.Token));
    }

    public void Pause()
    {
        _pauseEvent?.Reset();
        LogMessage?.Invoke("System: Paused");
    }

    public void Resume()
    {
        _pauseEvent?.Set();
        LogMessage?.Invoke("System: Resumed");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _pauseEvent?.Set(); // In case it was paused, we need to let it finish
        LogMessage?.Invoke("System: Stop requested");
    }

    private void SearchTask(CancellationToken token)
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Where(d => !d.Name.StartsWith("/System/Volumes", StringComparison.OrdinalIgnoreCase) && 
                            !d.Name.StartsWith("/private", StringComparison.OrdinalIgnoreCase) &&
                            !d.Name.StartsWith("/dev", StringComparison.OrdinalIgnoreCase))
                .ToList();
            LogMessage?.Invoke($"Found {drives.Count} ready drives.");

            ParallelOptions po = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

           
            
            Parallel.ForEach(drives, po, drive =>
            {
                TraverseAndProcess(drive.RootDirectory.FullName, token);
            });

            LogMessage?.Invoke("System: Scanning finished or stopped.");
            GenerateReport();
        }
        catch (OperationCanceledException)
        {
            LogMessage?.Invoke("System: Operation was cancelled.");
            GenerateReport();
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error: {ex.Message}");
        }
        finally
        {
            SearchFinished?.Invoke();
        }
    }

    private void TraverseAndProcess(string path, CancellationToken token)
    {
        _pauseEvent?.Wait(token);
        token.ThrowIfCancellationRequested();

        try
        {
            var dirInfo = new DirectoryInfo(path);
            
            // Prevent recursive scanning of the destination folder (which creates orig_orig_ files)
            string normalizedDest = _destinationFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(normalizedDest) && 
                (path.Equals(normalizedDest, StringComparison.OrdinalIgnoreCase) || 
                 path.StartsWith(normalizedDest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // macOS: Skip root system folders that contain firmlinks to the entire disk (/System/Volumes/Data)
            if (path.Equals("/System", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/private", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/dev", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Skip Symlinks and System folders to avoid infinite loops and access errors
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint ||
                (dirInfo.Attributes & FileAttributes.System) == FileAttributes.System)
            {
                return;
            }

            var directories = Directory.EnumerateDirectories(path);
            foreach (var dir in directories)
            {
                TraverseAndProcess(dir, token);
            }

            var files = Directory.EnumerateFiles(path);
            foreach (var file in files)
            {
                _pauseEvent?.Wait(token);
                token.ThrowIfCancellationRequested();

                var ext = Path.GetExtension(file).ToLower();
                if (_targetExtensions.Contains(ext))
                {
                    Interlocked.Increment(ref _filesFoundForProcessing);
                    ProcessFile(file, token);
                }
                
                // Show that it's alive even if it doesn't process the file
                int currentScanned = Interlocked.Increment(ref _filesProcessed);
                if (currentScanned % 2000 == 0) 
                {
                    ProgressUpdated?.Invoke(currentScanned, _filesFoundForProcessing);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (PathTooLongException) { }
        catch (Exception ex)
        {
            // Only log some to prevent log flooding
            if (new Random().Next(0, 100) < 2) 
            {
                LogMessage?.Invoke($"Warn: Could not read {path}. {ex.Message}");
            }
        }
    }

    private void ProcessFile(string filePath, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(_ignoredFilePath) && filePath.Equals(_ignoredFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return; // Ignore the dictionary file itself
        }

        try
        {
            var content = File.ReadAllText(filePath);
            bool containsForbidden = false;
            int totalReplacementsInFile = 0;
            
            var localWordCounts = new Dictionary<string, int>();
            string censoredContent = content;

            foreach (var word in _forbiddenWords)
            {
                var regex = new Regex($@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
                var matches = regex.Matches(censoredContent);

                if (matches.Count > 0)
                {
                    containsForbidden = true;
                    totalReplacementsInFile += matches.Count;
                    localWordCounts[word] = matches.Count;
                    
                    censoredContent = regex.Replace(censoredContent, "*******");
                }
            }

            if (containsForbidden)
            {
                string safeFileName = Guid.NewGuid().ToString().Substring(0, 8) + "_" + Path.GetFileName(filePath);
                string originalCopyPath = Path.Combine(_destinationFolder, "orig_" + safeFileName);
                File.Copy(filePath, originalCopyPath, true);

                string censoredCopyPath = Path.Combine(_destinationFolder, "censored_" + safeFileName);
                File.WriteAllText(censoredCopyPath, censoredContent);

                foreach (var kvp in localWordCounts)
                {
                    _wordCounts.AddOrUpdate(kvp.Key, kvp.Value, (_, count) => count + kvp.Value);
                }

                FileInfo fi = new FileInfo(filePath);
                _foundFiles.Add(new SearchResultItem
                {
                    OriginalFilePath = filePath,
                    CopiedOriginalPath = originalCopyPath,
                    CensoredFilePath = censoredCopyPath,
                    FileSizeBytes = fi.Length,
                    ReplacementsCount = totalReplacementsInFile
                });

                LogMessage?.Invoke($"Match found: {filePath} ({totalReplacementsInFile} replacements)");
            }
            
            // UI update for exact processing is moved to the file enumerator above
        }
        catch (Exception)
        {
            // Silently skip locked files
        }
    }

    private void GenerateReport()
    {
        try
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string reportPath = Path.Combine(desktopPath, "report.txt");
            using (var writer = new StreamWriter(reportPath))
            {
                writer.WriteLine("======================================");
                writer.WriteLine("      FORBIDDEN WORDS REPORT");
                writer.WriteLine("======================================");
                writer.WriteLine($"Date: {DateTime.Now}");
                writer.WriteLine($"Total files scanned (Text only): {_filesProcessed}");
                writer.WriteLine($"Total files containing forbidden words: {_foundFiles.Count}");
                
                var topWords = _wordCounts.OrderByDescending(kvp => kvp.Value).Take(10).ToList();
                writer.WriteLine("\n--- Top 10 Forbidden Words ---");
                foreach (var kvp in topWords)
                {
                    writer.WriteLine($"{kvp.Key}: {kvp.Value} times");
                }

                writer.WriteLine("\n--- Affected Files ---");
                foreach (var file in _foundFiles)
                {
                    writer.WriteLine($"Original: {file.OriginalFilePath}");
                    writer.WriteLine($"Size: {file.FileSizeBytes} bytes");
                    writer.WriteLine($"Replacements: {file.ReplacementsCount}");
                    writer.WriteLine($"Copied To: {file.CopiedOriginalPath}");
                    writer.WriteLine($"Censored To: {file.CensoredFilePath}");
                    writer.WriteLine("-");
                }
            }
            LogMessage?.Invoke($"Report generated at: {reportPath}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Failed to generate report: {ex.Message}");
        }
    }
}
