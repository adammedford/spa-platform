using System.Reflection;
using System.Runtime.Loader;

namespace Platform.Registry.Tests;

/// <summary>
/// Issue #373: under the full parallel run, the first calls of <see cref="ContractDocuments.IsRelease"/>
/// raced one another, and every later call failed with <c>RefResolutionException</c> on
/// <c>registry.schema.json#/$defs/release</c> (30 failures in <c>Spactl.Tests</c> on 2026-10-23 and
/// 2026-10-24).
///
/// The race is on first use only, and the schemas are statics, so in this process the cache is warm
/// after the first test that checks a name and a hammer here would check nothing. Each iteration
/// therefore loads <c>Platform.Registry</c> and the schema engine afresh into a collectible
/// <see cref="AssemblyLoadContext"/>, which gives it fresh statics (the engine's global schema
/// registry among them), and starts every thread at one barrier on that fresh copy.
/// </summary>
public sealed class ConcurrentFirstUseTests
{
    private const int Iterations = 100;

    private const int Threads = 32;

    /// <summary>One valid value per name, from <see cref="ContractDocumentsTests"/>' passing rows.</summary>
    private static readonly (string Method, string Value)[] Calls =
        [("IsAppId", "app1"), ("IsRelease", "2026-10-10.1")];

    [Fact]
    public void Concurrent_first_checks_of_an_app_id_and_a_release_all_resolve()
    {
        List<string> failures = [];
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            failures.AddRange(FirstUseAtOnce().Select(failure => $"iteration {iteration}: {failure}"));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failed calls:\n" + string.Join('\n', failures.Distinct().Take(10)));
    }

    /// <summary>Every thread's first call on a freshly loaded copy, released together.</summary>
    private static List<string> FirstUseAtOnce()
    {
        FreshContext context = new();
        try
        {
            Type documents = context.LoadFromAssemblyName(typeof(ContractDocuments).Assembly.GetName())
                .GetType(typeof(ContractDocuments).FullName!, throwOnError: true)!;

            // The test checks something only while both copies are fresh: a fall-through to the default
            // context would hit this process's warm statics and pass having raced nothing.
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(documents.Assembly));
            Assembly engine = context.LoadFromAssemblyName(typeof(Json.Schema.JsonSchema).Assembly.GetName());
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(engine));
            Assert.Contains(documents.Assembly.GetReferencedAssemblies(), name => name.Name == engine.GetName().Name);

            List<string> failures = [];
            using Barrier start = new(Threads);
            Thread[] threads = new Thread[Threads];
            for (int index = 0; index < Threads; index++)
            {
                (string method, string value) = Calls[index % Calls.Length];
                MethodInfo check = documents.GetMethod(method, [typeof(string)])!;
                threads[index] = new Thread(() =>
                {
                    start.SignalAndWait();
                    string? failure = Check(check, value);
                    if (failure is not null)
                    {
                        lock (failures)
                        {
                            failures.Add(failure);
                        }
                    }
                });
                threads[index].Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }

            return failures;
        }
        finally
        {
            context.Unload();
        }
    }

    private static string? Check(MethodInfo check, string value)
    {
        try
        {
            return check.Invoke(null, [value]) is true ? null : $"{check.Name}(\"{value}\") returned false";
        }
        catch (TargetInvocationException exception)
        {
            return $"{check.Name}(\"{value}\") threw {exception.InnerException?.GetType().Name}: {exception.InnerException?.Message}";
        }
    }

    /// <summary>
    /// Loads every assembly the test's output directory holds, so <c>Platform.Registry</c>,
    /// <c>Platform.Abstractions</c> and the schema engine with its dependencies are fresh copies. The
    /// framework falls through to the default context.
    /// </summary>
    private sealed class FreshContext() : AssemblyLoadContext(nameof(ConcurrentFirstUseTests), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
