// Review-only tool: writes a COPY of an assembly in which internal types/members in the
// Microsoft.IdentityModel / System.IdentityModel namespaces are marked public, for use as a
// compile-time reference only. The original, unmodified assemblies are what runs.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length != 2) { Console.Error.WriteLine("usage: Publicize <in.dll> <out.dll>"); return 2; }
var bytes = File.ReadAllBytes(args[0]);
int types = 0, methods = 0, fields = 0;
using (var pe = new PEReader(new MemoryStream(bytes, writable: false)))
{
    var md = pe.GetMetadataReader();
    var mdStart = pe.PEHeaders.MetadataStartOffset;
    int Row(TableIndex t, int rid) => mdStart + md.GetTableMetadataOffset(t) + (rid - 1) * md.GetTableRowSize(t);

    bool InScope(TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h);
        while (!td.GetDeclaringType().IsNil) td = md.GetTypeDefinition(td.GetDeclaringType());
        var ns = md.GetString(td.Namespace);
        return ns.StartsWith("Microsoft.IdentityModel", StringComparison.Ordinal) || ns.StartsWith("System.IdentityModel", StringComparison.Ordinal);
    }

    foreach (var h in md.TypeDefinitions)
    {
        if (!InScope(h)) continue;
        var td = md.GetTypeDefinition(h);
        var off = Row(TableIndex.TypeDef, MetadataTokens.GetRowNumber(h));
        var flags = BitConverter.ToUInt32(bytes, off);
        var vis = flags & 0x7;
        uint newVis = vis switch { 0 => 1u, 3 or 5 or 6 or 7 => 2u, _ => vis };   // NotPublic->Public, NestedAssembly/FamANDAssem/FamORAssem/Private->NestedPublic
        if (vis == 3) newVis = vis;                                                // leave nested private alone
        if (newVis != vis) { BitConverter.TryWriteBytes(bytes.AsSpan(off, 4), (flags & ~0x7u) | newVis); types++; }

        foreach (var mh in td.GetMethods())
        {
            var moff = Row(TableIndex.MethodDef, MetadataTokens.GetRowNumber(mh)) + 6;   // RVA(4) ImplFlags(2) Flags(2)
            var mf = BitConverter.ToUInt16(bytes, moff);
            var acc = mf & 0x7;
            if (acc is 2 or 3 or 5) { BitConverter.TryWriteBytes(bytes.AsSpan(moff, 2), (ushort)((mf & ~0x7) | 6)); methods++; }
        }
        foreach (var fh in td.GetFields())
        {
            var foff = Row(TableIndex.Field, MetadataTokens.GetRowNumber(fh));
            var ff = BitConverter.ToUInt16(bytes, foff);
            var acc = ff & 0x7;
            if (acc is 2 or 3 or 5) { BitConverter.TryWriteBytes(bytes.AsSpan(foff, 2), (ushort)((ff & ~0x7) | 6)); fields++; }
        }
    }
}
File.WriteAllBytes(args[1], bytes);
Console.WriteLine($"{Path.GetFileName(args[0])}: publicized {types} types, {methods} methods, {fields} fields");
return 0;
