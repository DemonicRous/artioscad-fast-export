using System;
using System.IO;
using System.Diagnostics;

namespace ArtiosCadFastExport
{
    public sealed class PilotSession
    {
        public string jobId;
        public string ard;
        public string profile;
        public string state;
        public int artiosPid;
        public string artiosStartUtc;
        public string runtimeDirectory;
    }

    // Пилотный адаптер: три профиля используют один входной каталог с записью состояния сессии.
    // Вывод допускается последовательно из одного ArtiosCAD. Параллельный режим не поддерживается.
    public sealed class PilotAdapter
    {
        private readonly RuntimeConfig config;
        private readonly JobEngine engine;
        public PilotAdapter(RuntimeConfig config) { this.config = config; engine = new JobEngine(config); }
        private string SessionPath { get { return Path.Combine(config.Root, "pilot-session.json"); } }
        public string Incoming { get { return Path.Combine(config.Root, "Incoming"); } }

        private static Process SingleArtios()
        {
            int session = Process.GetCurrentProcess().SessionId;
            Process match = null;
            foreach (var process in Process.GetProcesses())
            {
                bool artios;
                try { artios = process.SessionId == session && process.ProcessName.StartsWith("ArtiosCAD", StringComparison.OrdinalIgnoreCase); }
                catch { process.Dispose(); continue; }
                if (!artios) { process.Dispose(); continue; }
                if (match != null) { match.Dispose(); process.Dispose(); throw new InvalidOperationException("Пилот поддерживает только один процесс ArtiosCAD. Закройте лишние экземпляры."); }
                match = process;
            }
            if (match == null) throw new InvalidOperationException("Не найден процесс ArtiosCAD в текущем сеансе.");
            return match;
        }

        public Job Begin(string xml, string profile)
        {
            Directory.CreateDirectory(Incoming);
            using (Storage.Lock(Path.Combine(config.Root, "pilot.lock")))
            {
                if (File.Exists(SessionPath))
                {
                    var previous = Storage.Read<PilotSession>(SessionPath);
                    if (previous.state != "Completed")
                    {
                        previous.state = "Blocked"; Storage.Write(SessionPath, previous);
                        throw new InvalidOperationException("Предыдущий вывод не завершён. Закройте ArtiosCAD и запустите Reset Pilot.cmd. Входы сохранены для диагностики.");
                    }
                }
                // Блокируем сессию до подготовки: ошибка Begin не должна разрешать последующий Finish.
                var session = new PilotSession { state = "Blocked" };
                Storage.Write(SessionPath, session);
                using (var artios = SingleArtios())
                {
                    var job = engine.Begin(JobEngine.ArdFromXml(xml), profile);
                    session.jobId = job.id; session.ard = job.ard; session.profile = profile;
                    session.artiosPid = artios.Id; session.artiosStartUtc = artios.StartTime.ToUniversalTime().ToString("o");
                    session.runtimeDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    string quarantine = Path.Combine(engine.DirectoryFor(job.id), "PreviousInputs");
                    Directory.CreateDirectory(quarantine);
                    // Убираем предыдущие входы в карантин. Старый C:\CAM вообще не затрагивается.
                    foreach (string file in Directory.GetFiles(Incoming))
                    {
                        // XML запуска может находиться в том же рабочем каталоге; его перемещать нельзя.
                        string extension = Path.GetExtension(file).ToLowerInvariant();
                        if (extension != ".acm" && extension != ".pdf" && extension != ".eps") continue;
                        Storage.NoReparsePoints(file);
                        File.Move(file, Path.Combine(quarantine, Path.GetFileName(file)));
                    }
                    File.Copy(xml, Path.Combine(engine.DirectoryFor(job.id), "begin.xml"));
                    session.state = "Active"; Storage.Write(SessionPath, session);
                    return job;
                }
            }
        }

        public Job Finish(string xml, string profile)
        {
            using (Storage.Lock(Path.Combine(config.Root, "pilot.lock")))
            {
                var session = Storage.Read<PilotSession>(SessionPath);
                try
                {
                    if (session.state != "Active" || session.profile != profile || !Storage.SamePath(session.ard, JobEngine.ArdFromXml(xml)))
                        throw new InvalidOperationException("Нет подходящего подготовленного задания. Результаты не опубликованы.");
                    if (!Storage.SamePath(session.runtimeDirectory, AppDomain.CurrentDomain.BaseDirectory))
                        throw new InvalidOperationException("Версия приложения изменилась внутри задания.");
                    using (var artios = SingleArtios())
                        if (artios.Id != session.artiosPid || artios.StartTime.ToUniversalTime().ToString("o") != session.artiosStartUtc)
                            throw new InvalidOperationException("Экземпляр ArtiosCAD изменился внутри задания.");
                    string dir = engine.DirectoryFor(session.jobId);
                    File.Copy(xml, Path.Combine(dir, "finish.xml"));
                    string name = Path.GetFileNameWithoutExtension(session.ard);
                    foreach (string ext in Profiles.Inputs(profile))
                    {
                        string source = Path.Combine(Incoming, name + ext);
                        Storage.NoReparsePoints(source);
                        // Берём точные ожидаемые имена из очищенного слота, далее ядро создаёт снимок.
                        File.Move(source, Path.Combine(dir, "Incoming", name + ext));
                    }
                    var job = engine.Finish(session.jobId);
                    session.state = "Completed"; Storage.Write(SessionPath, session);
                    return job;
                }
                catch { session.state = "Blocked"; Storage.Write(SessionPath, session); throw; }
            }
        }

        public void Reset()
        {
            foreach (var p in Process.GetProcesses())
            using (p)
                if (p.SessionId == Process.GetCurrentProcess().SessionId && p.ProcessName.StartsWith("ArtiosCAD", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Для сброса сначала закройте ArtiosCAD.");
            using (Storage.Lock(Path.Combine(config.Root, "pilot.lock")))
            {
                if (File.Exists(SessionPath))
                    File.Move(SessionPath, Path.Combine(config.Root, "pilot-session." + Guid.NewGuid().ToString("N") + ".json"));
            }
        }
    }
}
