using System.Text.RegularExpressions;

namespace EduNexus.Foundation.Tests;

public class SourceHygieneTests
{
    private static string BackendSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EduNexus.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("EduNexus.sln not found above the test output."), "src");
    }

    /// <summary>
    /// A bare <c>catch { }</c> turns every failure into silence (the storage SDK problem, G6, was this in miniature).
    /// A narrow handler with a comment saying why is fine; an empty one is not.
    /// </summary>
    [Fact]
    public void No_Empty_Catch_Blocks_In_Backend_Source()
    {
        var empty = new Regex(@"catch\s*(\([^)]*\))?\s*(when\s*\([^)]*\))?\s*\{\s*\}", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(BackendSource(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in empty.Matches(text))
                offenders.Add($"{Path.GetRelativePath(BackendSource(), file)}:{text[..m.Index].Count(c => c == '\n') + 1}");
        }
        Assert.True(offenders.Count == 0, "Empty catch blocks (handle the error, or narrow it and say why in a comment): " + string.Join(", ", offenders));
    }
}
