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
    public static class Logger
    {
        private static BlockingCollection<string> _logQueue = new();
        private static Task _logWriterTask;
        private static CancellationTokenSource _cancellationTokenSource = new();
        private static bool _enableFileLogging;
        private static string _logDirectory;
        private static long _maxFileSizeInMb;
        private static string _currentLogFilePath;
        private static readonly object _fileLock = new();

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

        private static void EnsureLogDirectoryExists()
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
        }

        private static void StartLogWriter()
        {
            _logWriterTask = Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
        }

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
                    break; // Task foi cancelada
                }
            }
        }

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

        private static bool IsFileSizeExceeded()
        {
            if (!File.Exists(_currentLogFilePath))
                return true;

            var fileInfo = new FileInfo(_currentLogFilePath);
            return fileInfo.Length > _maxFileSizeInMb * 1024 * 1024;
        }

        private static string GenerateNewLogFilePath()
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            return Path.Combine(_logDirectory, $"log_{timestamp}.log");
        }

        public static void Log(LogLevel level, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logMessage = $"[{timestamp}] [{level}] {message}";

            // Imprime no console com cor
            WriteToConsole(level, logMessage);

            // Adiciona à fila de logs
            _logQueue.Add(logMessage);
        }

        private static void WriteToConsole(LogLevel level, string message)
        {
            ConsoleColor originalColor = Console.ForegroundColor;

            Console.ForegroundColor = level switch
            {
                LogLevel.INFO => ConsoleColor.Cyan,
                LogLevel.WARNING => ConsoleColor.Yellow,
                LogLevel.ERROR => ConsoleColor.Red,
                LogLevel.SUCCESS => ConsoleColor.Green, // Cor para logs de sucesso
                _ => ConsoleColor.White,
            };

            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }

        public static void LogInfo(string message) => Log(LogLevel.INFO, message);
        public static void LogWarning(string message) => Log(LogLevel.WARNING, message);
        public static void LogError(string message) => Log(LogLevel.ERROR, message);
        public static void LogSuccess(string message) => Log(LogLevel.SUCCESS, message); // Novo método para sucesso

        public static void Stop()
        {
            _cancellationTokenSource.Cancel();
            _logWriterTask.Wait();
            _logQueue.Dispose();
        }

        private class WriteLogConfig
        {
            public bool Enable { get; set; }
            public string Path { get; set; }
            public long SizeMax { get; set; }
        }

        public enum LogLevel
        {
            INFO,
            WARNING,
            ERROR,
            SUCCESS // Novo nível de log para sucesso
        }
    }
}
