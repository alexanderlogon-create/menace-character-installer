using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;

namespace MenaceCharacterInstaller;

public sealed record PackageFile(string Path, string Sha256, long Size, string? Url = null);
public sealed record Leader(string Id, string LeaderId, string Name, string FullName, string Role, string Description,
    string Portrait, Dictionary<string,int> Attributes, PackageFile Package, bool IsClone=true, string[]? Requires=null);
public sealed record Catalog(int SchemaVersion, string Name, string Version, string Module, string GameAssemblySha256,
    string TestedGame, PackageFile Shared, List<Leader> Characters, string Format="Jiangyu", string[]? Dependencies=null,
    string[]? Notices=null, bool Imported=false);
public static class Data
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(path);
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { JsonSerializer.Serialize(stream,value,Json);stream.Flush(true); }
            File.Move(temp,path,true);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    public static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
    public static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/','\\').Any(p => p is ".." or "." or "")) throw new InvalidDataException("Unsafe relative path: " + relative);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes root");
        RejectLinks(root, full);
        return full;
    }
    public static void RejectLinks(string root, string full)
    {
        // Existing parent junctions/symlinks must not redirect installation or rollback.
        for(var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)!)
        {
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links/junctions are not supported: " + p);
            if (Path.GetFullPath(p).Equals(Path.GetPathRoot(p), StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    public static void Extract(string archive, string root, Func<string,bool>? allow = null)
    {
        using var zip = ZipFile.OpenRead(archive);
        if(zip.Entries.Count > 20000 || zip.Entries.Sum(e=>e.Length) > 2L*1024*1024*1024)throw new InvalidDataException("Archive exceeds installation limits");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\','/');
            if(name.EndsWith('/'))continue;
            var path = Inside(root, name);
            if(!paths.Add(path))throw new InvalidDataException("Duplicate archive entry: " + name);
            if(allow is not null && !allow(name))throw new InvalidDataException("Unexpected archive entry: " + name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
        }
    }
}
public sealed class ContentStore(string root, string cache)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    public async Task<string> Get(PackageFile file, IProgress<string>? log, CancellationToken ct)
    {
        var bundled = Data.Inside(root, file.Path);
        var cached = Data.Inside(cache, file.Path);
        foreach(var path in new[]{bundled,cached}) if(File.Exists(path) && new FileInfo(path).Length == file.Size && Data.Hash(path).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))return path;
        if(file.Url is null)throw new FileNotFoundException("Missing or damaged content. Extract the complete installer ZIP again: " + file.Path);
        var uri = new Uri(file.Url);
        if(uri.Scheme != "https")throw new InvalidDataException("HTTPS download required");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        var temp = cached + "." + Guid.NewGuid().ToString("N") + ".partial";
        log?.Report("Downloading " + file.Path);
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using(var input = await response.Content.ReadAsStreamAsync(ct))
            await using(var output = File.Create(temp))
            {
                var buffer = new byte[131072]; long total = 0; int n;
                while((n = await input.ReadAsync(buffer,ct)) > 0)
                {
                    total += n; if(total > file.Size)throw new InvalidDataException("Download is larger than expected");
                    await output.WriteAsync(buffer.AsMemory(0,n),ct);
                }
            }
            if(new FileInfo(temp).Length != file.Size || !Data.Hash(temp).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Download integrity check failed: " + file.Path);
            File.Move(temp,cached,true); return cached;
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
}
public sealed class PackAssembler(Catalog catalog, ContentStore content)
{
    public async Task<string> Build(IReadOnlyCollection<string> ids, string destination, IProgress<string>? log, CancellationToken ct)
    {
        if(ids.Count==0 || ids.Distinct().Count()!=ids.Count || ids.Any(id=>!catalog.Characters.Any(c=>c.Id==id)))throw new InvalidDataException("Choose at least one known character");
        foreach(var c in catalog.Characters.Where(c=>ids.Contains(c.Id)))
            if(c.Requires?.Any(id=>!ids.Contains(id))==true)throw new InvalidDataException("Linked characters must be selected together: "+c.Name);
        if(catalog.Format=="CustomLeaders")return await BuildCustom(ids,destination,log,ct);
        if(Directory.Exists(destination))throw new IOException("Build destination already exists");
        Directory.CreateDirectory(destination);
        var scratch=Path.Combine(destination,".parts"); Directory.CreateDirectory(scratch);
        var manifest = new JsonObject(); var patches=new JsonArray(); var clones=new JsonArray(); var prefabs=new JsonArray();
        var allCloneIds=new HashSet<string>(); var selected=catalog.Characters.Where(c=>ids.Contains(c.Id)).ToArray();
        var pieces=new[]{catalog.Shared}.Concat(selected.Select(c=>c.Package)).DistinctBy(p=>p.Path).ToArray();
        int part=0;
        foreach(var piece in pieces)
        {
            ct.ThrowIfCancellationRequested(); part++; log?.Report("Preparing " + part + "/" + pieces.Length);
            var folder=Path.Combine(scratch,part.ToString());
            Data.Extract(await content.Get(piece,log,ct),folder,n=>n is "manifest.json" or "templates.json" or "prefabs.json" || n.StartsWith("bundles/") || n.StartsWith("locales/"));
            var mf=Path.Combine(folder,"manifest.json"); if(File.Exists(mf))manifest=JsonNode.Parse(File.ReadAllText(mf))!.AsObject();
            var tf=Path.Combine(folder,"templates.json");
            if(File.Exists(tf))
            {
                var defs=JsonNode.Parse(File.ReadAllText(tf))!;
                foreach(var node in defs["templateClones"]!.AsArray())
                {
                    var key=node!["templateType"]+"|"+node["cloneId"];
                    if(!allCloneIds.Add(key))throw new InvalidDataException("Duplicate clone: "+key);
                    clones.Add(node.DeepClone());
                }
                foreach(var node in defs["templatePatches"]!.AsArray())patches.Add(node!.DeepClone());
            }
            var pf=Path.Combine(folder,"prefabs.json");if(File.Exists(pf))foreach(var node in JsonNode.Parse(File.ReadAllText(pf))!.AsArray())prefabs.Add(node!.DeepClone());
            var bundles=Path.Combine(folder,"bundles");
            if(Directory.Exists(bundles))foreach(var f in Directory.GetFiles(bundles,"*",SearchOption.AllDirectories))
            {
                var dest=Data.Inside(destination,"bundles/"+Path.GetRelativePath(bundles,f)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(f,dest,false);
            }
            var locales=Path.Combine(folder,"locales");
            if(Directory.Exists(locales))foreach(var f in Directory.GetFiles(locales,"*",SearchOption.AllDirectories))
            {var dest=Data.Inside(destination,"locales/"+Path.GetRelativePath(locales,f));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(f,dest,false);}
        }
        var actual=clones.Where(c=>c!["templateType"]!.GetValue<string>()=="UnitLeaderTemplate").Select(c=>c!["cloneId"]!.GetValue<string>()).ToHashSet();
        if(!actual.SetEquals(selected.Where(c=>c.IsClone).Select(c=>c.LeaderId)))throw new InvalidDataException("Selected roster differs from installed templates");
        // Older character archives omit dossier pools. Register only selected clones,
        // preserving explicit registrations and every existing vanilla candidate.
        var registered=patches.Where(p=>p!["templateType"]!.GetValue<string>()=="DossierItemTemplate")
            .SelectMany(p=>p!["set"]!.AsArray()).Where(o=>o?["fieldPath"]?.GetValue<string>()=="m_UnlockedLeaders"&&o?["value"]?["kind"]?.GetValue<string>()=="TemplateReference")
            .Select(o=>o!["value"]!["reference"]!["templateId"]!.GetValue<string>()).ToHashSet();
        foreach(var leader in clones.Where(c=>c!["templateType"]!.GetValue<string>()=="UnitLeaderTemplate"))
        {
            var id=leader!["cloneId"]!.GetValue<string>();var source=leader["sourceId"]!.GetValue<string>();
            var dossier=source.StartsWith("squad_leader.")?"dossier.squad_leader":source.StartsWith("pilot.")?"dossier.pilot":null;
            if(dossier is null||!registered.Add(id))continue;
            patches.Add(new JsonObject{["templateType"]="DossierItemTemplate",["templateId"]=dossier,["set"]=new JsonArray(new JsonObject{
                ["op"]="Append",["fieldPath"]="m_UnlockedLeaders",["value"]=new JsonObject{["kind"]="TemplateReference",["reference"]=new JsonObject{["templateType"]="UnitLeaderTemplate",["templateId"]=id}}
            })});
        }
        foreach(var group in patches.Where(p=>p!["templateType"]!.GetValue<string>()=="DossierItemTemplate").GroupBy(p=>p!["templateId"]!.GetValue<string>()).Where(g=>g.Count()>1).ToArray())
        {
            var merged=new JsonArray(group.SelectMany(p=>p!["set"]!.AsArray()).Select(o=>o!.DeepClone()).ToArray());
            var first=group.First()!;first["set"]=merged;
            foreach(var duplicate in group.Skip(1).ToArray())patches.Remove(duplicate);
        }
        // Append only this selection; retain vanilla starters and the number the player can choose.
        if(selected.Any(c=>c.IsClone))patches.Add(new JsonObject { ["templateType"]="StrategyConfig",["templateId"]="strategy_config",["set"]=new JsonArray(selected.Where(c=>c.IsClone).Select(c=>(JsonNode)new JsonObject{
            ["op"]="Append",["fieldPath"]="InitialPickableUnitLeaders",["value"]=new JsonObject{
                ["kind"]="TemplateReference",["reference"]=new JsonObject{["templateType"]="UnitLeaderTemplate",["templateId"]=c.LeaderId}}
        }).ToArray())});
        manifest["name"]=catalog.Module;manifest["version"]=catalog.Version;manifest["additionPrefabs"]=prefabs;
        manifest["description"]=catalog.Name+" - selected characters";
        File.WriteAllText(Path.Combine(destination,"jiangyu.json"),manifest.ToJsonString(Data.Json));
        File.WriteAllText(Path.Combine(destination,"templates.json"),new JsonObject{["templatePatches"]=patches,["templateClones"]=clones}.ToJsonString(Data.Json));
        Data.Write(Path.Combine(destination,"installer-selection.json"),new Selection(catalog.Module,catalog.Version,selected.Select(c=>c.Id).ToArray()));
        File.WriteAllText(Path.Combine(destination,"README.txt"),"MENACE Character Installer\n"+catalog.Name+" "+catalog.Version+"\nSelected: "+string.Join(", ",selected.Select(c=>c.Name))+"\nKeep these character IDs for existing campaigns. Refer to the manifest for additional pack dependencies.\n");
        Directory.Delete(scratch,true);
        return destination;
    }
    private async Task<string> BuildCustom(IReadOnlyCollection<string> ids,string destination,IProgress<string>? log,CancellationToken ct)
    {
        if(Directory.Exists(destination))throw new IOException("Build destination already exists");Directory.CreateDirectory(destination);
        foreach(var c in catalog.Characters.Where(c=>ids.Contains(c.Id)))
        {
            ct.ThrowIfCancellationRequested();log?.Report("Preparing " + c.Name);
            Data.Extract(await content.Get(c.Package,log,ct),destination,n=>n.StartsWith("customleaders/",StringComparison.Ordinal));
        }
        Data.Write(Path.Combine(destination,"installer-selection.json"),new Selection(catalog.Module,catalog.Version,ids.ToArray()));
        return destination;
    }
}
public sealed record Selection(string Module,string Version,string[] CharacterIds);
