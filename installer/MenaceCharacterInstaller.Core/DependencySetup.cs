using System.Diagnostics;
using System.IO.Compression;

namespace MenaceCharacterInstaller;

public sealed record DependencyInstallResult(string? Backup,bool Changed,bool RequiresRestart=false);
public interface IRuntimeSetup
{
    string Version {get;}
    Task<int> Run(string installer,CancellationToken ct);
}
public sealed class MicrosoftRuntimeSetup : IRuntimeSetup
{
    public string Version=>Installer.RuntimeVersion();
    public async Task<int> Run(string installer,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process=Process.Start(new ProcessStartInfo(installer,"/install /norestart")
        {UseShellExecute=true,Verb="runas"})??throw new IOException("Could not start the Microsoft runtime installer");
        // Windows Installer owns the operation once launched. Do not terminate it on Cancel.
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }
}
public sealed partial class Installer
{
    private IRuntimeSetup Runtime=>runtimeSetup??new MicrosoftRuntimeSetup();
    private DependencyStatus[] CurrentDependencies(string game)
    {
        var statuses=Dependencies(game,catalog);
        if(runtimeSetup is not null)statuses=statuses.Select(d=>d.Name==".NET 6 Desktop x64"?d with {Installed=runtimeSetup.Version,Required=runtimeSetup.Version=="Not installed"}:d).ToArray();
        return statuses;
    }
    private static void CheckLoaderConflict(string game,bool replacingMelon)
    {
        if(Directory.Exists(Path.Combine(game,"BepInEx")))throw new IOException("BepInEx detected. This installer supports MelonLoader; resolve the loader conflict first.");
        if(replacingMelon&&File.Exists(Path.Combine(game,"version.dll"))&&!File.Exists(Path.Combine(game,"MelonLoader","net6","MelonLoader.dll")))throw new IOException("An existing version.dll belongs to an unknown loader. It will not be overwritten.");
    }
    private async Task<bool> SetupRuntime(string file,IProgress<string>? log,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log?.Report("Installing Microsoft .NET 6 Desktop x64. Windows may ask for administrator approval.");
        var setup=Runtime;int code;
        try{code=await setup.Run(file,ct);}
        catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==1223){throw new OperationCanceledException("The Microsoft runtime installation was cancelled.",ex);}
        if(code==1602)throw new OperationCanceledException("The Microsoft runtime installation was cancelled.");
        if(code is not 0 and not 3010)throw new IOException("Microsoft runtime installer returned "+code);
        if(setup.Version=="Not installed"&&code!=3010)throw new IOException("Runtime installation could not be verified. Restart Windows and try again.");
        if(code==3010)log?.Report("Microsoft runtime requests a Windows restart before playing.");
        return code==3010;
    }
    public async Task<DependencyInstallResult> InstallDependency(string game,string name,IProgress<string>? log,CancellationToken ct,string? localArchive=null)
    {
        var info=Games.Inspect(game);game=info.Path;Games.RequireClosed();
        var dependency=CurrentDependencies(game).SingleOrDefault(d=>d.Name==name)??throw new InvalidDataException("Unknown dependency: "+name);
        if(!dependency.Required)return new(null,false);
        CheckLoaderConflict(game,name=="MelonLoader");
        var work=Path.Combine(stateRoot,"staging",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        try
        {
            var downloads=new ContentStore(dependenciesRoot??contentRoot,Path.Combine(stateRoot,"cache"));
            if(name==".NET 6 Desktop x64")
            {
                var file=await downloads.Get(Data.Read<PackageFile>(Path.Combine(dependenciesRoot??contentRoot,"runtime.json")),log,ct);
                Games.RequireClosed();
                var restart=await SetupRuntime(file,log,ct);
                return new(null,true,restart);
            }
            if(!dependency.Automatic&&localArchive is null)throw new IOException("Select the downloaded dependency archive: "+name);
            var incoming=await PrepareDependency(dependency,downloads,work,localArchive,log,ct);
            ct.ThrowIfCancellationRequested();Games.RequireClosed();
            if(!Games.Inspect(game).Hash.Equals(info.Hash,StringComparison.OrdinalIgnoreCase))throw new IOException("The game was updated while preparing this installation. Check compatibility again.");
            var aliases=name=="Custom Leaders"?new[]{"Mods/MenaceCustomLeader.dll","Mods/CustomLeaderMod.dll"}.Where(p=>!incoming.ContainsKey(p)&&File.Exists(Data.Inside(game,p))).ToArray():[];
            var backup=new FileTransaction(game,Path.Combine(stateRoot,"backups")).Apply(incoming,aliases,log);
            Data.Write(Path.Combine(stateRoot,"last-install.json"),new {game,backup});
            return new(backup,true);
        }
        finally {CleanStaging(work);}
    }
    private async Task<Dictionary<string,string>> PrepareDependency(DependencyStatus dependency,ContentStore downloads,string work,string? local,IProgress<string>? log,CancellationToken ct)
    {
        var name=dependency.Name;var incoming=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(name=="MelonLoader")
        {
            var zip=local??await downloads.Get(Melon,log,ct);var folder=Path.Combine(work,"melon");
            Data.Extract(zip,folder,n=>n.StartsWith("MelonLoader/",StringComparison.Ordinal)||n is "version.dll" or "dobby.dll");
            var dll=Data.Inside(folder,"MelonLoader/net6/MelonLoader.dll");
            if(!File.Exists(Path.Combine(folder,"version.dll"))||!AtLeast(Games.Version(dll),dependency.Explanation.Replace(">= ","")))throw new InvalidDataException("The selected archive does not contain a suitable MelonLoader installation.");
            foreach(var file in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))incoming.Add(Path.GetRelativePath(folder,file).Replace('\\','/'),file);
        }
        else if(name=="Jiangyu Loader")
        {
            var dll=local is null?await downloads.Get(Jiangyu,log,ct):ReadModDll(local,Path.Combine(work,"mod"),["Jiangyu.Loader.dll"]);
            if(!AtLeast(Games.Version(dll),dependency.Explanation.Replace(">= ","")))throw new InvalidDataException("The selected archive does not contain the required dependency version: "+name);
            incoming.Add("Mods/Jiangyu.Loader.dll",dll);
        }
        else if(name is "Custom Leaders" or "All Leaders Pickable")
        {
            if(local is null)throw new IOException("Select the downloaded dependency archive: "+name);
            var dll=ReadModDll(local,Path.Combine(work,"mod"),name=="Custom Leaders"?["MenaceCustomLeader.dll","CustomLeaderMod.dll"]:["AllLeadersPickable.dll"]);
            var pinned=name=="Custom Leaders"&&Data.Hash(dll)=="29BEF52D317FBA7985A1C92939EAF332659C849B5E25828DAF5E5B8BFEAC2AEA"&&Version.Parse(dependency.Explanation.Replace(">= ",""))<=new Version(0,2,4,1);
            if(!pinned&&!AtLeast(Games.Version(dll),dependency.Explanation.Replace(">= ","")))throw new InvalidDataException("The selected archive does not contain the required dependency version: "+name);
            // Installing a loader must not import the example characters bundled with its release.
            incoming.Add("Mods/"+Path.GetFileName(dll),dll);
        }
        else throw new InvalidDataException("Unknown dependency: "+name);
        return incoming;
    }
    private static string ReadModDll(string archive,string output,string[] names)
    {
        Directory.CreateDirectory(output);
        if(Path.GetExtension(archive).Equals(".dll",StringComparison.OrdinalIgnoreCase))
        {
            var name=Path.GetFileName(archive);if(!names.Contains(name,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Wrong dependency file: "+name);
            var destination=Data.Inside(output,name);File.Copy(archive,destination);return destination;
        }
        if(!Path.GetExtension(archive).Equals(".zip",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Select a ZIP or DLL dependency file.");
        using var zip=ZipFile.OpenRead(archive);
        if(zip.Entries.Count>20000||zip.Entries.Sum(e=>e.Length)>2L*1024*1024*1024)throw new InvalidDataException("Archive exceeds installation limits");
        // Validate every path, even ignored examples and unrelated files.
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries.Where(e=>!e.FullName.EndsWith('/')))
            if(!paths.Add(Data.Inside(output,entry.FullName.Replace('\\','/'))))throw new InvalidDataException("Duplicate archive entry: "+entry.FullName);
        var dlls=zip.Entries.Where(e=>names.Contains(Path.GetFileName(e.FullName.Replace('\\','/')),StringComparer.OrdinalIgnoreCase)).ToArray();
        if(dlls.Length!=1)throw new InvalidDataException("The archive must contain exactly one matching dependency DLL.");
        var target=Data.Inside(output,Path.GetFileName(dlls[0].FullName.Replace('\\','/')));dlls[0].ExtractToFile(target);return target;
    }
    private void CleanStaging(string work)
    {
        var safe=Data.Inside(Path.Combine(stateRoot,"staging"),Path.GetFileName(work));
        try{if(Directory.Exists(safe))Directory.Delete(safe,true);}catch(IOException){ }
    }
}
