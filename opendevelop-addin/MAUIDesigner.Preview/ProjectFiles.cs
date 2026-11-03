namespace MAUIDesigner.Preview;

/// <summary>Locates the project that owns a file when the IDE's item lookup cannot (a XAML file
/// that is not a project item yet, or a file opened without a solution).</summary>
public static class ProjectFiles
{
    static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>The nearest project file at or above <paramref name="filePath"/>'s directory, or
    /// null. Deliberately bounded and non-recursive: only the directories that contain the file.</summary>
    public static string? FindAbove(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        try
        {
            var directory = Directory.Exists(filePath)
                ? new DirectoryInfo(filePath)
                : new FileInfo(filePath).Directory;
            for (var depth = 0; directory != null && depth < 32; depth++, directory = directory.Parent)
            {
                var candidates = directory.EnumerateFiles()
                    .Where(f => ProjectExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToArray();
                if (candidates.Length > 0)
                    return candidates[0].FullName;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
