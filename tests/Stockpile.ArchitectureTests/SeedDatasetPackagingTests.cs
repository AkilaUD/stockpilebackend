using System.Xml.Linq;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// The API seeds two migration datasets while starting: the live catalogue
/// (<c>CatalogueSeeder</c>) and the reference news tree (<c>ContentSeeder</c>).
/// Both seeders resolve their dataset from disk beside the content root, so a
/// published deployment must ship every file under <c>tools/*-migrate/out</c>.
/// This guards the packaging half of that contract: Stockpile.Api.csproj must
/// link each dataset into both the output and the publish directory.
/// </summary>
/// <remarks>
/// Regression for the 2026-10-15 boot failure. The catalogue dataset was
/// packaged but the content dataset was not, so a published API threw
/// "Content seed dataset not found" out of
/// <c>ContentSeeder.ResolveDatasetPath</c> and terminated in
/// <c>DbSeeder.SeedAsync</c> before Kestrel ever listened. A development run
/// hides this because the resolver walks up from the project directory to the
/// repository root; only a published layout exposes it.
/// </remarks>
public sealed class SeedDatasetPackagingTests
{
    private static readonly (string Directory, string File)[] SeedDatasets =
    [
        ("tools/catalogue-migrate/out", "catalogue.json"),
        ("tools/content-migrate/out", "articles.json"),
    ];

    [Fact]
    public void Every_seed_dataset_is_packaged_with_the_published_api()
    {
        // The solution marker sits in backend/, so the projects live below it
        // while the migration datasets live at the repository root above it.
        string backendRoot = FindRepositoryRoot("Stockpile.slnx");
        string repositoryRoot = Directory.GetParent(backendRoot)?.FullName ?? backendRoot;
        string csproj = Path.Combine(backendRoot, "src", "Stockpile.Api", "Stockpile.Api.csproj");
        Assert.True(File.Exists(csproj), $"API project not found at '{csproj}'.");

        XDocument document = XDocument.Load(csproj);
        var failures = new List<string>();

        foreach ((string directory, string file) in SeedDatasets)
        {
            // The dataset itself must still exist: an empty repository would
            // otherwise make the packaging check pass vacuously.
            string source = Path.Combine(repositoryRoot, directory.Replace('/', Path.DirectorySeparatorChar), file);
            if (!File.Exists(source))
            {
                failures.Add($"{directory}/{file}: dataset file is missing from the repository");
            }

            XElement? item = document
                .Descendants("Content")
                .FirstOrDefault(element => Normalize(element.Attribute("Include")?.Value)
                    .Contains(directory + "/", StringComparison.Ordinal));

            if (item is null)
            {
                failures.Add($"{directory}: no <Content Include> packs this dataset in Stockpile.Api.csproj");
                continue;
            }

            string? publish = item.Element("CopyToPublishDirectory")?.Value.Trim();
            if (!string.Equals(publish, "PreserveNewest", StringComparison.Ordinal))
            {
                failures.Add(
                    $"{directory}: CopyToPublishDirectory is '{publish ?? "<missing>"}', expected 'PreserveNewest'");
            }

            string? output = item.Element("CopyToOutputDirectory")?.Value.Trim();
            if (!string.Equals(output, "PreserveNewest", StringComparison.Ordinal))
            {
                failures.Add(
                    $"{directory}: CopyToOutputDirectory is '{output ?? "<missing>"}', expected 'PreserveNewest'");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Seed dataset packaging violated:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Railway builds from the repository root with <c>backend/Dockerfile</c>.
    /// The image must COPY each seed dataset tree so <c>dotnet publish</c> can
    /// pack them via the csproj Content includes (relative to repo root).
    /// </summary>
    [Fact]
    public void Api_dockerfile_copies_seed_dataset_trees()
    {
        string backendRoot = FindRepositoryRoot("Stockpile.slnx");
        string dockerfile = Path.Combine(backendRoot, "Dockerfile");
        Assert.True(File.Exists(dockerfile), $"API Dockerfile not found at '{dockerfile}'.");

        string text = File.ReadAllText(dockerfile);
        var failures = new List<string>();

        foreach ((string directory, string _) in SeedDatasets)
        {
            string expected = $"COPY {directory}/ ./{directory}/";
            if (!text.Contains(expected, StringComparison.Ordinal))
            {
                failures.Add($"missing '{expected}'");
            }
        }

        Assert.True(
            failures.Count == 0,
            "backend/Dockerfile must COPY seed datasets from the repo-root build context:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Replace('\\', '/');

    private static string FindRepositoryRoot(string marker)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, marker)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Could not locate repository root marker '{marker}'.");
    }
}
