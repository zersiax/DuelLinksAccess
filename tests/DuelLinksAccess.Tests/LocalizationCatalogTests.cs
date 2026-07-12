using System.Reflection;
using System.Text.RegularExpressions;
using System.Globalization;
using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class LocalizationCatalogTests
{
    private static readonly Regex LiteralLookupRegex = new(
        "Loc\\.Get\\(\\s*\"(?<key>[^\"]+)\"",
        RegexOptions.Compiled);
    private static readonly Regex DefinitionRegex = new(
        "_english\\[\"(?<key>[^\"]+)\"\\]\\s*=",
        RegexOptions.Compiled);

    [Fact]
    public void Catalog_HasNoDuplicateDefinitions()
    {
        string locSource = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "Loc.cs"));
        string[] keys = DefinitionRegex.Matches(locSource)
            .Select(match => match.Groups["key"].Value)
            .ToArray();
        string[] duplicates = keys.GroupBy(key => key)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Catalog_DefinesEveryLiteralLookup()
    {
        string root = FindRepositoryRoot();
        string locSource = File.ReadAllText(Path.Combine(root, "src", "Loc.cs"));
        var definitions = DefinitionRegex.Matches(locSource)
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
        string[] missing = Directory.GetFiles(
                Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(
                Path.Combine("src", "Loc.cs"),
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => LiteralLookupRegex.Matches(File.ReadAllText(path)))
            .Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !definitions.Contains(key))
            .OrderBy(key => key)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Catalog_HasValidCompositeFormatStrings()
    {
        Loc.Initialize();
        var field = typeof(Loc).GetField(
            "_english", BindingFlags.Static | BindingFlags.NonPublic);
        var catalog = Assert.IsType<Dictionary<string, string>>(
            field?.GetValue(null));
        object[] arguments = Enumerable.Range(0, 100)
            .Cast<object>()
            .ToArray();

        string[] invalid = catalog
            .Where(entry => !CanFormat(entry.Value, arguments))
            .Select(entry => entry.Key)
            .OrderBy(key => key)
            .ToArray();

        Assert.Empty(invalid);
    }

    private static bool CanFormat(string template, object[] arguments)
    {
        try
        {
            string.Format(CultureInfo.InvariantCulture, template, arguments);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "DuelLinksAccess.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found");
    }
}
