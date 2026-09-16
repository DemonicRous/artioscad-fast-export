using System;
using System.IO;
using System.Windows.Forms;
using ArtiosCadFastExport;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        RuntimeConfig config = null;
        try
        {
            Application.EnableVisualStyles();
            config = Storage.Read<RuntimeConfig>(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime.json"));
            if (args.Length == 0) throw new ArgumentException("Запустите Setup.exe из сетевой папки для настройки пилота.");
            var engine = new JobEngine(config);
            switch (args[0])
            {
                case "pilot-begin":
                    Require(args, 3); new PilotAdapter(config).Begin(args[2], args[1]); break;
                case "pilot-finish":
                    Require(args, 3); new PilotAdapter(config).Finish(args[2], args[1]); break;
                case "reset":
                    new PilotAdapter(config).Reset(); MessageBox.Show("Состояние пилота сброшено. Можно открыть ArtiosCAD и повторить вывод.", "ArtiosCAD Fast Export"); break;
                case "setup":
                    Require(args, 2); Configure(config, args[1]); break;
                case "diagnostics":
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + config.Root + "\""); break;
                case "recover":
                    Require(args, 2); Publisher.Recover(args[1]); break;
                case "begin":
                    Require(args, 4);
                    var job = engine.Begin(args[2], args[1]);
                    Storage.Write(args[3], job); break;
                case "finish":
                    Require(args, 2); engine.Finish(args[1]); break;
                default: throw new ArgumentException("Неизвестная команда: " + args[0]);
            }
            return 0;
        }
        catch (Exception ex)
        {
            string log = "";
            try
            {
                string root = config == null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtiosCADFE") : config.Root;
                string logs = Path.Combine(root, "Logs"); Directory.CreateDirectory(logs);
                log = Path.Combine(logs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".error.log");
                File.WriteAllText(log, ex.ToString());
            }
            catch { }
            MessageBox.Show(ex.Message + "\r\n\r\nЛог: " + log, "ArtiosCAD Fast Export — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Require(string[] args, int length)
    { if (args.Length != length) throw new ArgumentException("Неверное количество аргументов команды " + args[0]); }

    private static void Configure(RuntimeConfig config, string root)
    {
        if (!root.StartsWith(@"\\")) throw new ArgumentException("Запустите Setup.exe по UNC-пути из сетевой папки (\\server\\share). Локальная установка не поддерживается.");
        string incoming = Path.Combine(config.Root, "Incoming"); Directory.CreateDirectory(incoming);
        var outputs = Setup.Generate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prototype-outputs.xml"), root, incoming);
        outputs.Save(Path.Combine(config.Root, "outputs-preview.xml"));
        if (MessageBox.Show("Пилот для ArtiosCAD 22.07: три группы Plotter.\r\n\r\nИспользуйте копии ARD и один экземпляр ArtiosCAD. EPS-драйвер и последовательность Outputs требуют проверки.\r\nНастройщик сохранит backup и изменит только одноимённые Outputs.\r\n\r\nСейчас выберите clientdflt.zip нужного пользователя. ArtiosCAD должен быть закрыт. Продолжить?", "ArtiosCAD Fast Export — настройка пилота", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        using (var dialog = new OpenFileDialog { Title = "Выберите ClientLib\\clientdflt.zip вашего ArtiosCAD 22.07", Filter = "User Defaults|clientdflt.zip", CheckFileExists = true })
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;
            string backup = Setup.Install(dialog.FileName, outputs);
            File.WriteAllText(Path.Combine(config.Root, "last-setup.txt"), "Defaults: " + dialog.FileName + "\r\nBackup: " + backup + "\r\nIncoming: " + incoming);
            MessageBox.Show("Установлены три группы Plotter.\r\n\r\nBackup:\r\n" + backup + "\r\n\r\nПервый тест: Plotter (ACM / DXF).\r\nИнструкция: PILOT_RU.md в сетевой папке.", "ArtiosCAD Fast Export");
        }
    }
}
