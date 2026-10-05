using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace MenaceCharacterInstaller;
public sealed record ImportedPack(Catalog Catalog,string Root,string Archive);
public static class PackImport
{
    static string Id(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    static string S(JsonNode? n,string key,string fallback="")=>n?[key]?.GetValue<string>()??fallback;
    public static ImportedPack Open(string archive,string imports,CancellationToken ct)
    {
        if(!Path.GetExtension(archive).Equals(".zip",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Only ZIP character packs are supported.");
        var root=Path.Combine(imports,Guid.NewGuid().ToString("N"));var raw=Path.Combine(root,"source");
        ct.ThrowIfCancellationRequested();Data.Extract(archive,raw);ct.ThrowIfCancellationRequested();
        if(Directory.GetFiles(raw,"*",SearchOption.AllDirectories).Any(p=>Path.GetExtension(p).Equals(".dll",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(p).Equals(".exe",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Code-based packs cannot be split safely by this installer.");
        var manifests=Directory.GetFiles(raw,"jiangyu.json",SearchOption.AllDirectories);
        Catalog catalog;
        if(manifests.Length==1)catalog=Jiangyu(Path.GetDirectoryName(manifests[0])!,root,archive,ct);
        else if(manifests.Length>1)throw new InvalidDataException("This archive contains several Jiangyu modules. Open a single character-pack module.");
        else catalog=Custom(raw,root,archive,ct);
        Data.Write(Path.Combine(root,"catalog.json"),catalog);return new(catalog,root,Path.GetFullPath(archive));
    }
    static PackageFile Zip(string root,string name,Dictionary<string,string> paths,Dictionary<string,JsonNode> json)
    {
        var rel="packages/"+name+".zip";var dest=Data.Inside(root,rel);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using(var zip=ZipFile.Open(dest,ZipArchiveMode.Create))
        {
            foreach(var (entry,path) in paths)zip.CreateEntryFromFile(path,entry,CompressionLevel.Fastest);
            foreach(var (entry,node) in json){using var writer=new StreamWriter(zip.CreateEntry(entry,CompressionLevel.Fastest).Open(),new UTF8Encoding(false));writer.Write(node.ToJsonString(Data.Json));}
        }
        return new(rel,Data.Hash(dest),new FileInfo(dest).Length);
    }
    static Catalog Custom(string raw,string root,string archive,CancellationToken ct)
    {
        var configs=Directory.GetFiles(raw,"*.json",SearchOption.AllDirectories).Where(f=>Regex.IsMatch(Path.GetFileName(f),@"_(clone|replace)\.json$",RegexOptions.IgnoreCase)).ToArray();
        if(configs.Length==0)throw new InvalidDataException("No Jiangyu or Custom Leaders character definitions were found in this ZIP.");
        var characters=new List<Leader>();var folders=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var notices=new List<string>();
        foreach(var file in configs)
        {
            ct.ThrowIfCancellationRequested();var node=JsonNode.Parse(File.ReadAllText(file))!;var slug=Regex.Replace(Path.GetFileNameWithoutExtension(file),"_(clone|replace)$","",RegexOptions.IgnoreCase);
            if(!Regex.IsMatch(slug,@"^[a-zA-Z0-9_\-]+$")||!folders.Add(slug))throw new InvalidDataException("Duplicate or unsafe character folder: "+slug);
            if(string.IsNullOrWhiteSpace(S(node,"clone_from")))throw new InvalidDataException("Missing clone_from: "+slug);
            var folder=Path.GetDirectoryName(file)!;var paths=new Dictionary<string,string>();
            foreach(var path in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))
            {
                if(Path.GetExtension(path).Equals(".dll",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Executable files inside a character folder are unsupported.");
                paths["customleaders/"+slug+"/"+Path.GetRelativePath(folder,path).Replace('\\','/')]=path;
            }
            var png=Directory.GetFiles(folder,"*.png",SearchOption.AllDirectories).FirstOrDefault(f=>Path.GetFileName(f).Equals(slug+"_162x162.png",StringComparison.OrdinalIgnoreCase))??Directory.GetFiles(folder,"*.png",SearchOption.AllDirectories).FirstOrDefault(f=>!f.Contains("inactive")&&!f.Contains("injured"));
            var portrait="";if(png is not null){portrait="portraits/"+Id(slug)+".png";Directory.CreateDirectory(Path.Combine(root,"portraits"));File.Copy(png,Data.Inside(root,portrait));}
            var attr=node["attributes"]?.AsObject().ToDictionary(k=>k.Key,k=>k.Value!.GetValue<int>())??[];
            var clone=file.EndsWith("_clone.json",StringComparison.OrdinalIgnoreCase);
            characters.Add(new(slug,S(node,"clone_from"),S(node,"nickname",S(node,"clone_from")),string.Join(" ",new[]{S(node,"forename"),S(node,"surname")}.Where(s=>s.Length>0)),S(node,"title"),string.Join("\n\n",new[]{S(node,"description"),S(node,"unit_description")}.Where(s=>s.Length>0)),portrait,attr,Zip(root,Id(slug),paths,[]),clone));
            if(File.Exists(Path.Combine(folder,"voice_manifest.json")))notices.Add("legacy_voice");
        }
        var studio=Directory.GetFiles(raw,"menace-pack-studio-manifest.json",SearchOption.AllDirectories).FirstOrDefault();var metadata=studio is null?null:JsonNode.Parse(File.ReadAllText(studio));
        var projectId=S(metadata,"projectId");
        var module="custompack_"+Id(projectId.Length>0?projectId:string.Join("|",characters.Select(c=>c.Id).Order()));
        var shared=Zip(root,"shared",[],[]);
        return new(1,S(metadata,"name",Path.GetFileNameWithoutExtension(archive)),"imported",module,"","Not verified",shared,characters,"CustomLeaders",["Custom Leaders >= 0.2.4.1"],notices.Distinct().Append("imported_compatibility").ToArray(),true);
    }
    static Catalog Jiangyu(string source,string root,string archive,CancellationToken ct)
    {
        var mf=JsonNode.Parse(File.ReadAllText(Path.Combine(source,"jiangyu.json")))!.AsObject();var module=S(mf,"name");
        if(!Regex.IsMatch(module,@"^[a-zA-Z0-9_\-.]+$")||module is "." or "..")throw new InvalidDataException("Unsafe Jiangyu module ID");
        if(Directory.GetFiles(source,"*.dll",SearchOption.AllDirectories).Length>0||Directory.GetFiles(source,"*.exe",SearchOption.AllDirectories).Length>0)throw new InvalidDataException("Code-based packs cannot be split safely by this installer.");
        if(mf["textureReplacements"]?.AsArray().Count>0 || mf["meshes"]?.AsObject().Count>0)throw new InvalidDataException("This pack contains global mesh/texture replacements that cannot be assigned safely to individual characters.");
        var defs=JsonNode.Parse(File.ReadAllText(Path.Combine(source,"templates.json")))!;
        var clones=defs["templateClones"]!.AsArray().Select(n=>n!).ToArray();var patches=defs["templatePatches"]!.AsArray().Select(n=>n!).ToArray();
        // A dossier is a candidate pool, not a dependency requiring the whole roster.
        // Separate only direct Append operations owned by a cloned leader; retain all other edits.
        var clonedLeaders=clones.Where(n=>S(n,"templateType")=="UnitLeaderTemplate").Select(n=>S(n,"cloneId")).ToHashSet();
        var recruitment=new Dictionary<string,List<JsonNode>>();
        foreach(var patch in patches.Where(n=>S(n,"templateType")=="DossierItemTemplate"))
        {
            var remainder=new List<JsonNode>();
            foreach(var op in Ops(patch))
            {
                var reference=op["value"]?["reference"];
                var id=S(reference,"templateId");
                if(S(op,"op")=="Append"&&S(op,"fieldPath")=="m_UnlockedLeaders"&&S(op["value"],"kind")=="TemplateReference"&&S(reference,"templateType")=="UnitLeaderTemplate"&&clonedLeaders.Contains(id))
                {
                    if(!recruitment.TryGetValue(id,out var owned))recruitment[id]=owned=[];
                    owned.Add(new JsonObject{["templateType"]="DossierItemTemplate",["templateId"]=S(patch,"templateId"),["set"]=new JsonArray(op.DeepClone())});
                }
                else remainder.Add(op.DeepClone());
            }
            patch["set"]=new JsonArray(remainder.ToArray());
        }
        string Key(JsonNode n,bool clone=false)=>S(n,"templateType")+"|"+S(n,clone?"cloneId":"templateId");
        var nodes=clones.Concat(patches).GroupBy(n=>Key(n,n["cloneId"] is not null)).ToDictionary(g=>g.Key,g=>g.ToArray());
        var leaders=nodes.Keys.Where(k=>k.StartsWith("UnitLeaderTemplate|",StringComparison.Ordinal)).ToArray();
        if(leaders.Length==0)throw new InvalidDataException("This Jiangyu module has no selectable UnitLeaderTemplate definitions.");
        var graph=nodes.Keys.ToDictionary(k=>k,k=>new HashSet<string>());
        foreach(var (key,values) in nodes)
        foreach(var n in values)
        {
            foreach(var reference in References(n))
            {
                var other=S(reference,"templateType")+"|"+S(reference,"templateId");
                if(other==key||!nodes.ContainsKey(other))continue;
                // StrategyConfig is processed explicitly below, not a dependency connecting every leader.
                if(key.StartsWith("StrategyConfig|"))continue;
                graph[key].Add(other);graph[other].Add(key);
            }
            var sourceKey=S(n,"templateType")+"|"+S(n,"sourceId");
            if(nodes.ContainsKey(sourceKey)&&sourceKey!=key){graph[key].Add(sourceKey);graph[sourceKey].Add(key);}
        }
        var components=new Dictionary<string,HashSet<string>>();
        foreach(var leader in leaders)
        {
            var visited=new HashSet<string>();var queue=new Queue<string>();queue.Enqueue(leader);
            while(queue.TryDequeue(out var key)){if(!visited.Add(key))continue;foreach(var next in graph[key])queue.Enqueue(next);}
            components[leader]=visited;
        }
        var owners=nodes.Keys.ToDictionary(k=>k,k=>leaders.Where(l=>components[l].Contains(k)).ToArray());
        var sharedPaths=Directory.GetFiles(source,"*",SearchOption.AllDirectories).Where(f=>Path.GetRelativePath(source,f).Replace('\\','/').StartsWith("bundles/")||Path.GetRelativePath(source,f).Replace('\\','/').StartsWith("locales/")).ToDictionary(f=>Path.GetRelativePath(source,f).Replace('\\','/'),f=>f);
        var pictures=BundlePortraits.Read(sharedPaths.Where(k=>k.Key.EndsWith(".bundle")).Select(k=>k.Value),Path.Combine(root,"portraits"),ct);
        var chars=new List<Leader>();var componentPackages=new Dictionary<string,PackageFile>();
        foreach(var leader in leaders)
        {
            ct.ThrowIfCancellationRequested();var component=components[leader];var group=leaders.Where(l=>component.Contains(l)).Order().ToArray();var groupId=Id(string.Join("|",group));
            if(!componentPackages.TryGetValue(groupId,out var package))
            {
                var templates=new JsonObject{["templateClones"]=new JsonArray(clones.Where(n=>component.Contains(Key(n,true))).Select(n=>n.DeepClone()).ToArray()),["templatePatches"]=new JsonArray(patches.Where(n=>component.Contains(Key(n))).Select(n=>n.DeepClone()).ToArray())};
                foreach(var member in group)
                    if(recruitment.TryGetValue(member.Split('|',2)[1],out var registrations))
                        foreach(var registration in registrations)templates["templatePatches"]!.AsArray().Add(registration.DeepClone());
                package=Zip(root,groupId,[],new(){{"templates.json",templates}});componentPackages[groupId]=package;
            }
            var lp=patches.FirstOrDefault(n=>Key(n)==leader);var speakerRef=Ops(lp).FirstOrDefault(n=>S(n,"fieldPath")=="SpeakerTemplate")?["value"]?["reference"];
            var speakerKey="SpeakerTemplate|"+S(speakerRef,"templateId");var sp=patches.FirstOrDefault(n=>Key(n)==speakerKey);
            string Text(JsonNode? patch,string field)=>Ops(patch).LastOrDefault(n=>S(n,"fieldPath")==field||n["descent"]?.AsArray().FirstOrDefault()?["field"]?.GetValue<string>()==field)?["value"]?["string"]?.GetValue<string>()??"";
            var leaderId=leader.Split('|',2)[1];var nickname=Text(sp,"Nickname");if(nickname.Length==0)nickname=leaderId.Split('.').Last();
            var portraitRef=Ops(lp).FirstOrDefault(n=>S(n,"fieldPath")=="Slot")?["value"]?["asset"]?["name"]?.GetValue<string>()??"";
            var picture=pictures.FirstOrDefault(p=>p.Key.Equals(portraitRef,StringComparison.OrdinalIgnoreCase)||p.Key.Equals(portraitRef.Replace("/","__"),StringComparison.OrdinalIgnoreCase)||p.Key.Equals(portraitRef.Replace("_sprites",""),StringComparison.OrdinalIgnoreCase)).Value;
            if(picture is null && portraitRef.Length>0)picture=pictures.FirstOrDefault(p=>p.Key.Replace("_texture","").Equals(portraitRef.Replace("_sprites",""),StringComparison.OrdinalIgnoreCase)).Value;
            var attr=new Dictionary<string,int>();var attributeNames=new[]{"agility","weapon_skill","valour","toughness","vitality","precision","positioning"};
            foreach(var op in Ops(lp).Where(n=>S(n,"fieldPath")=="InitialAttributes"))if(op["index"] is JsonValue ix&&ix.TryGetValue<int>(out var index)&&index>=0&&index<7)attr[attributeNames[index]]=op["value"]?["byte"]?.GetValue<int>()??-1;
            chars.Add(new(leaderId,leaderId,nickname,string.Join(" ",new[]{Text(sp,"Forename"),Text(sp,"Surname")}.Where(s=>s.Length>0)),Text(lp,"UnitTitle"),string.Join("\n\n",new[]{Text(sp,"Description"),Text(lp,"UnitDescription")}.Where(s=>s.Length>0)),picture is null?"":Path.GetRelativePath(root,picture).Replace('\\','/'),attr,package,clones.Any(c=>Key(c,true)==leader),group.Where(k=>k!=leader).Select(k=>k.Split('|',2)[1]).ToArray()));
        }
        var globalClones=clones.Where(n=>owners[Key(n,true)].Length==0).ToArray();
        var globalPatches=patches.Where(n=>owners[Key(n)].Length==0).Select(n=>n.DeepClone()).ToArray();
        // Normal campaign availability is rebuilt for the chosen clone roster.
        foreach(var patch in globalPatches.Where(n=>S(n,"templateType")=="StrategyConfig"))
            patch["set"]=new JsonArray(Ops(patch).Where(n=>S(n,"fieldPath")!="InitialPickableUnitLeaders"&&S(n,"fieldPath")!="InitialUnlockedUnitLeaders").Select(n=>n.DeepClone()).ToArray());
        var sharedDefs=new JsonObject{["templateClones"]=new JsonArray(globalClones.Select(n=>n.DeepClone()).ToArray()),["templatePatches"]=new JsonArray(globalPatches.Where(n=>Ops(n).Any()).ToArray())};
        var notices=new List<string>{"imported_compatibility","shared_resources"};if(globalClones.Length>0||globalPatches.Any(n=>Ops(n).Any()))notices.Add("global_changes");if(chars.Any(c=>c.Requires!.Length>0))notices.Add("linked_characters");if(chars.Any(c=>c.Portrait.Length==0))notices.Add("missing_portraits");
        var shared=Zip(root,"shared",sharedPaths,new(){{"manifest.json",mf},{"templates.json",sharedDefs},{"prefabs.json",mf["additionPrefabs"]?.DeepClone()??new JsonArray()}});
        return new(1,S(mf,"description",Path.GetFileNameWithoutExtension(archive)),S(mf,"version","imported"),module,"","Not verified",shared,chars,"Jiangyu",mf["depends"]?.AsArray().Select(n=>n!.GetValue<string>()).ToArray()??[],notices.ToArray(),true);
    }
    static IEnumerable<JsonNode> Ops(JsonNode? n)=>n?["set"]?.AsArray().Select(x=>x!)??[];
    static IEnumerable<JsonNode> References(JsonNode? n)
    {
        if(n is JsonObject obj){if(S(obj,"kind")=="TemplateReference"&&obj["reference"] is JsonNode reference)yield return reference;foreach(var (_,v) in obj)foreach(var r in References(v))yield return r;}
        else if(n is JsonArray a)foreach(var v in a)foreach(var r in References(v))yield return r;
    }
}
