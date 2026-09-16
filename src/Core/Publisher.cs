using System;
using System.IO;
using System.Collections.Generic;

namespace ArtiosCadFastExport
{
    public sealed class Publication
    {
        public string ard;
        public string jobId;
        public string state;
        public string[] files;
        public List<string> originals;
    }

    public static class Publisher
    {
        private static string ControlDirectory(string ard)
        {
            return Path.Combine(Path.GetDirectoryName(Storage.FullPath(ard)), ".artioscad-fe-" + Storage.HashText(Path.GetFileName(ard).ToUpperInvariant()).Substring(0, 16));
        }

        public static void Publish(string ard, string snapshot, string[] files, string jobId, string ardHash)
        {
            string control = ControlDirectory(ard);
            Directory.CreateDirectory(control);
            Storage.NoReparsePoints(control);
            using (Storage.Lock(Path.Combine(control, "publish.lock")))
            {
                RecoverLocked(ard, control);
                if (Storage.Hash(ard) != ardHash) throw new IOException("ARD изменился до публикации.");
                string transaction = Path.Combine(control, "pending");
                if (Directory.Exists(transaction)) throw new IOException("Не удалось очистить предыдущую транзакцию: " + transaction);
                Directory.CreateDirectory(transaction);
                var record = new Publication { ard = Storage.FullPath(ard), jobId = jobId, files = files, state = "Preparing", originals = new List<string>() };
                string journal = Path.Combine(transaction, "journal.json");
                Storage.Write(journal, record);
                try
                {
                    foreach (string file in files)
                    {
                        Storage.PlainFileName(file);
                        string target = Path.Combine(Path.GetDirectoryName(ard), file);
                        Storage.NoReparsePoints(target);
                        File.Copy(Path.Combine(snapshot, file), Path.Combine(transaction, file + ".new"), false);
                        if (Storage.Hash(Path.Combine(snapshot, file)) != Storage.Hash(Path.Combine(transaction, file + ".new")))
                            throw new IOException("Не совпала контрольная сумма подготовленного файла: " + file);
                        if (File.Exists(target))
                        {
                            File.Copy(target, Path.Combine(transaction, file + ".old"), false);
                            record.originals.Add(file);
                        }
                    }
                    // Сначала сохраняем журнал и резервные копии, затем изменяем целевые файлы.
                    record.state = "Applying"; Storage.Write(journal, record);
                    foreach (string file in files)
                        Storage.Replace(Path.Combine(transaction, file + ".new"), Path.Combine(Path.GetDirectoryName(ard), file));
                    record.state = "Committed"; Storage.Write(journal, record);
                }
                catch
                {
                    // Если откат не удался, сохраняем журнал и копии. Новый вывод начнётся с восстановления.
                    RecoverLocked(ard, control);
                    throw;
                }
                Storage.Write(Path.Combine(control, "last-success.json"), record);
                DeleteTransaction(transaction, record);
            }
        }

        public static void Recover(string ard)
        {
            ard = Storage.FullPath(ard);
            string control = ControlDirectory(ard);
            if (!Directory.Exists(control)) return;
            Storage.NoReparsePoints(control);
            using (Storage.Lock(Path.Combine(control, "publish.lock"))) RecoverLocked(ard, control);
        }

        private static void RecoverLocked(string ard, string control)
        {
            string transaction = Path.Combine(control, "pending");
            if (!Directory.Exists(transaction)) return;
            Storage.NoReparsePoints(transaction);
            string journal = Path.Combine(transaction, "journal.json");
            if (!File.Exists(journal)) throw new IOException("Найдена незавершённая подготовка без журнала: " + transaction);
            var record = Storage.Read<Publication>(journal);
            if (record == null || !Storage.SamePath(record.ard, ard) || record.files == null || record.originals == null)
                throw new InvalidDataException("Повреждён журнал публикации: " + journal);
            string basename = Path.GetFileNameWithoutExtension(ard);
            var allowed = new HashSet<string>(new[] { basename + ".ACM", basename + ".ACM.dxf", basename + ".pdf", basename + ".eps" }, StringComparer.OrdinalIgnoreCase);
            foreach (string file in record.files)
            { Storage.PlainFileName(file); if (!allowed.Contains(file)) throw new InvalidDataException("Чужой файл в журнале публикации."); }
            if (record.state == "Applying")
            {
                foreach (string file in record.files)
                {
                    string target = Path.Combine(Path.GetDirectoryName(ard), file);
                    Storage.NoReparsePoints(target);
                    if (record.originals.Contains(file))
                    {
                        string restoring = Path.Combine(transaction, file + ".restore");
                        File.Copy(Path.Combine(transaction, file + ".old"), restoring, true);
                        Storage.Replace(restoring, target);
                    }
                    else if (File.Exists(target)) File.Delete(target);
                }
                record.state = "RolledBack"; Storage.Write(journal, record);
            }
            if (record.state != "Preparing" && record.state != "Committed" && record.state != "RolledBack")
                throw new InvalidDataException("Неизвестное состояние журнала. Требуется диагностика.");
            DeleteTransaction(transaction, record);
        }

        private static void DeleteTransaction(string dir, Publication record)
        {
            // Без рекурсивного удаления: только точные имена этой транзакции, журнал удаляется последним.
            foreach (string file in record.files)
                foreach (string suffix in new[] { ".old", ".new", ".restore" })
                { string path = Path.Combine(dir, file + suffix); if (File.Exists(path)) File.Delete(path); }
            File.Delete(Path.Combine(dir, "journal.json"));
            Directory.Delete(dir, false);
        }
    }
}
