using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Xml;

namespace ArtiosCadFastExport
{
    public sealed class Job
    {
        public string id;
        public string ard;
        public string ardHash;
        public string profile;
        public string createdUtc;
        public string state;
        public string error;
        public Dictionary<string, string> inputHashes;
    }

    public sealed class JobEngine
    {
        private readonly RuntimeConfig config;
        public JobEngine(RuntimeConfig config) { this.config = config; Directory.CreateDirectory(Path.Combine(config.Root, "Jobs")); }

        public string DirectoryFor(string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) throw new ArgumentException("Некорректный ID задания.");
            string dir = Path.Combine(config.Root, "Jobs", id);
            Storage.NoReparsePoints(dir);
            return dir;
        }

        public Job Begin(string ard, string profile)
        {
            // GUID исключает совпадение каталогов у одноимённых чертежей; хеш фиксирует версию ARD.
            Profiles.Inputs(profile);
            ard = Storage.FullPath(ard);
            if (!File.Exists(ard) || !string.Equals(Path.GetExtension(ard), ".ard", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Сначала сохраните исходный ARD.", ard);
            Storage.PlainFileName(Path.GetFileName(ard));
            var job = new Job { id = Guid.NewGuid().ToString("N"), ard = ard, ardHash = Storage.Hash(ard), profile = profile,
                createdUtc = DateTime.UtcNow.ToString("o"), state = "AwaitingInputs" };
            string dir = DirectoryFor(job.id);
            Directory.CreateDirectory(Path.Combine(dir, "Incoming"));
            Storage.Write(Path.Combine(dir, "job.json"), job);
            Log(job, "Created");
            return job;
        }

        public static string ArdFromXml(string xmlPath)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(xmlPath, settings)) doc.Load(reader);
            var name = doc.SelectSingleNode("/ARTIOSDBDOC/DESIGN/NAME");
            var path = doc.SelectSingleNode("/ARTIOSDBDOC/DESIGN/PATH");
            if (name == null || path == null) throw new InvalidDataException("В XML отсутствует NAME/PATH текущего ARD.");
            Storage.PlainFileName(name.InnerText.Trim());
            if (!Path.IsPathRooted(path.InnerText.Trim())) throw new InvalidDataException("Путь ARD должен быть абсолютным.");
            return Path.Combine(path.InnerText.Trim(), name.InnerText.Trim());
        }

        public Job Finish(string id)
        {
            string dir = DirectoryFor(id);
            using (Storage.Lock(Path.Combine(dir, "job.lock")))
            {
                var job = Storage.Read<Job>(Path.Combine(dir, "job.json"));
                if (job == null || job.id != id || job.state != "AwaitingInputs")
                    throw new InvalidOperationException("Задание уже обработано или требует нового запуска.");
                try
                {
                    if (DateTime.UtcNow - DateTime.Parse(job.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind) > TimeSpan.FromMinutes(config.jobLifetimeMinutes))
                        throw new InvalidDataException("Истекло время задания. Выполните новый вывод.");
                    if (Storage.Hash(job.ard) != job.ardHash) throw new InvalidDataException("ARD изменён после начала вывода.");
                    string name = Path.GetFileNameWithoutExtension(job.ard);
                    Storage.PlainFileName(name);
                    string snapshot = Path.Combine(dir, "Snapshot");
                    Directory.CreateDirectory(snapshot);
                    job.inputHashes = new Dictionary<string, string>();
                    var files = new List<string>();
                    foreach (string extension in Profiles.Inputs(job.profile))
                    {
                        // Читаем под эксклюзивной блокировкой. Все дальнейшие операции идут со снимком.
                        string file = name + extension;
                        string source = Path.Combine(dir, "Incoming", file);
                        string dest = Path.Combine(snapshot, file);
                        Storage.NoReparsePoints(source);
                        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            if (input.Length == 0 || input.Length > (long)config.maxInputMegabytes * 1024 * 1024)
                                throw new InvalidDataException("Пустой или слишком большой вход: " + file);
                            using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            { input.CopyTo(output); output.Flush(true); }
                        }
                        ValidateEnvelope(dest, extension);
                        job.inputHashes[file] = Storage.Hash(dest);
                        files.Add(file);
                    }
                    var geometry = Geometry.ParseAcm(Path.Combine(snapshot, name + ".ACM"));
                    string dxfName = name + ".ACM.dxf";
                    File.WriteAllText(Path.Combine(snapshot, dxfName), Geometry.BuildDxf(geometry), Encoding.ASCII);
                    files.Add(dxfName);
                    job.state = "Publishing";
                    Storage.Write(Path.Combine(dir, "job.json"), job);
                    Publisher.Publish(job.ard, snapshot, files.ToArray(), job.id, job.ardHash);
                    job.state = "Completed";
                    Storage.Write(Path.Combine(dir, "job.json"), job);
                    Log(job, "Completed");
                    // Удаляем только известные файлы завершённого задания; манифест и XML сохраняются.
                    foreach (string file in files)
                    {
                        Cleanup(Path.Combine(snapshot, file));
                        Cleanup(Path.Combine(dir, "Incoming", file));
                    }
                    return job;
                }
                catch (Exception ex)
                {
                    job.state = "Failed"; job.error = ex.Message;
                    try { Storage.Write(Path.Combine(dir, "job.json"), job); Log(job, "Failed: " + ex.Message); } catch { }
                    throw;
                }
            }
        }

        private static void ValidateEnvelope(string path, string extension)
        {
            if (extension == ".ACM") return;
            using (var stream = File.OpenRead(path))
            {
                byte[] prefix = new byte[64]; int length = stream.Read(prefix, 0, prefix.Length);
                string text = Encoding.ASCII.GetString(prefix, 0, length);
                if (extension == ".pdf")
                {
                    if (!text.StartsWith("%PDF-")) throw new InvalidDataException("Файл не имеет заголовка PDF.");
                    stream.Position = Math.Max(0, stream.Length - 2048);
                    byte[] tail = new byte[2048]; length = stream.Read(tail, 0, tail.Length);
                    if (!Encoding.ASCII.GetString(tail, 0, length).Contains("%%EOF")) throw new InvalidDataException("PDF не завершён.");
                }
                if (extension == ".eps" && (!text.StartsWith("%!PS-Adobe-") || !text.Contains("EPSF-")))
                    throw new InvalidDataException("Ожидался текстовый EPS с заголовком EPSF. Другие варианты пока не поддерживаются.");
            }
        }

        private static void Cleanup(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* Очистка не отменяет публикацию. Остатки изолированы уникальным ID задания. */ } }

        private void Log(Job job, string message)
        {
            string logs = Path.Combine(config.Root, "Logs"); Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, job.id + ".log"), DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
        }
    }
}
