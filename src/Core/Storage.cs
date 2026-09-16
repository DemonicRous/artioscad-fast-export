using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace ArtiosCadFastExport
{
    public static class Storage
    {
        public static T Read<T>(string path)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            return json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                Replace(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        // При отказе атомарной замены сохраняем прежний файл: опасного копирования поверх него нет.
        public static void Replace(string source, string target)
        {
            if (File.Exists(target)) File.Replace(source, target, null);
            else File.Move(source, target);
        }

        public static string Hash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static string HashText(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        public static string FullPath(string path)
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }

        public static bool SamePath(string a, string b)
        {
            return string.Equals(FullPath(a).TrimEnd('\\'), FullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static void PlainFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Недопустимое имя файла: " + name);
        }

        public static void NoReparsePoints(string path)
        {
            string current = FullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Перенаправленные каталоги/файлы не поддерживаются: " + current);
                current = Path.GetDirectoryName(current);
            }
        }

        public static FileStream Lock(string path)
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }

    public sealed class RuntimeConfig
    {
        // LOCALAPPDATA вычисляется под текущей учётной записью. EXE остаются на сетевом диске.
        public string localRoot = @"%LOCALAPPDATA%\ArtiosCADFE";
        public int maxInputMegabytes = 128;
        public int jobLifetimeMinutes = 120;

        public string Root
        {
            get
            {
                if (string.IsNullOrWhiteSpace(localRoot)) throw new InvalidDataException("localRoot не задан.");
                string root = Storage.FullPath(localRoot);
                if (root.StartsWith(@"\\") || root.IndexOf('%') >= 0 || !Path.IsPathRooted(root))
                    throw new InvalidDataException("localRoot должен указывать на локальный каталог.");
                if (new DriveInfo(Path.GetPathRoot(root)).DriveType != DriveType.Fixed)
                    throw new InvalidDataException("Временные файлы должны находиться на локальном фиксированном диске.");
                if (maxInputMegabytes < 1 || maxInputMegabytes > 1024 || jobLifetimeMinutes < 1 || jobLifetimeMinutes > 1440)
                    throw new InvalidDataException("Некорректные ограничения runtime.json.");
                Storage.NoReparsePoints(root);
                return root;
            }
        }
    }

    public static class Profiles
    {
        public static string[] Inputs(string profile)
        {
            switch (profile)
            {
                case "plotter": return new[] { ".ACM" };
                case "plotter-pdf": return new[] { ".ACM", ".pdf" };
                case "plotter-pdf-eps": return new[] { ".ACM", ".pdf", ".eps" };
                default: throw new ArgumentException("Неизвестный профиль: " + profile);
            }
        }
    }
}
