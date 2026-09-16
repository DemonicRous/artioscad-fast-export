using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Text; using System.Text.RegularExpressions;
namespace ArtiosCadFastExport { public static class Geometry {
private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public sealed class AcmDocument
    {
        public double Width;
        public double Height;
        public readonly List<Entity> Entities =
            new List<Entity>();
    }

    public abstract class Entity
    {
        public int Color;
    }

    public sealed class LineEntity : Entity
    {
        public double X1;
        public double Y1;
        public double X2;
        public double Y2;
    }

    public sealed class ArcEntity : Entity
    {
        public double CX;
        public double CY;
        public double Radius;
        public double StartAngle;
        public double EndAngle;
    }

    public sealed class CircleEntity : Entity
    {
        public double CX;
        public double CY;
        public double Radius;
    }

    public static AcmDocument ParseAcm(string path)
    {
        string[] lines = File.ReadAllLines(path);

        AcmDocument doc = new AcmDocument();

        // Единицы исходного прототипа:
        // %INCM и координаты 49900 -> 499.00 мм.
        // При другом режиме единиц останавливаем вывод,
        // чем тихо создать DXF в неверном масштабе.
        bool metricHundredths = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (string.Equals(
                line,
                "%INCM",
                StringComparison.OrdinalIgnoreCase))
            {
                metricHundredths = true;
            }

            if (line.StartsWith(
                "%HDR SIZE ",
                StringComparison.OrdinalIgnoreCase))
            {
                Match sm = Regex.Match(
                    line,
                    @"^%HDR SIZE\s+([+-]?\d+)\s+([+-]?\d+)$",
                    RegexOptions.IgnoreCase
                );

                if (sm.Success)
                {
                    doc.Width =
                        ParseDouble(sm.Groups[1].Value) / 100.0;

                    doc.Height =
                        ParseDouble(sm.Groups[2].Value) / 100.0;
                }
            }
        }

        if (!metricHundredths)
            throw new Exception(
                "Этот конвертер пока поддерживает только ACM " +
                "с режимом %INCM (метрический Kongsberg ACM)."
            );

        if (doc.Width <= 0.0 || doc.Height <= 0.0)
            throw new Exception(
                "В ACM не найден корректный %HDR SIZE."
            );

        double x = 0.0;
        double y = 0.0;

        bool penDown = false;
        int tool = -1;
        int motion = 1;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (line.Length == 0)
                continue;

            if (line[0] == '%')
                continue;

            // M59X0Y0 и прочие M-команды в этом формате
            // не рисуют DXF-геометрию.
            if (line[0] == 'M')
                continue;

            if (string.Equals(
                line,
                "D1",
                StringComparison.OrdinalIgnoreCase))
            {
                penDown = true;
                continue;
            }

            if (string.Equals(
                line,
                "D2",
                StringComparison.OrdinalIgnoreCase))
            {
                penDown = false;
                continue;
            }

            Match pm = Regex.Match(
                line,
                @"^P(\d+)$",
                RegexOptions.IgnoreCase
            );

            if (pm.Success)
            {
                tool = int.Parse(
                    pm.Groups[1].Value,
                    Inv
                );

                continue;
            }

            Dictionary<char, double> words =
                ParseWords(line);

            if (words.Count == 0)
                continue;

            double gv;

            if (words.TryGetValue('G', out gv))
            {
                if (gv != Math.Truncate(gv) || gv < 0 || gv > 3)
                    throw new InvalidDataException("Неподдерживаемый режим движения ACM: " + line);
                motion = (int)gv;
            }

            double rawX;
            double rawY;

            double nx = words.TryGetValue('X', out rawX)
                ? rawX / 100.0
                : x;

            double ny = words.TryGetValue('Y', out rawY)
                ? rawY / 100.0
                : y;

            bool moved =
                Math.Abs(nx - x) > 1e-12 ||
                Math.Abs(ny - y) > 1e-12;

            bool arcMotion =
                motion == 2 ||
                motion == 3;

            double rawI;
            double rawJ;

            double i = words.TryGetValue('I', out rawI)
                ? rawI / 100.0
                : 0.0;

            double j = words.TryGetValue('J', out rawJ)
                ? rawJ / 100.0
                : 0.0;

            // В ACM полный круг часто кодируется G2/G3 так, что
            // конечная точка совпадает с начальной, а центр задаётся I/J.
            // В предыдущей версии такие команды терялись, потому что
            // moved == false и объект вообще не создавался.
            bool fullCircle =
                arcMotion &&
                !moved &&
                (
                    Math.Abs(i) > 1e-12 ||
                    Math.Abs(j) > 1e-12
                );

            if (penDown && (moved || fullCircle))
            {
                int color = ToolToDxfColor(tool);

                if (motion == 1 || motion == 0)
                {
                    // Для линейного движения одинаковые start/end
                    // не создают геометрию.
                    if (moved)
                    {
                        LineEntity e = new LineEntity();

                        e.Color = color;
                        e.X1 = x;
                        e.Y1 = y;
                        e.X2 = nx;
                        e.Y2 = ny;

                        doc.Entities.Add(e);
                    }
                }
                else if (arcMotion)
                {
                    if (fullCircle)
                    {
                        double radius =
                            Math.Sqrt(i * i + j * j);

                        if (radius <= 1e-12)
                            throw new Exception(
                                "Некорректный полный круг ACM: " +
                                "радиус равен нулю."
                            );

                        CircleEntity e = new CircleEntity();

                        e.Color = color;
                        e.CX = x + i;
                        e.CY = y + j;
                        e.Radius = radius;

                        doc.Entities.Add(e);
                    }
                    else
                    {
                        ArcEntity e = BuildArc(
                            x,
                            y,
                            nx,
                            ny,
                            i,
                            j,
                            motion == 3,
                            color
                        );

                        doc.Entities.Add(e);
                    }
                }
                else
                {
                    throw new Exception(
                        "Неподдерживаемая G-команда при рисовании: G" +
                        motion.ToString(Inv)
                    );
                }
            }

            x = nx;
            y = ny;
        }

        if (doc.Entities.Count == 0)
            throw new InvalidDataException("ACM не содержит геометрии. Вывод остановлен.");
        return doc;
    }

    private static Dictionary<char, double> ParseWords(
        string line)
    {
        Dictionary<char, double> result =
            new Dictionary<char, double>();

        MatchCollection matches = Regex.Matches(
            line,
            @"([A-Za-z])([+-]?\d+(?:\.\d+)?)"
        );

        string remainder = Regex.Replace(line, @"([A-Za-z])([+-]?\d+(?:\.\d+)?)", "").Trim();
        if (remainder.Length != 0 || matches.Count == 0)
            throw new InvalidDataException("Неподдерживаемая строка ACM: " + line);

        foreach (Match m in matches)
        {
            char key =
                char.ToUpperInvariant(
                    m.Groups[1].Value[0]
                );

            double value =
                ParseDouble(m.Groups[2].Value);

            if ("GXYIJ".IndexOf(key) < 0 || result.ContainsKey(key) || double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException("Неподдерживаемое или повторное слово ACM: " + line);
            result[key] = value;
        }

        return result;
    }

    private static int ToolToDxfColor(int tool)
    {
        // Сопоставление перенесено из прототипа; новые инструменты требуют сверки с эталонным DXF.
        //
        // P1  -> DXF color 2
        // P11 -> DXF color 2
        // P2  -> DXF color 1
        if (tool == 1 || tool == 11)
            return 2;

        if (tool == 2)
            return 1;

        // Не создаём потенциально неправильный файл молча.
        throw new Exception(
            "В ACM встретился неизвестный инструмент P" +
            tool.ToString(Inv) +
            ".\r\n\r\n" +
            "Нужен пример ручного DXF для такого ACM, " +
            "чтобы добавить корректное сопоставление."
        );
    }

    private static ArcEntity BuildArc(
        double sx,
        double sy,
        double ex,
        double ey,
        double i,
        double j,
        bool counterClockwise,
        int color)
    {
        double qx = ex - sx;
        double qy = ey - sy;

        double dot = qx * i + qy * j;

        if (Math.Abs(dot) < 1e-14)
            throw new Exception(
                "Некорректная дуга ACM: невозможно определить центр."
            );

        // Kongsberg задаёт I/J от начальной точки.
        // Коррекция центра вдоль I/J перенесена из прототипа.
        // Её совпадение с ArtiosCAD на производственных данных ещё предстоит проверить.
        double lambda =
            (qx * qx + qy * qy) /
            (2.0 * dot);

        double cx = sx + lambda * i;
        double cy = sy + lambda * j;

        double r =
            Math.Sqrt(
                (sx - cx) * (sx - cx) +
                (sy - cy) * (sy - cy)
            );

        double start;
        double end;

        if (counterClockwise)
        {
            start = AngleDeg(sy - cy, sx - cx);
            end = AngleDeg(ey - cy, ex - cx);

            while (end <= start)
                end += 360.0;
        }
        else
        {
            // DXF ARC всегда хранится против часовой стрелки.
            // Для G2 разворачиваем направление: конечная точка ACM
            // становится стартовым углом DXF.
            start = AngleDeg(ey - cy, ex - cx);
            end = AngleDeg(sy - cy, sx - cx);

            while (start >= end)
                start -= 360.0;
        }

        ArcEntity a = new ArcEntity();

        a.Color = color;
        a.CX = cx;
        a.CY = cy;
        a.Radius = r;
        a.StartAngle = start;
        a.EndAngle = end;

        return a;
    }

    private static double AngleDeg(double y, double x)
    {
        return Math.Atan2(y, x) * 180.0 / Math.PI;
    }

    // ============================================================
    // Запись DXF R12
    // ============================================================

    public static string BuildDxf(AcmDocument doc)
    {
        StringBuilder s = new StringBuilder(16384);

        Pair(s, 0, "SECTION");
        Pair(s, 2, "HEADER");

        Pair(s, 9, "$ACADVER");
        Pair(s, 1, "AC1009");

        Pair(s, 9, "$EXTMIN");
        Pair(s, 10, "0");
        Pair(s, 20, "0");

        Pair(s, 9, "$EXTMAX");
        Pair(s, 10, Fmt(doc.Width));
        Pair(s, 20, Fmt(doc.Height));

        Pair(s, 0, "ENDSEC");

        Pair(s, 0, "SECTION");
        Pair(s, 2, "TABLES");

        Pair(s, 0, "TABLE");
        Pair(s, 2, "LAYER");
        Pair(s, 70, "1");

        Pair(s, 0, "LAYER");
        Pair(s, 2, "Design");
        Pair(s, 70, "64");
        Pair(s, 62, "7");
        Pair(s, 6, "CONTINUOUS");

        Pair(s, 0, "ENDTAB");
        Pair(s, 0, "ENDSEC");

        Pair(s, 0, "SECTION");
        Pair(s, 2, "BLOCKS");
        Pair(s, 0, "ENDSEC");

        Pair(s, 0, "SECTION");
        Pair(s, 2, "ENTITIES");

        foreach (Entity entity in doc.Entities)
        {
            LineEntity line = entity as LineEntity;

            if (line != null)
            {
                Pair(s, 0, "LINE");
                CommonEntity(s, line.Color);

                Pair(s, 10, Fmt(line.X1));
                Pair(s, 20, Fmt(line.Y1));
                Pair(s, 11, Fmt(line.X2));
                Pair(s, 21, Fmt(line.Y2));

                continue;
            }

            ArcEntity arc = entity as ArcEntity;

            if (arc != null)
            {
                Pair(s, 0, "ARC");
                CommonEntity(s, arc.Color);

                Pair(s, 10, FmtArc(arc.CX));
                Pair(s, 20, FmtArc(arc.CY));
                Pair(s, 40, FmtArc(arc.Radius));
                Pair(s, 50, FmtArc(arc.StartAngle));
                Pair(s, 51, FmtArc(arc.EndAngle));

                continue;
            }

            CircleEntity circle = entity as CircleEntity;

            if (circle != null)
            {
                Pair(s, 0, "CIRCLE");
                CommonEntity(s, circle.Color);

                Pair(s, 10, FmtArc(circle.CX));
                Pair(s, 20, FmtArc(circle.CY));
                Pair(s, 40, FmtArc(circle.Radius));

                continue;
            }
        }

        Pair(s, 0, "ENDSEC");
        Pair(s, 0, "EOF");

        return s.ToString();
    }

    private static void CommonEntity(
        StringBuilder s,
        int color)
    {
        Pair(s, 8, "Design");
        Pair(s, 6, "CONTINUOUS");
        Pair(s, 62, color.ToString(Inv));
    }

    private static void Pair(
        StringBuilder s,
        int code,
        string value)
    {
        s.Append(code.ToString(Inv).PadLeft(3));
        s.Append("\r\n");
        s.Append(value);
        s.Append("\r\n");
    }

    private static string Fmt(double value)
    {
        if (Math.Abs(value) < 0.0000000001)
            value = 0.0;

        return value.ToString(
            "0.##########",
            Inv
        );
    }

    private static string FmtArc(double value)
    {
        if (Math.Abs(value) < 0.0000000001)
            value = 0.0;

        return value.ToString(
            "0.000000000",
            Inv
        );
    }
    private static double ParseDouble(string value)
    {
        return double.Parse(value, NumberStyles.Float, Inv);
    }
} }
