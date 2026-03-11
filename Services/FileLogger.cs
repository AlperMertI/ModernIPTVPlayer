using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ModernIPTVPlayer.Services
{
    public class FileLoggerListener : TraceListener
    {
        private readonly string _logFilePath;
        private readonly object _lock = new object();
        private readonly StringBuilder _buffer = new StringBuilder();
        private DateTime _lastFlush = DateTime.MinValue;

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

                // Start fresh log on every session as requested
                File.WriteAllText(_logFilePath, $"--- SESSION START: {DateTime.Now} ---\n", Encoding.UTF8);
            }
            catch { }
        }

        public override void Write(string message)
        {
            lock (_lock)
            {
                _buffer.Append(message);
                ConditionalFlush();
            }
        }

        public override void WriteLine(string message)
        {
            lock (_lock)
            {
                _buffer.AppendLine(message);
                ConditionalFlush(true);
            }
        }

        private void ConditionalFlush(bool force = false)
        {
            if (force || _buffer.Length > 1024 || (DateTime.Now - _lastFlush).TotalSeconds > 5)
            {
                Flush();
            }
        }

        public override void Flush()
        {
            lock (_lock)
            {
                if (_buffer.Length == 0) return;

                try
                {
                    File.AppendAllText(_logFilePath, _buffer.ToString(), Encoding.UTF8);
                    _buffer.Clear();
                    _lastFlush = DateTime.Now;
                    
                    // Basic Rotation: If file > 5MB, clear it (keep only recent)
                    var info = new FileInfo(_logFilePath);
                    if (info.Exists && info.Length > 5 * 1024 * 1024)
                    {
                        File.WriteAllText(_logFilePath, "--- LOG ROTATED (file > 5MB) ---\n", Encoding.UTF8);
                    }
                }
                catch { }
            }
        }
    }
}
