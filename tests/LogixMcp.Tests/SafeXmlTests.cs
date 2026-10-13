using System.Text.RegularExpressions;
using System.Xml;
using Xunit;

namespace LogixMcp.Tests;

public class SafeXmlTests
{
    private const string Dtd =
        "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x \"boom\">]><RSLogix5000Content>&x;</RSLogix5000Content>";

    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"safexml-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Dtd_Refused()
    {
        var ex = Assert.Throws<SafeXmlException>(() => SafeXml.ParseDocument(Dtd));
        Assert.Equal(SafeXml.InvalidL5x, ex.Kind);
        Assert.IsAssignableFrom<XmlException>(ex);

        var path = TempFile(Dtd);
        try
        {
            var fileEx = Assert.Throws<SafeXmlException>(() => SafeXml.LoadDocument(path));
            Assert.Equal(SafeXml.InvalidL5x, fileEx.Kind);
            Assert.Throws<SafeXmlException>(() => SafeXml.LoadL5x(path));
            Assert.Throws<SafeXmlException>(() => SafeXml.ParseL5x(Dtd));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Oversize_Refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"safexml-{Guid.NewGuid():N}.L5X");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create))
                fs.SetLength(SafeXml.MaxBytes + 1);   // sparse; content never read

            var ex = Assert.Throws<SafeXmlException>(() => SafeXml.LoadDocument(path));
            Assert.Equal(SafeXml.InputTooLarge, ex.Kind);
            Assert.Equal(SafeXml.InputTooLarge, Assert.Throws<SafeXmlException>(() => SafeXml.LoadL5x(path)).Kind);
        }
        finally { File.Delete(path); }

        var big = new string('a', (int)SafeXml.MaxBytes + 1);
        Assert.Equal(SafeXml.InputTooLarge,
            Assert.Throws<SafeXmlException>(() => SafeXml.ParseDocument(big)).Kind);
    }

    [Fact]
    public void Malformed_IsInvalidL5x()
    {
        var ex = Assert.Throws<SafeXmlException>(() => SafeXml.ParseDocument("<a>"));
        Assert.Equal(SafeXml.InvalidL5x, ex.Kind);
    }

    [Fact]
    public void ValidDocument_Parses()
    {
        var doc = SafeXml.ParseDocument("<a><b/></a>");
        Assert.Equal("a", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void AllEntryPoints_UseSafeXml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("*.sln").Length == 0) dir = dir.Parent;
        Assert.NotNull(dir);

        var forbidden = new Regex(
            @"\b(XDocument|XElement)\s*\.\s*(Load|Parse)\b|\bL5X\s*\.\s*(Load|Parse)\b|\bXmlReader\s*\.\s*Create\b|\bnew\s+XmlDocument\b");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            if (Path.GetFileName(file) == "SafeXml.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (forbidden.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(dir.FullName, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "Unsafe XML entry points:\n" + string.Join("\n", offenders));
    }
}
