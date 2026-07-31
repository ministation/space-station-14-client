using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

var contentDir = args.ElementAtOrDefault(0)
                 ?? @"c:\ss14\space-station-14\bin\Content.Client";
var stubRoot = args.ElementAtOrDefault(1)
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                   "src", "Port.RobustClientStub"));
var outDir = args.ElementAtOrDefault(2)
             ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                 "artifacts", "content-bind-stub"));

var shared = Path.Combine(contentDir, "Robust.Shared.dll");
var maths = Path.Combine(contentDir, "Robust.Shared.Maths.dll");
if (!File.Exists(shared))
{
    Console.Error.WriteLine("missing " + shared);
    return 1;
}

Directory.CreateDirectory(outDir);
var projDir = Path.Combine(outDir, "proj");
Directory.CreateDirectory(projDir);

var sharedVer = AssemblyName.GetAssemblyName(shared).Version?.ToString() ?? "0.0.0.0";
var files = Directory.EnumerateFiles(stubRoot, "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
    .Select(Path.GetFullPath)
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

var sb = new StringBuilder();
sb.AppendLine("""
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
        <AssemblyName>Robust.Client</AssemblyName>
        <RootNamespace>Robust.Client</RootNamespace>
        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
        <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
        <OutputPath>../out/</OutputPath>
    """);
sb.AppendLine($"        <AssemblyVersion>{sharedVer}</AssemblyVersion>");
sb.AppendLine($"        <FileVersion>{sharedVer}</FileVersion>");
sb.AppendLine("      </PropertyGroup>");
sb.AppendLine("      <ItemGroup>");
foreach (var f in files)
    sb.AppendLine($"        <Compile Include=\"{f.Replace('\\', '/')}\" />");
sb.AppendLine($"        <Reference Include=\"Robust.Shared\"><HintPath>\"{shared.Replace('\\', '/')}\"/><Private>false</Private></Reference>");
if (File.Exists(maths))
    sb.AppendLine($"        <Reference Include=\"Robust.Shared.Maths\"><HintPath>\"{maths.Replace('\\', '/')}\"/><Private>false</Private></Reference>");
sb.AppendLine("      </ItemGroup>");
sb.AppendLine("</Project>");

var csproj = Path.Combine(projDir, "ContentBind.csproj");
File.WriteAllText(csproj, sb.ToString());

var psi = new ProcessStartInfo("dotnet", $"build \"{csproj}\" -c Release --nologo")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
using var proc = Process.Start(psi)!;
var stdout = proc.StandardOutput.ReadToEnd();
var stderr = proc.StandardError.ReadToEnd();
proc.WaitForExit();
Console.Write(stdout);
if (!string.IsNullOrWhiteSpace(stderr))
    Console.Error.Write(stderr);

var built = Path.Combine(outDir, "out", "Robust.Client.dll");
var dest = Path.Combine(outDir, "Robust.Client.ContentBind.dll");
if (!File.Exists(built))
{
    Console.Error.WriteLine("build produced no DLL");
    return proc.ExitCode == 0 ? 2 : proc.ExitCode;
}

File.Copy(built, dest, overwrite: true);
// Also drop next to content dir for the host loader.
var sideBySide = Path.Combine(contentDir, "Robust.Client.ContentBind.dll");
try { File.Copy(dest, sideBySide, overwrite: true); } catch { /* may be locked */ }

Console.WriteLine($"OK content-bind stub → {dest} (Shared {sharedVer})");

// Smoke: Content.Client GetExportedTypes with bind stub (not desktop Clyde client).
var alc = new AssemblyLoadContext("bind-smoke", isCollectible: true);
alc.Resolving += (_, name) =>
{
    if (name.Name is null) return null;
    if (name.Name.Equals("Robust.Client", StringComparison.OrdinalIgnoreCase))
        return alc.LoadFromAssemblyPath(dest);
    var local = Path.Combine(contentDir, name.Name + ".dll");
    if (File.Exists(local) && !name.Name.Equals("Robust.Client", StringComparison.OrdinalIgnoreCase))
        return alc.LoadFromAssemblyPath(local);
    return null;
};
alc.LoadFromAssemblyPath(shared);
if (File.Exists(maths)) alc.LoadFromAssemblyPath(maths);
alc.LoadFromAssemblyPath(dest);
var clientPath = Path.Combine(contentDir, "Content.Client.dll");
var asm = alc.LoadFromAssemblyPath(clientPath);
var types = asm.GetExportedTypes().Length;
Console.WriteLine($"SMOKE Content.Client types={types}");
return types > 0 ? 0 : 3;
