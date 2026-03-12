using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Services
{
    public class FileLoggerListener : TraceListener
    {
        private readonly string _logFilePath;
        private readonly System.Collections.Concurrent.BlockingCollection<string> _logQueue = new(10000); // Buffer up to 10k lines
        private readonly CancellationTokenSource _cts = new();

        public FileLoggerListener(string filePath)
        {
            _logFilePath = filePath;
            
            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(_logFilePath, $"\n\n--- ASYNC SESSION START: {DateTime.Now} ---\n", Encoding.UTF8);
            }
            catch { }

            // Start background worker
            Task.Run(ProcessQueueAsync);
        }

        private async Task ProcessQueueAsync()
        {
            var buffer = new StringBuilder();
            var lastFlush = DateTime.Now;

            while (!_cts.Token.IsCancellationRequested || _logQueue.Count > 0)
            {
                try
                {
                    // Try to take a message, or wait a bit for efficiency
                    if (_logQueue.TryTake(out string? message, 100))
                    {
                        buffer.Append(message);
                    }

                    // Flush condition: buffer too big OR too much time passed OR no more messages and we have something
                    if (buffer.Length > 0 && (buffer.Length > 4096 || (DateTime.Now - lastFlush).TotalSeconds > 2 || (_logQueue.Count == 0 && buffer.Length > 0)))
                    {
                        await WriteToFileAsync(buffer.ToString());
                        buffer.Clear();
                        lastFlush = DateTime.Now;
                    }
                    
                    if (_logQueue.Count == 0 && buffer.Length == 0)
                    {
                        await Task.Delay(500, _cts.Token); // Less aggressive idle wait (500ms)
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }

            // Final flush
            if (buffer.Length > 0) await WriteToFileAsync(buffer.ToString());
        }

        private async Task WriteToFileAsync(string content)
        {
            try
            {
                // Use standard I/O for simplicity but in a background task
                await File.AppendAllTextAsync(_logFilePath, content, Encoding.UTF8);

                // Simple rotation
                var info = new FileInfo(_logFilePath);
                if (info.Exists && info.Length > 15 * 1024 * 1024)
                {
                    await File.WriteAllTextAsync(_logFilePath, $"--- LOG ROTATED at {DateTime.Now} (file > 15MB) ---\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        public override void Write(string? message)
        {
            if (message == null) return;
            
            // Only queue warnings, errors, or session markers
            if (message.Contains("[WARN]") || message.Contains("[ERR ]") || message.Contains("[CRIT]") || message.Contains("SESSION START"))
            {
                _logQueue.TryAdd(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message == null) return;

            // Only queue warnings, errors, or session markers
            if (message.Contains("[WARN]") || message.Contains("[ERR ]") || message.Contains("[CRIT]") || message.Contains("SESSION START"))
            {
                _logQueue.TryAdd(message + Environment.NewLine);
            }
        }

        public override void Flush()
        {
            // Synchronously flush the remaining queue to disk
            var buffer = new StringBuilder();
            while (_logQueue.TryTake(out string? message))
            {
                buffer.Append(message);
            }
            if (buffer.Length > 0)
            {
                try
                {
                    // Use sync write for Flush to ensure it finishes during crash
                    File.AppendAllText(_logFilePath, buffer.ToString(), Encoding.UTF8);
                }
                catch { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts.Cancel();
                _logQueue.CompleteAdding();
                Flush(); // Final sync flush
            }
            base.Dispose(disposing);
        }
    }
}
