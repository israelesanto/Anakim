using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anakim.Infrastructure
{
    // Static logger class for console and file logging with asynchronous file writing
    public static class Logger
    {
        // Queue to store log messages in memory (thread-safe)
        private static BlockingCollection<string> _logQueue = new();

        // Background task for writing logs to file
        private static Task _logWriterTask;

        // Token to control cancellation of background logging
        private static CancellationTokenSource _cancellationTokenSource = new();

        // File logging settings
        private static bool _enableFileLogging;
        private static string _logDirectory;
        private static long _maxFileSizeInMb;
        private static string _currentLogFilePath;

        // Lock object for file access to prevent race conditions
        private static readonly object _fileLock = new();

        // Initializes logger from app configuration (e.g., appsettings.json)
        public static void Initialize(IConfiguration configuration)
        {
            var logConfig = configuration.GetSection("WriteLog").Get<WriteLogConfig>();

            _enableFileLogging = logConfig.Enable;
            _logDirectory = logConfig.Path;
            _maxFileSizeInMb = logConfig.SizeMax;

            if (_enableFileLogging)
            {
                EnsureLogDirectoryExists();
                StartLogWriter();
            }
        }

        // Ensures that the logging directory exists on disk
        private static void EnsureLogDirectoryExists()
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
        }

        // Starts the background task to handle queued log entries
        private static void StartLogWriter()
        {
            _logWriterTask = Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
        }

        // Continuously processes log queue and writes to file asynchronously
        private static async Task ProcessLogQueue(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_logQueue.TryTake(out var logMessage, Timeout.Infinite, token))
                    {
                        await WriteToFileAsync(logMessage);
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Task has been cancelled
                }
            }
        }

        // Writes a single message to the current log file
        private static async Task WriteToFileAsync(string message)
        {
            lock (_fileLock)
            {
                if (_currentLogFilePath == null || IsFileSizeExceeded())
                {
                    _currentLogFilePath = GenerateNewLogFilePath();
                }
            }

            await File.AppendAllTextAsync(_currentLogFilePath, message + Environment.NewLine);
        }

        // Checks if the current log file exceeded the maximum allowed size
        private static bool IsFileSizeExceeded()
        {
            if (!File.Exists(_currentLogFilePath))
                return true;

            var fileInfo = new FileInfo(_currentLogFilePath);
            return fileInfo.Length > _maxFileSizeInMb * 1024 * 1024;
        }

        // Generates a new file name with timestamp
        private static string GenerateNewLogFilePath()
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            return Path.Combine(_logDirectory, $"log_{timestamp}.log");
        }

        // Public method to log a message with the specified level
        public static void Log(LogLevel level, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logMessage = $"[{timestamp}] [{level}] {message}";

            WriteToConsole(level, logMessage);  // Print to console
            _logQueue.Add(logMessage);         // Enqueue for file logging
        }

        // Prints log message to console with color based on level
        private static void WriteToConsole(LogLevel level, string message)
        {
            ConsoleColor originalColor = Console.ForegroundColor;

            Console.ForegroundColor = level switch
            {
                LogLevel.INFO => ConsoleColor.Cyan,
                LogLevel.WARNING => ConsoleColor.Yellow,
                LogLevel.ERROR => ConsoleColor.Red,
                LogLevel.SUCCESS => ConsoleColor.Green,
                _ => ConsoleColor.White,
            };

            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }

        // Helper methods for logging by level
        public static void LogInfo(string message) => Log(LogLevel.INFO, message);
        public static void LogWarning(string message) => Log(LogLevel.WARNING, message);
        public static void LogError(string message) => Log(LogLevel.ERROR, message);
        public static void LogSuccess(string message) => Log(LogLevel.SUCCESS, message);

        // Stops background task and cleans up resources
        public static void Stop()
        {
            _cancellationTokenSource.Cancel();
            _logWriterTask.Wait();
            _logQueue.Dispose();
        }

        // Internal class to map logging settings from configuration
        private class WriteLogConfig
        {
            public bool Enable { get; set; }
            public string Path { get; set; }
            public long SizeMax { get; set; }
        }

        // Enumeration of supported log levels
        public enum LogLevel
        {
            INFO,
            WARNING,
            ERROR,
            SUCCESS
        }
    }
}
