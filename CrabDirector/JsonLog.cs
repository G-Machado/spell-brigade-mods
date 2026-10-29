using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>One JSON object per line, raw quantities only; a fresh file per run.</summary>
    internal static class JsonLog
    {
        private static StreamWriter _w;
        public static string Path { get; private set; }
        public static int Lines { get; private set; }

        public static void Open(string dir)
        {
            Close();
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            _w = new StreamWriter(Path, append: false) { AutoFlush = true };
            Lines = 0;
        }

        public static void Close()
        {
            _w?.Dispose();
            _w = null;
        }

        public static void Write(Dictionary<string, object> line)
        {
            if (_w == null) return;
            _w.WriteLine(JsonSerializer.Serialize(line));
            Lines++;
        }
    }
}
