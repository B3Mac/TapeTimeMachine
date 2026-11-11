using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace TapeRescue.Services;

public sealed record Chapter(TimeSpan Start, string Name);

/// <summary>Writes chapter lists in the two formats the tools understand: MKVToolNix XML and FFmpeg metadata.</summary>
public static class ChapterWriter
{
    /// <summary>"Tape 7.avi" → "Tape 7.chapters.xml" in the same folder.</summary>
    public static string XmlPathFor(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + ".chapters.xml");

    /// <summary>Turns scene start times into chapters named "Scene 1", "Scene 2"…</summary>
    public static List<Chapter> FromScenes(IReadOnlyList<TimeSpan> starts) =>
        starts.Select((t, i) => new Chapter(t, $"Scene {i + 1}")).ToList();

    /// <summary>MKVToolNix chapter XML (works with mkvmerge --chapters and MKVToolNix GUI).</summary>
    public static void WriteMatroskaXml(string path, IReadOnlyList<Chapter> chapters)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var xml = XmlWriter.Create(path, settings);
        xml.WriteStartDocument();
        xml.WriteDocType("Chapters", null, "matroskachapters.dtd", null);
        xml.WriteStartElement("Chapters");
        xml.WriteStartElement("EditionEntry");
        foreach (var chapter in chapters)
        {
            xml.WriteStartElement("ChapterAtom");
            xml.WriteElementString("ChapterTimeStart", MatroskaTime(chapter.Start));
            xml.WriteStartElement("ChapterDisplay");
            xml.WriteElementString("ChapterString", chapter.Name);
            xml.WriteElementString("ChapterLanguage", "eng");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    /// <summary>FFmpeg's metadata format, used to add chapters while converting or remuxing.</summary>
    public static void WriteFfMetadata(string path, IReadOnlyList<Chapter> chapters, TimeSpan totalDuration)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        for (int i = 0; i < chapters.Count; i++)
        {
            long start = (long)chapters[i].Start.TotalMilliseconds;
            long end = (long)(i + 1 < chapters.Count ? chapters[i + 1].Start : totalDuration).TotalMilliseconds;
            if (end <= start) end = start + 1;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append(CultureInfo.InvariantCulture, $"START={start}\nEND={end}\n");
            sb.Append("title=").Append(Escape(chapters[i].Name)).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Reads chapters back from a MKVToolNix XML file. Empty if the file is missing or not chapters.</summary>
    public static List<Chapter> ReadMatroskaXml(string path)
    {
        var result = new List<Chapter>();
        if (!File.Exists(path)) return result;
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            doc.Load(reader);
            foreach (XmlNode atom in doc.SelectNodes("//ChapterAtom")!)
            {
                string? time = atom.SelectSingleNode("ChapterTimeStart")?.InnerText;
                string name = atom.SelectSingleNode("ChapterDisplay/ChapterString")?.InnerText ?? $"Chapter {result.Count + 1}";
                if (time is not null && TryParseMatroskaTime(time, out var start)) result.Add(new Chapter(start, name));
            }
        }
        catch (Exception ex) { Log.Error($"Reading chapters from {path}", ex); }
        return result;
    }

    private static string MatroskaTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Ticks % TimeSpan.TicksPerSecond * 100:000000000}");

    private static bool TryParseMatroskaTime(string s, out TimeSpan time)
    {
        time = default;
        var parts = s.Trim().Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)) return false;
        time = TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
        return true;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
