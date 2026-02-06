using System;
using System.Collections.Generic;
using System.Text;

namespace CoreSim.Utils
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// CoreSim logger. Does NOT depend on Unity.
    /// You can route it to Unity in UnityViz.
    /// </summary>
    public sealed class SimLogger
    {
        public bool Enabled { get; set; } = true;
        public LogLevel MinLevel { get; set; } = LogLevel.Info;

        private readonly List<string> _buffer = new List<string>();

        public IReadOnlyList<string> Buffer => _buffer;

        public void Clear()
        {
            _buffer.Clear();
        }

        public void Log(LogLevel level, string message)
        {
            if (!Enabled) return;
            if (level < MinLevel) return;

            string line = $"[{DateTime.UtcNow:HH:mm:ss.fff} UTC] [{level}] {message}";
            _buffer.Add(line);
        }

        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);

        /// <summary>
        /// Dumps buffered logs into one string (useful for saving to file later).
        /// </summary>
        public string DumpToString()
        {
            var sb = new StringBuilder();
            foreach (var line in _buffer)
                sb.AppendLine(line);
            return sb.ToString();
        }
    }
}