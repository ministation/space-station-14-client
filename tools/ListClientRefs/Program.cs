using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Collections.Generic;

var path = @"c:\ss14\space-station-14\bin\Content.Client\Content.Client.dll";
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var mr = pe.GetMetadataReader();
var refs = new HashSet<string>(StringComparer.Ordinal);
foreach (var h in mr.TypeReferences)
{
    var t = mr.GetTypeReference(h);
    var ns = mr.GetString(t.Namespace);
    var name = mr.GetString(t.Name);
    if (ns.StartsWith("Robust.Client", StringComparison.Ordinal))
        refs.Add(ns + "." + name);
}
foreach (var r in refs.OrderBy(x => x))
    Console.WriteLine(r);
Console.WriteLine("--- total " + refs.Count);
