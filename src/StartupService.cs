using System;
using System.IO;
using Microsoft.Win32;

namespace RugWindows
{
    /// <summary>Current-user logon startup. Never modifies machine-wide or unrelated startup entries.</summary>
    public sealed class StartupService
    {
        internal const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "UnderTheRugWindows";
        readonly string executablePath;
        readonly IStartupStore store;

        public StartupService(string executablePath)
            : this(executablePath, new RegistryStartupStore(Registry.CurrentUser, RunSubKey, ValueName)) { }

        // Unit tests inject an isolated in-memory store, not the user's real Run key.
        internal StartupService(string executablePath, IStartupStore startupStore)
        {
            if (startupStore == null) throw new ArgumentNullException("startupStore");
            this.executablePath = executablePath;
            store = startupStore;
        }

        public string RegisteredCommand { get { return store.Read() ?? String.Empty; } }
        public bool Enabled { get { return !String.IsNullOrWhiteSpace(RegisteredCommand); } }

        public void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                store.Remove();
                if (Enabled) throw new IOException("Windows не подтвердила отключение автозапуска Ковра.");
                return;
            }

            // Validate all command data and existence before the first registry mutation.
            string command = BuildCommand(executablePath);
            string fullPath = NormalizeExecutablePath(executablePath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Не найден файл Ковра. Сначала распакуйте программу в постоянную папку, затем включите автозапуск.", fullPath);
            if (!String.Equals(RegisteredCommand, command, StringComparison.Ordinal)) store.Write(command);
            if (!String.Equals(RegisteredCommand, command, StringComparison.Ordinal)) throw new IOException("Windows не подтвердила включение автозапуска Ковра.");
        }

        public static string BuildCommand(string executablePath)
        {
            string command = "\"" + NormalizeExecutablePath(executablePath) + "\" --startup";
            // Microsoft documents 260 characters for a Run-key command line.
            // https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys
            if (command.Length > 260) throw new ArgumentException("Путь к Ковру слишком длинный для автозапуска Windows. Переместите программу в папку с более коротким путём.", "executablePath");
            return command;
        }

        static string NormalizeExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не задан путь к программе.", "executablePath");
            foreach (char character in path)
                if (character == '"' || Char.IsControl(character)) throw new ArgumentException("Недопустимые символы в пути к программе.", "executablePath");
            if (!Path.IsPathRooted(path)) throw new ArgumentException("Для автозапуска нужен полный путь к программе.", "executablePath");

            string root = Path.GetPathRoot(path);
            bool driveAbsolute = root.Length == 3 && root[1] == ':' && IsSeparator(root[2]);
            bool uncAbsolute = path.StartsWith(@"\\", StringComparison.Ordinal) && root.Trim('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
            if (!driveAbsolute && !uncAbsolute) throw new ArgumentException("Для автозапуска нужен абсолютный путь к программе.", "executablePath");

            string fullPath = Path.GetFullPath(path);
            if (!String.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Для автозапуска выберите EXE-файл программы.", "executablePath");
            return fullPath;
        }

        static bool IsSeparator(char character) { return character == '\\' || character == '/'; }
    }

    internal interface IStartupStore
    {
        string Read();
        void Write(string command);
        void Remove();
    }

    internal sealed class RegistryStartupStore : IStartupStore
    {
        readonly RegistryKey baseKey;
        readonly string subKey, valueName;
        internal RegistryStartupStore(RegistryKey root, string path, string name)
        {
            if (root == null) throw new ArgumentNullException("root");
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(name)) throw new ArgumentException("Не задан раздел автозапуска.");
            baseKey = root; subKey = path; valueName = name;
        }

        public string Read()
        {
            using (RegistryKey key = baseKey.OpenSubKey(subKey, false))
                return key == null ? null : key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public void Write(string command)
        {
            using (RegistryKey key = baseKey.CreateSubKey(subKey))
            {
                if (key == null) throw new IOException("Windows не разрешила изменить автозапуск Ковра.");
                key.SetValue(valueName, command, RegistryValueKind.String);
                key.Flush();
            }
        }

        public void Remove()
        {
            using (RegistryKey key = baseKey.OpenSubKey(subKey, true))
            {
                if (key == null) return;
                key.DeleteValue(valueName, false);
                key.Flush();
            }
        }
    }
}
