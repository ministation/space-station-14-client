using System.Buffers.Binary;
using System.Net.Http;
using Port.Content;

var http = PortHttp.Create(TimeSpan.FromMinutes(5));
var acz = new AczContentClient(http);
var baseUrl = "http://ss14.ministation.ru:1214";
var manBytes = await acz.DownloadManifestAsync(baseUrl);
var man = ContentManifest.Parse(manBytes);
var asm = man.Entries.Where(e => e.Path.StartsWith("Assemblies/") && e.Path.EndsWith(".dll")).Take(6).Select(e => e.Index).ToArray();
Console.WriteLine($"manifest {man.Entries.Count} asm sample {asm.Length}: {string.Join(",", asm)}");
var tmp = Path.Combine(Path.GetTempPath(), "acz-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
var n = await acz.DownloadFilesBatchedAsync(baseUrl, man, asm, tmp, batchSize: 3, stage: "assemblies");
Console.WriteLine($"downloaded {n}");
foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
  Console.WriteLine($"  {f.Substring(tmp.Length+1)} {new FileInfo(f).Length}");
