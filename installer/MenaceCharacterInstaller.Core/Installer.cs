using System.Diagnostics;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace MenaceCharacterInstaller;

public sealed record DependencyStatus(string Name,string Installed,bool Required,string Explanation,string DownloadPage="",bool Automatic=true);
public sealed record CustomSelection(string Module,string Version,string[] CharacterIds,string[] Files);
public sealed partial class Installer(Catalog catalog,string contentRoot,string stateRoot,string? dependenciesRoot=null,IRuntimeSetup? runtimeSetup=null)
{
    public static readonly PackageFile Melon = new("downloads/MelonLoader.x64.zip","5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416",20155622,"https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip");
    public static readonly PackageFile Jiangyu = new("downloads/Jiangyu.Loader.dll","16D8213CA30C8FAE5097AEA8C0E27300E5E2CBA7F28BB6D2AE468765A03787B3",1246720,"https://github.com/antistrategie/jiangyu/releases/download/v1.4.7/Jiangyu.Loader.dll");
    public static string RuntimeVersion()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","shared","Microsoft.WindowsDesktop.App");
        if(!Directory.Exists(root))return "Not installed";
        return Directory.GetDirectories(root).Select(Path.GetFileName).Where(s=>Version.TryParse(s,out var v)&&v.Major==6&&v.Minor==0&&v.Build>=15).OrderByDescending(s=>Version.Parse(s!)).FirstOrDefault()??"Not installed";
    }
    private static bool AtLeast(string s,string version)=>Version.TryParse(s.Split('-')[0],out var v)&&v>=Version.Parse(version);
    public static DependencyStatus[] Dependencies(string game,Catalog? catalog=null)
    {
        var melon=Games.Version(Path.Combine(game,"MelonLoader","net6","MelonLoader.dll"));var runtime=RuntimeVersion();
        var result=new List<DependencyStatus>{new("MelonLoader",melon,!AtLeast(melon,"0.7.3")||!File.Exists(Path.Combine(game,"version.dll")),">= 0.7.3","https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3")};
        var declarations=(catalog?.Dependencies??[]).ToList();
        if(catalog?.Format=="CustomLeaders"){if(!declarations.Any(d=>d.StartsWith("Custom",StringComparison.OrdinalIgnoreCase)))declarations.Add("Custom Leaders >= 0.2.4.1");}
        else if(!declarations.Any(d=>d.StartsWith("Jiangyu",StringComparison.OrdinalIgnoreCase)))declarations.Add("Jiangyu >= 1.4.7");
        foreach(var declaration in declarations.Distinct())
        {
            var normalized=Regex.Replace(declaration,@"[\s_\-.]","").ToLowerInvariant();
            var minimum=Regex.Match(declaration,@"\d+\.\d+\.\d+(?:\.\d+)?").Value;
            if(normalized.StartsWith("jiangyu"))
            {
                if(minimum.Length==0||Version.Parse(minimum)<new Version(1,4,7))minimum="1.4.7";
                var version=Games.Version(Path.Combine(game,"Mods","Jiangyu.Loader.dll"));
                result.Add(new("Jiangyu Loader",version,!AtLeast(version,minimum),">= "+minimum,"https://github.com/antistrategie/jiangyu/releases",Version.Parse(minimum)<=new Version(1,4,7)));
            }
            else if(normalized.StartsWith("customleader"))
            {
                if(minimum.Length==0)minimum="0.2.4.1";
                var dll=new[]{"MenaceCustomLeader.dll","CustomLeaderMod.dll"}.Select(n=>Path.Combine(game,"Mods",n)).FirstOrDefault(File.Exists);
                var version=dll is null?"Not installed":Games.Version(dll);
                // The original release's ProductVersion does not consistently expose all four components.
                var pinned=dll is not null&&Data.Hash(dll)=="29BEF52D317FBA7985A1C92939EAF332659C849B5E25828DAF5E5B8BFEAC2AEA"&&Version.Parse(minimum)<=new Version(0,2,4,1);
                result.Add(new("Custom Leaders",pinned?"0.2.4.1":version,!pinned&&!AtLeast(version,minimum),">= "+minimum,"https://www.nexusmods.com/menace/mods/18",false));
            }
            else if(normalized.StartsWith("allleaderspickable"))
            {
                if(minimum.Length==0)minimum="1.0.0";
                var version=Games.Version(Path.Combine(game,"Mods","AllLeadersPickable.dll"));
                result.Add(new("All Leaders Pickable",version,!AtLeast(version,minimum),">= "+minimum,"https://www.nexusmods.com/menace/mods/16",false));
            }
            else if(normalized.StartsWith("melonloader"))
            {
                if(minimum.Length>0 && !AtLeast(melon,minimum))result[0]=result[0] with {Required=true,Explanation=">= "+minimum,Automatic=Version.Parse(minimum)<=new Version(0,7,3),DownloadPage="https://github.com/LavaGang/MelonLoader/releases"};
            }
            else throw new InvalidDataException("Unsupported pack dependency: "+declaration+". Install this pack using its author's instructions.");
        }
        result.Add(new(".NET 6 Desktop x64",runtime,runtime=="Not installed",">= 6.0.15","https://dotnet.microsoft.com/en-us/download/dotnet/6.0"));
        return result.GroupBy(d=>d.Name).Select(g=>g.OrderByDescending(d=>Version.TryParse(d.Explanation.Replace(">= ",""),out var v)?v:new Version()).First() with {Required=g.Any(d=>d.Required),Automatic=g.All(d=>d.Automatic)}).ToArray();
    }
    public async Task<string> Install(string game,IReadOnlyCollection<string> selected,bool allowUnverified,IProgress<string>? log,CancellationToken ct)
    {
        var info=Games.Inspect(game);game=info.Path;Games.RequireClosed();Games.CheckDuplicates(game,catalog);
        if(!allowUnverified&&!info.Hash.Equals(catalog.GameAssemblySha256,StringComparison.OrdinalIgnoreCase))throw new IOException("This game build has not been verified. Review the compatibility notice before continuing.");
        var status=CurrentDependencies(game);
        var manual=status.Where(d=>d.Required&&!d.Automatic).ToArray();
        if(manual.Length>0)throw new IOException("Install missing dependencies using their Install buttons: "+string.Join(", ",manual.Select(d=>d.Name)));
        CheckLoaderConflict(game,status[0].Required);
        var cache=Path.Combine(stateRoot,"cache");var store=new ContentStore(contentRoot,Path.Combine(cache,catalog.Module));
        var downloads=new ContentStore(dependenciesRoot??contentRoot,cache);
        var work=Path.Combine(stateRoot,"staging",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        try
        {
            var pack=await new PackAssembler(catalog,store).Build(selected,Path.Combine(work,"pack"),log,ct);
            var incoming=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in Directory.GetFiles(pack,"*",SearchOption.AllDirectories))
            {
                var rel=Path.GetRelativePath(pack,file).Replace('\\','/');
                if(catalog.Format=="CustomLeaders"&&rel=="installer-selection.json")continue;
                incoming.Add(catalog.Format=="CustomLeaders"?"Mods/"+rel:"Mods/"+catalog.Module+"/"+rel,file);
            }
            if(status[0].Required)
            {
                var folder=Path.Combine(work,"melon");Data.Extract(await downloads.Get(Melon,log,ct),folder,n=>n.StartsWith("MelonLoader/",StringComparison.Ordinal)||n is "version.dll" or "dobby.dll");
                foreach(var file in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))incoming.Add(Path.GetRelativePath(folder,file).Replace('\\','/'),file);
            }
            if(status.Any(d=>d.Name=="Jiangyu Loader"&&d.Required))incoming.Add("Mods/Jiangyu.Loader.dll",await downloads.Get(Jiangyu,log,ct));
            // Runtime installer is downloaded and verified before touching game files.
            string? runtime=null;
            if(status.Any(d=>d.Name==".NET 6 Desktop x64"&&d.Required))runtime=await downloads.Get(Data.Read<PackageFile>(Path.Combine(dependenciesRoot??contentRoot,"runtime.json")),log,ct);
            ct.ThrowIfCancellationRequested();Games.RequireClosed();
            if(runtime is not null)
            {
                if(await SetupRuntime(runtime,log,ct))throw new IOException("Restart Windows to finish .NET installation, then retry the character installation.");
            }
            ct.ThrowIfCancellationRequested();
            if(!Games.Inspect(game).Hash.Equals(info.Hash,StringComparison.OrdinalIgnoreCase))throw new IOException("The game was updated while preparing this installation. Check compatibility again.");
            var existing=Path.Combine(game,"Mods",catalog.Module);
            var remove=Directory.Exists(existing)?Directory.GetFiles(existing,"*",SearchOption.AllDirectories).Select(f=>Path.GetRelativePath(game,f).Replace('\\','/')).Where(f=>!incoming.ContainsKey(f)).ToArray():[];
            if(catalog.Format=="CustomLeaders")
            {
                var ledger="Mods/menace-character-installer-"+catalog.Module+".json";var previous=Data.Inside(game,ledger);
                remove=File.Exists(previous)?Data.Read<CustomSelection>(previous).Files.Where(f=>!incoming.ContainsKey(f)).ToArray():[];
                if(remove.Any(f=>!f.StartsWith("Mods/customleaders/",StringComparison.Ordinal)))throw new InvalidDataException("Invalid selection ledger");
                var source=Path.Combine(work,"selection.json");Data.Write(source,new CustomSelection(catalog.Module,catalog.Version,selected.ToArray(),incoming.Keys.Where(k=>k.StartsWith("Mods/customleaders/")).ToArray()));incoming.Add(ledger,source);
            }
            var backup=new FileTransaction(game,Path.Combine(stateRoot,"backups")).Apply(incoming,remove,log);
            Data.Write(Path.Combine(stateRoot,"last-install.json"),new {game,backup});
            log?.Report("Installed "+selected.Count+" characters. Backup: "+backup);
            return backup;
        }
        finally
        {
            // This random directory is owned solely by this operation.
            CleanStaging(work);
        }
    }
    public string? LatestBackup(string game)
    {
        var root=Path.Combine(stateRoot,"backups");if(!Directory.Exists(root))return null;
        return Directory.GetDirectories(root).OrderByDescending(p=>p,StringComparer.Ordinal).FirstOrDefault(p=>{
            try {var j=Data.Read<InstallJournal>(Path.Combine(p,"transaction.json"));return j.Game.Equals(Path.GetFullPath(game),StringComparison.OrdinalIgnoreCase)&&j.Status is "Complete" or "Applying";}catch{return false;}
        });
    }
}
