using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Xml;
using ArtiosCadFastExport;

internal static class Tests
{
    private const string Acm = "%INCM\r\n%HDR SIZE 10000 10000\r\nP1\r\nD1\r\nG1X100Y0\r\nD2\r\n";
    private static int passed;
    private static string root;
    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); passed++; }
    private static void Fails(Action action, string label)
    {
        bool failed = false; try { action(); } catch { failed = true; } Check(failed, label);
    }

    // Все проверки используют собственный каталог. Рабочие Defaults и C:\CAM не затрагиваются.
    private static int Main(string[] args)
    {
        root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            GeometryTests(); JobTests(); PublicationTests(); SetupTests(args[0]); PilotTests();
            Console.WriteLine("ALL PASSED: " + passed); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void GeometryTests()
    {
        string file = Path.Combine(root, "geometry.ACM"); File.WriteAllText(file, Acm);
        var doc = Geometry.ParseAcm(file);
        Check(doc.Entities.Count == 1 && ((Geometry.LineEntity)doc.Entities[0]).X2 == 1.0, "line scale hundredths -> mm");
        File.WriteAllText(file, "%INCM\n%HDR SIZE 10000 10000\nP1\nD1\nG2X0Y0I100J0\n");
        doc = Geometry.ParseAcm(file); Check(doc.Entities[0] is Geometry.CircleEntity && ((Geometry.CircleEntity)doc.Entities[0]).Radius == 1, "full circle");
        File.WriteAllText(file, "%INCM\n%HDR SIZE 10000 10000\nP1\nD1\nG3X100Y100I0J100\n");
        doc = Geometry.ParseAcm(file); Check(doc.Entities[0] is Geometry.ArcEntity && Math.Abs(((Geometry.ArcEntity)doc.Entities[0]).Radius - 1) < 1e-9, "quarter arc");
        File.WriteAllText(file, "%INCM\n%HDR SIZE 10000 10000\n"); Fails(() => Geometry.ParseAcm(file), "reject empty geometry");
        File.WriteAllText(file, Acm + "BROKEN INPUT\n"); Fails(() => Geometry.ParseAcm(file), "reject unknown commands");
        File.WriteAllText(file, Acm.Replace("P1", "P99")); Fails(() => Geometry.ParseAcm(file), "reject unknown tool");
        File.WriteAllText(file, Acm.Replace("%INCM", "%INCH")); Fails(() => Geometry.ParseAcm(file), "reject other units");
        File.WriteAllText(file, Acm.Replace("G1X100Y0", "G1X100X200Y0")); Fails(() => Geometry.ParseAcm(file), "reject repeated coordinate");
    }

    private static RuntimeConfig Config(string name) { return new RuntimeConfig { localRoot = Path.Combine(root, name) }; }
    private static string Ard(string name)
    { string dir = Path.Combine(root, name); Directory.CreateDirectory(dir); string file = Path.Combine(dir, "Коробка 01.ARD"); File.WriteAllText(file, "synthetic ARD fixture"); return file; }
    private static void Inputs(JobEngine engine, Job job)
    {
        string incoming = Path.Combine(engine.DirectoryFor(job.id), "Incoming"); string name = Path.GetFileNameWithoutExtension(job.ard);
        File.WriteAllText(Path.Combine(incoming, name + ".ACM"), Acm);
        if (job.profile != "plotter") File.WriteAllText(Path.Combine(incoming, name + ".pdf"), "%PDF-1.4\nsynthetic envelope only\n%%EOF\n");
        if (job.profile == "plotter-pdf-eps") File.WriteAllText(Path.Combine(incoming, name + ".eps"), "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 10 10\n%%EOF\n");
    }

    private static void JobTests()
    {
        var engine = new JobEngine(Config("jobs"));
        foreach (string profile in Setup.ProfileIds)
        {
            string ard = Ard(profile); var job = engine.Begin(ard, profile); Inputs(engine, job);
            var result = engine.Finish(job.id);
            Check(result.state == "Completed" && File.Exists(Path.ChangeExtension(ard, ".ACM.dxf")), "publish " + profile);
            Fails(() => engine.Finish(job.id), "reject duplicate finish " + profile);
            var repeat = engine.Begin(ard, profile); Inputs(engine, repeat); engine.Finish(repeat.id);
            Check(File.ReadAllText(Path.ChangeExtension(ard, ".ACM")) == Acm, "overwrite " + profile);
        }
        string missing = Ard("missing"); string target = Path.ChangeExtension(missing, ".ACM"); File.WriteAllText(target, "old result");
        var absent = engine.Begin(missing, "plotter-pdf");
        File.WriteAllText(Path.Combine(engine.DirectoryFor(absent.id), "Incoming", "Коробка 01.ACM"), Acm);
        File.WriteAllText(Path.ChangeExtension(missing, ".pdf"), "%PDF-1.4\nold\n%%EOF");
        Fails(() => engine.Finish(absent.id), "old destination PDF cannot satisfy new job");
        Check(File.ReadAllText(target) == "old result", "missing input leaves old ACM unchanged");
        var changed = engine.Begin(missing, "plotter"); Inputs(engine, changed); File.AppendAllText(missing, "changed");
        Fails(() => engine.Finish(changed.id), "reject changed ARD");
        var bad = engine.Begin(missing, "plotter-pdf"); Inputs(engine, bad);
        File.WriteAllText(Path.Combine(engine.DirectoryFor(bad.id), "Incoming", "Коробка 01.pdf"), "%PDF-1.4\ntruncated");
        Fails(() => engine.Finish(bad.id), "reject incomplete PDF");
        var invalidEps = engine.Begin(missing, "plotter-pdf-eps"); Inputs(engine, invalidEps);
        File.WriteAllText(Path.Combine(engine.DirectoryFor(invalidEps.id), "Incoming", "Коробка 01.eps"), "%PDF-1.4\n%%EOF");
        Fails(() => engine.Finish(invalidEps.id), "reject PDF renamed to EPS");
        Fails(() => engine.DirectoryFor("..\\other"), "reject path traversal job ID");
        string xml = Path.Combine(root, "bad.xml"); File.WriteAllText(xml, "<!DOCTYPE X [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><ARTIOSDBDOC>&x;</ARTIOSDBDOC>");
        Fails(() => JobEngine.ArdFromXml(xml), "reject XML external entities");
    }

    private static void PublicationTests()
    {
        string ard = Ard("rollback"); string dir = Path.GetDirectoryName(ard); string acm = "Коробка 01.ACM"; string dxf = acm + ".dxf";
        File.WriteAllText(Path.Combine(dir, acm), "old acm"); File.WriteAllText(Path.Combine(dir, dxf), "old dxf");
        string snapshot = Path.Combine(root, "snapshot"); Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, acm), "new acm"); File.WriteAllText(Path.Combine(snapshot, dxf), "new dxf");
        using (new FileStream(Path.Combine(dir, dxf), FileMode.Open, FileAccess.Read, FileShare.Read))
            Fails(() => Publisher.Publish(ard, snapshot, new[] { acm, dxf }, Guid.NewGuid().ToString("N"), Storage.Hash(ard)), "locked target interrupts publication");
        Publisher.Recover(ard);
        Check(File.ReadAllText(Path.Combine(dir, acm)) == "old acm" && File.ReadAllText(Path.Combine(dir, dxf)) == "old dxf", "rollback restores complete old set");
        Publisher.Publish(ard, snapshot, new[] { acm, dxf }, Guid.NewGuid().ToString("N"), Storage.Hash(ard));
        Check(File.ReadAllText(Path.Combine(dir, acm)) == "new acm" && File.ReadAllText(Path.Combine(dir, dxf)) == "new dxf", "publish after recovery");
    }

    private static void SetupTests(string template)
    {
        var generated = Setup.Generate(template, @"\\server\share\Fast Export", Path.Combine(root, "Incoming"));
        Check(generated.SelectNodes("/OUTPUTTEMPLATE/DTABLEITEM").Count == 12, "three groups and nine internal outputs");
        Check(!generated.OuterXml.Contains("{{") && !generated.OuterXml.Contains(@"C:\CAM"), "no unresolved tokens or shared C CAM path");
        string zipPath = Path.Combine(root, "clientdflt.zip");
        using (var file = File.Create(zipPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("clientdflt.xml").Open()))
                writer.Write("<ROOT LastSavedVersion=\"22.07\"><DFOLDER Name=\"Defaults\"><DTABLE Name=\"Outputs\"><DTABLEITEM Name=\"Unrelated\" /></DTABLE></DFOLDER></ROOT>");
            using (var writer = new StreamWriter(zip.CreateEntry("keep.txt").Open())) writer.Write("keep");
        }
        string backup = Setup.Install(zipPath, generated); Setup.Install(zipPath, generated);
        using (var file = File.OpenRead(zipPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
        using (var stream = zip.GetEntry("clientdflt.xml").Open())
        {
            var doc = new XmlDocument(); doc.Load(stream);
            Check(doc.SelectNodes("/*/DFOLDER/DTABLE/DTABLEITEM").Count == 13, "setup is idempotent and preserves other output");
            Check(zip.GetEntry("keep.txt") != null && File.Exists(backup), "setup preserves extra ZIP entry and backup");
        }
    }

    private static void PilotTests()
    {
        var config = Config("pilot"); var adapter = new PilotAdapter(config); string ard = Ard("pilot-ard");
        string xml = Path.Combine(root, "pilot.xml");
        var doc = new XmlDocument(); doc.LoadXml("<ARTIOSDBDOC><DESIGN><NAME/><PATH/></DESIGN></ARTIOSDBDOC>");
        doc.SelectSingleNode("//NAME").InnerText = Path.GetFileName(ard); doc.SelectSingleNode("//PATH").InnerText = Path.GetDirectoryName(ard); doc.Save(xml);
        using (var mock = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ArtiosCADMock.exe")) { UseShellExecute = false, CreateNoWindow = true }))
        {
            try
            {
                Directory.CreateDirectory(adapter.Incoming); File.WriteAllText(Path.Combine(adapter.Incoming, "Коробка 01.pdf"), "stale");
                xml = Path.Combine(adapter.Incoming, "input.xml"); doc.Save(xml);
                var job = adapter.Begin(xml, "plotter");
                Check(File.Exists(xml), "begin preserves XML in working directory");
                Check(!File.Exists(Path.Combine(adapter.Incoming, "Коробка 01.pdf")) && File.Exists(Path.Combine(new JobEngine(config).DirectoryFor(job.id), "PreviousInputs", "Коробка 01.pdf")), "pilot quarantines stale inputs before output");
                File.WriteAllText(Path.Combine(adapter.Incoming, "Коробка 01.ACM"), Acm);
                Check(adapter.Finish(xml, "plotter").state == "Completed", "pilot begin-output-finish");
                adapter.Begin(xml, "plotter");
                Fails(() => adapter.Begin(xml, "plotter"), "overlapping begin blocks session");
                Fails(() => adapter.Finish(xml, "plotter"), "blocked session cannot publish");
                Fails(() => adapter.Reset(), "cannot reset while Artios is running");
            }
            finally { if (!mock.HasExited) mock.Kill(); mock.WaitForExit(); }
        }
        adapter.Reset(); Check(!File.Exists(Path.Combine(config.Root, "pilot-session.json")), "reset preserves old session receipt");
    }
}
