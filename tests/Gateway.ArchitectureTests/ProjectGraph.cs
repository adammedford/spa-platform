using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Gateway.ArchitectureTests;

/// <summary>One project, as its project file declares its references.</summary>
/// <param name="Name">The file name without its extension, which is the project's name.</param>
/// <param name="Path">Repository-relative, for a failure message that can be acted on.</param>
public sealed record Project(
    string Name,
    string Path,
    ImmutableArray<string> ProjectReferences,
    ImmutableArray<string> PackageReferences);

/// <summary>
/// Every project in the solution and what it references. A-01 to A-04 are rules about references,
/// so they are read from the project files rather than from compiled assemblies: a reference that
/// exists but is unused is still a reference, and the compiler flattens transitive ones into a
/// shape where "who asked for this" is no longer visible.
/// </summary>
public sealed class ProjectGraph
{
    private readonly ImmutableDictionary<string, Project> projects;

    public ProjectGraph(IEnumerable<Project> projects) =>
        this.projects = projects.ToImmutableDictionary(project => project.Name, StringComparer.Ordinal);

    /// <summary>The solution as it is on disk.</summary>
    public static ProjectGraph Solution { get; } = Load();

    /// <summary>Where this repository is, for a test that looks beyond the solution file.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public IEnumerable<Project> Projects => projects.Values;

    public Project this[string name] => projects.TryGetValue(name, out Project? project)
        ? project
        : throw new KeyNotFoundException($"{name} is not a project in the solution.");

    public bool Contains(string name) => projects.ContainsKey(name);

    /// <summary>
    /// Everything a project reaches through project references, directly or not, excluding itself.
    /// A-01 needs the closure and not the direct list: a test identity provider reached through one
    /// more project is still compiled into the gateway.
    /// </summary>
    public ImmutableHashSet<string> ClosureOf(string name)
    {
        HashSet<string> reached = [];
        Queue<string> pending = new([name]);

        while (pending.TryDequeue(out string? current))
        {
            foreach (string reference in projects.TryGetValue(current, out Project? project)
                ? project.ProjectReferences
                : [])
            {
                if (reached.Add(reference))
                {
                    pending.Enqueue(reference);
                }
            }
        }

        reached.Remove(name);

        return [.. reached];
    }

    private static ProjectGraph Load()
    {
        string root = FindRepositoryRoot();
        XDocument solution = XDocument.Load(System.IO.Path.Combine(root, "SpaPlatform.slnx"));

        IEnumerable<string> paths = solution.Descendants("Project")
            .Select(project => project.Attribute("Path")?.Value)
            .Where(path => path is not null)
            .Select(path => path!);

        return new ProjectGraph(paths.Select(path => Read(root, path)));
    }

    private static Project Read(string root, string relativePath)
    {
        string path = System.IO.Path.Combine(root, relativePath);
        XDocument file = XDocument.Load(path);

        return new Project(
            Name: System.IO.Path.GetFileNameWithoutExtension(path),
            Path: relativePath.Replace('\\', '/'),
            ProjectReferences: [.. Items(file, "ProjectReference").Select(System.IO.Path.GetFileNameWithoutExtension).Select(name => name!)],
            PackageReferences: [.. Items(file, "PackageReference")]);
    }

    private static IEnumerable<string> Items(XDocument file, string element) =>
        file.Descendants(element)
            .Select(item => item.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrEmpty(include))
            .Select(include => include!.Replace('\\', '/'));

    private static string FindRepositoryRoot([CallerFilePath] string? thisFile = null)
    {
        // The source tree first: this file sits at tests/Gateway.ArchitectureTests/ProjectGraph.cs.
        string? fromSource = thisFile is null
            ? null
            : System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "..", ".."));

        if (fromSource is not null && File.Exists(System.IO.Path.Combine(fromSource, "SpaPlatform.slnx")))
        {
            return fromSource;
        }

        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(System.IO.Path.Combine(candidate.FullName, "SpaPlatform.slnx")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            $"the repository root was not found from '{thisFile}' or from '{AppContext.BaseDirectory}'.");
    }
}
