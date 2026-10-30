using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis.Diagnostics;
// args: package-name dll...  -> prints package,id,category,defaultSeverity,enabledByDefault
var pkg = args[0];
var seen = new HashSet<string>();
foreach (var path in args.Skip(1))
{
    var dir = Path.GetDirectoryName(path)!;
    var ctx = new AssemblyLoadContext(path, false);
    ctx.Resolving += (c, n) => { var p = Path.Combine(dir, n.Name + ".dll"); return File.Exists(p) ? c.LoadFromAssemblyPath(p) : null; };
    var asm = ctx.LoadFromAssemblyPath(path);
    Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
    foreach (var t in types.Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null))
    {
        try { foreach (var d in ((DiagnosticAnalyzer)Activator.CreateInstance(t)!).SupportedDiagnostics)
            if (seen.Add(d.Id)) Console.WriteLine($"{pkg},{d.Id},{d.Category},{d.DefaultSeverity},{d.IsEnabledByDefault}"); }
        catch (Exception ex) { Console.Error.WriteLine($"{t.FullName}: {ex.GetType().Name}"); }
    }
}
