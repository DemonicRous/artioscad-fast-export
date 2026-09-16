using System;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Collections.Generic;
using System.Diagnostics;

namespace ArtiosCadFastExport
{
    public static class Setup
    {
        public static readonly string[] ProfileIds = { "plotter", "plotter-pdf", "plotter-pdf-eps" };
        public static readonly string[] GroupNames = { "Plotter (ACM / DXF)", "Plotter (ACM / DXF / PDF)", "Plotter (ACM / DXF / PDF / EPS)" };

        public static XmlDocument Generate(string templatePath, string networkRoot, string incoming)
        {
            // Структура Outputs взята из прототипа 22.07. EPSF требует проверки на реальном ArtiosCAD.
            var source = LoadXml(templatePath);
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml("<OUTPUTTEMPLATE LastSavedVersion=\"22.07\" HighestSavedVersion=\"22.07\" />");
            XmlElement sample = Clone(source, doc, "SYBOX - Kongsberg XL44", "FE - ACM");
            XmlElement pdf = Clone(source, doc, "SYBOX - PDF AUTO same folder", "FE - PDF");
            XmlElement eps = Clone(source, doc, "SYBOX - PDF AUTO same folder", "FE - EPS (pilot)");
            Set(eps, "TuneFile", "TUNE.EPSF.TXT"); Set(eps, "CamTuneFileDesc", "EPSF"); Set(eps, "CamDriverName", "EPSF");
            foreach (XmlElement e in new[] { sample, pdf, eps })
            {
                Set(e, "Directory", incoming); Set(e, "ShownInOutput", "0");
                Set(e, "LaunchExeOption", "0"); Set(e, "SkipOutputDlg", "1"); Set(e, "SkipSaveAsDlg", "1");
            }
            foreach (XmlElement e in new[] { pdf, eps })
                foreach (XmlElement value in e.SelectNodes(".//DVALUE[@Name='Extension']"))
                    if (value.GetAttribute("Value") == ".pdf") value.SetAttribute("Value", e == pdf ? ".pdf" : ".eps");
            for (int i = 0; i < ProfileIds.Length; i++)
            {
                string profile = ProfileIds[i];
                string beginName = "FE - Begin " + profile;
                string finishName = "FE - Finish " + profile;
                foreach (string phase in new[] { "Begin", "Finish" })
                {
                    var output = Clone(source, doc, "SYBOX - DXF Launcher", "FE - " + phase + " " + profile);
                    Set(output, "ExeToLaunch", Path.Combine(networkRoot, phase + "-" + profile + ".exe"));
                    Set(output, "Directory", incoming); Set(output, "ShownInOutput", "0");
                    Set(output, "LaunchExeOption", "1"); Set(output, "ExeBlockOption", "1");
                    Set(output, "ExeInputParamOption", "1"); Set(output, "XMLAddDesData", "1"); Set(output, "ExeAddParamFormula", "");
                    Set(output, "SkipOutputDlg", "1");
                }
                var group = Clone(source, doc, "SYBOX - Kongsberg XL44 (Full)", GroupNames[i]);
                var names = new List<string> { beginName, "FE - ACM" };
                var data = new List<string> { "0 0 2 1", "0 1 1 1" };
                if (i >= 1) { names.Add("FE - PDF"); data.Add("0 0 2 1"); }
                if (i >= 2) { names.Add("FE - EPS (pilot)"); data.Add("0 0 2 1"); }
                names.Add(finishName); data.Add("0 0 2 1");
                Set(group, "OutputList", string.Join(";", names.ToArray()) + ";");
                Set(group, "OutputListData", string.Join(";", data.ToArray()) + ";");
                Set(group, "RunSave", "1"); Set(group, "ShownInOutput", "1"); Set(group, "SkipOutputDlg", "1");
            }
            if (doc.OuterXml.Contains("{{")) throw new InvalidDataException("Остались незаполненные токены Outputs.");
            return doc;
        }

        private static XmlElement Clone(XmlDocument source, XmlDocument target, string oldName, string newName)
        {
            var item = source.SelectSingleNode("/OUTPUTTEMPLATE/DTABLEITEM[@Name='" + oldName + "']");
            if (item == null) throw new InvalidDataException("В шаблоне не найден " + oldName);
            var copy = (XmlElement)target.ImportNode(item, true); copy.SetAttribute("Name", newName);
            target.DocumentElement.AppendChild(copy); return copy;
        }

        private static void Set(XmlElement item, string name, string value)
        {
            var nodes = item.SelectNodes(".//DVALUE[@Name='" + name + "']");
            if (nodes.Count == 0) throw new InvalidDataException("В шаблоне отсутствует параметр " + name);
            foreach (XmlElement node in nodes) node.SetAttribute("Value", value);
        }

        private static XmlDocument LoadXml(string path)
        {
            using (var stream = File.OpenRead(path)) return LoadXml(stream);
        }

        private static XmlDocument LoadXml(Stream stream)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 })) doc.Load(reader);
            return doc;
        }

        public static string Install(string zipPath, XmlDocument outputs)
        {
            foreach (var p in Process.GetProcesses())
            using (p)
                if (p.ProcessName.StartsWith("ArtiosCAD", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Закройте ArtiosCAD перед настройкой.");
            zipPath = Storage.FullPath(zipPath);
            Storage.NoReparsePoints(zipPath);
            string backup = zipPath + ".before-fe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";
            string temp = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (Storage.Lock(zipPath + ".fe.lock"))
            {
                string originalHash = Storage.Hash(zipPath);
                File.Copy(zipPath, backup, false);
                try
                {
                    using (var input = File.OpenRead(zipPath))
                    using (var source = new ZipArchive(input, ZipArchiveMode.Read))
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    using (var target = new ZipArchive(output, ZipArchiveMode.Create))
                    {
                        var entry = source.GetEntry("clientdflt.xml");
                        if (entry == null) throw new InvalidDataException("В ZIP отсутствует clientdflt.xml.");
                        XmlDocument doc;
                        using (var stream = entry.Open()) doc = LoadXml(stream);
                        string version = doc.DocumentElement.GetAttribute("LastSavedVersion");
                        if (version != "22.07") throw new InvalidDataException("Поддерживаются Defaults только версии 22.07, получено: " + version);
                        var defaults = Child(doc.DocumentElement, "DFOLDER", "Defaults");
                        var table = Child(defaults, "DTABLE", "Outputs");
                        // Это установка и обновление: отсутствующие Defaults/Outputs создаются выше.
                        // Самих устройств у пользователя может не быть — берём их целиком из комплекта.
                        // Удаляем только одноимённые записи, чтобы повторная установка не создавала дублей.
                        foreach (XmlElement newItem in outputs.DocumentElement.ChildNodes)
                        {
                            var remove = new List<XmlNode>();
                            foreach (XmlElement existing in table.SelectNodes("DTABLEITEM"))
                                if (existing.GetAttribute("Name") == newItem.GetAttribute("Name")) remove.Add(existing);
                            foreach (var existing in remove) table.RemoveChild(existing);
                            table.AppendChild(doc.ImportNode(newItem, true));
                        }
                        foreach (var old in source.Entries)
                        {
                            if (old.FullName == "clientdflt.xml") continue;
                            using (var from = old.Open())
                            using (var to = target.CreateEntry(old.FullName).Open()) from.CopyTo(to);
                        }
                        using (var stream = target.CreateEntry("clientdflt.xml").Open()) doc.Save(stream);
                    }
                    // Повторно открываем временный ZIP и сравниваем все добавленные Outputs до замены Defaults.
                    using (var stream = File.OpenRead(temp))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    using (var xml = zip.GetEntry("clientdflt.xml").Open())
                    {
                        var doc = LoadXml(xml);
                        foreach (XmlElement wanted in outputs.DocumentElement.ChildNodes)
                        {
                            var actual = doc.SelectSingleNode("/*/DFOLDER[@Name='Defaults']/DTABLE[@Name='Outputs']/DTABLEITEM[@Name='" + wanted.GetAttribute("Name") + "']");
                            if (actual == null || actual.OuterXml != wanted.OuterXml) throw new InvalidDataException("Проверка Outputs после записи не пройдена.");
                        }
                    }
                    if (Storage.Hash(zipPath) != originalHash) throw new IOException("Defaults изменились во время настройки. Повторите после закрытия ArtiosCAD.");
                    Storage.Replace(temp, zipPath);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            return backup;
        }

        private static XmlElement Child(XmlElement parent, string tag, string name)
        {
            foreach (XmlElement e in parent.SelectNodes(tag)) if (e.GetAttribute("Name") == name) return e;
            var child = parent.OwnerDocument.CreateElement(tag); child.SetAttribute("Name", name); parent.AppendChild(child); return child;
        }
    }
}
