using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MenaceCharacterInstaller;
public sealed record GameInfo(string Path, string Hash, string Version);
public static class Games
{
    public static List<string> Detect()
    {
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(OperatingSystem.IsWindows())foreach(var key in new[]{@"HKEY_CURRENT_USER\Software\Valve\Steam",@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"})
        {
            if(Registry.GetValue(key,"SteamPath",null) is string a)roots.Add(a);
            if(Registry.GetValue(key,"InstallPath",null) is string b)roots.Add(b);
        }
        return DetectLibraries(roots);
    }
    public static List<string> DetectLibraries(IEnumerable<string> roots)
    {
        var libraries=new HashSet<string>(roots,StringComparer.OrdinalIgnoreCase);
        foreach(var root in libraries.ToArray())
        {
            var vdf=Path.Combine(root,"steamapps","libraryfolders.vdf");
            if(File.Exists(vdf))foreach(Match m in Regex.Matches(File.ReadAllText(vdf),"\"path\"\\s+\"([^\"]+)\""))libraries.Add(m.Groups[1].Value.Replace("\\\\","\\"));
        }
        var result=new List<string>();
        foreach(var root in libraries)
        {
            var manifest=Path.Combine(root,"steamapps","appmanifest_2432860.acf");
            var name=File.Exists(manifest)?Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value:"Menace";
            if(string.IsNullOrWhiteSpace(name)||Path.GetFileName(name)!=name)continue;
            var path=Path.Combine(root,"steamapps","common",name);
            if(File.Exists(Path.Combine(path,"Menace.exe")))result.Add(Path.GetFullPath(path));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static GameInfo Inspect(string path)
    {
        path=Path.GetFullPath(path.Trim().Trim('"'));
        Data.RejectLinks(path,path);
        if(!File.Exists(Path.Combine(path,"Menace.exe"))||!File.Exists(Path.Combine(path,"GameAssembly.dll"))||!Directory.Exists(Path.Combine(path,"Menace_Data")))throw new InvalidDataException("Choose the MENACE folder containing Menace.exe, GameAssembly.dll and Menace_Data.");
        return new(path,Data.Hash(Path.Combine(path,"GameAssembly.dll")),FileVersionInfo.GetVersionInfo(Path.Combine(path,"Menace.exe")).ProductVersion??"Unknown");
    }
    public static void RequireClosed()
    {
        var processes=Process.GetProcessesByName("Menace");
        try { if(processes.Length>0)throw new IOException("Close MENACE before installing or restoring files."); } finally { foreach(var p in processes)p.Dispose(); }
    }
    public static string Version(string file)
    {
        if(!File.Exists(file))return "Not installed";
        try{return FileVersionInfo.GetVersionInfo(file).ProductVersion?.Split('+')[0]??"Unknown";}catch{return "Unknown";}
    }
    public static string[] InstalledIds(string game,Catalog catalog)
    {
        if(catalog.Format=="CustomLeaders")return catalog.Characters.Where(c=>File.Exists(Data.Inside(game,"Mods/customleaders/"+c.Id+"/"+c.Id+(c.IsClone?"_clone.json":"_replace.json")))).Select(c=>c.Id).ToArray();
        var folder=Path.Combine(game,"Mods",catalog.Module);
        var templates=Path.Combine(folder,"templates.json");
        if(!File.Exists(templates))return [];
        var defs=JsonNode.Parse(File.ReadAllText(templates))!;
        var all=new HashSet<string>();
        foreach(var type in new[]{"templateClones","templatePatches"})foreach(var n in defs[type]?.AsArray()??[])
            if(n?["templateType"]?.GetValue<string>()=="UnitLeaderTemplate")all.Add(n[type=="templateClones"?"cloneId":"templateId"]!.GetValue<string>());
        return catalog.Characters.Where(c=>all.Contains(c.LeaderId)).Select(c=>c.Id).ToArray();
    }
    public static void CheckDuplicates(string game,Catalog catalog)
    {
        var mods=Path.Combine(game,"Mods");if(!Directory.Exists(mods))return;
        if(catalog.Format=="CustomLeaders")
        {
            var custom=Path.Combine(mods,"customleaders");if(!Directory.Exists(custom))return;
            var ledger=Path.Combine(mods,"menace-character-installer-"+catalog.Module+".json");
            var owned=File.Exists(ledger)?Data.Read<Selection>(ledger).CharacterIds:[];
            foreach(var c in catalog.Characters)
            {
                var folder=Data.Inside(custom,c.Id);if(!Directory.Exists(folder))continue;
                if(!owned.Contains(c.Id))throw new IOException("Character folder already exists and is not managed by this pack: "+folder);
            }
            return;
        }
        var target=Path.GetFullPath(Path.Combine(mods,catalog.Module));
        foreach(var file in Directory.EnumerateFiles(mods,"templates.json",SearchOption.AllDirectories))
        {
            Data.RejectLinks(mods,file);
            if(Path.GetDirectoryName(file)!.Equals(target,StringComparison.OrdinalIgnoreCase))continue;
            var defs=JsonNode.Parse(File.ReadAllText(file));
            var ids=new HashSet<string>();
            foreach(var kind in new[]{"templateClones","templatePatches"})foreach(var n in defs?[kind]?.AsArray()??[])
                if(n?["templateType"]?.GetValue<string>()=="UnitLeaderTemplate")ids.Add(n[kind=="templateClones"?"cloneId":"templateId"]!.GetValue<string>());
            if(catalog.Characters.Any(c=>ids.Contains(c.LeaderId)))throw new IOException("Another installed pack contains the same characters: "+Path.GetDirectoryName(file)+". Remove that duplicate before continuing.");
        }
        var existing=Path.Combine(target,"jiangyu.json");
        if(Directory.Exists(target) && (!File.Exists(existing)||JsonNode.Parse(File.ReadAllText(existing))?["name"]?.GetValue<string>()!=catalog.Module))throw new IOException("Existing target folder is not the expected character-pack module.");
    }
}
public sealed record Change(string Relative, bool Existed, string? BeforeHash, string? AfterHash);
public sealed class InstallJournal
{
    public string Game {get;set;}="";
    public string Status {get;set;}="Prepared";
    public DateTimeOffset Created {get;set;}=DateTimeOffset.Now;
    public List<Change> Changes {get;set;}=[];
    public List<string> Applied {get;set;}=[];
}
public sealed class FileTransaction(string game, string backupRoot)
{
    public string Apply(IReadOnlyDictionary<string,string> files, IEnumerable<string> remove, IProgress<string>? log=null)
    {
        Games.RequireClosed();
        var backup=Path.Combine(backupRoot,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var journal=new InstallJournal{Game=Path.GetFullPath(game)};
        var paths=files.Keys.Concat(remove).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var rel in paths)
        {
            var target=Data.Inside(game,rel);var exists=File.Exists(target);
            var before=exists?Data.Hash(target):null;
            if(files.TryGetValue(rel,out var incoming) && before==Data.Hash(incoming))continue;
            if(!exists && !files.ContainsKey(rel))continue;
            if(exists){var copy=Data.Inside(backup,"original/"+rel.Replace('\\','/'));Directory.CreateDirectory(Path.GetDirectoryName(copy)!);File.Copy(target,copy);if(Data.Hash(copy)!=before)throw new IOException("File changed while backing up: "+target);}
            journal.Changes.Add(new(rel,exists,before,files.TryGetValue(rel,out var source)?Data.Hash(source):null));
        }
        var record=Path.Combine(backup,"transaction.json");Data.Write(record,journal);
        try
        {
            Games.RequireClosed();journal.Status="Applying";Data.Write(record,journal);
            foreach(var change in journal.Changes)
            {
                var target=Data.Inside(game,change.Relative);
                if(change.Existed ? !File.Exists(target)||Data.Hash(target)!=change.BeforeHash : File.Exists(target))throw new IOException("File changed during installation: "+target);
            }
            foreach(var change in journal.Changes)
            {
                var target=Data.Inside(game,change.Relative);log?.Report("Installing " + change.Relative);
                // Write-ahead record lets recovery distinguish untouched and attempted files.
                journal.Applied.Add(change.Relative);Data.Write(record,journal);
                if(files.TryGetValue(change.Relative,out var source))AtomicCopy(source,target);else File.Delete(target);
            }
            journal.Status="Complete";Data.Write(record,journal);return backup;
        }
        catch(Exception error)
        {
            try { Restore(backup,game,false); }
            catch(Exception rollback) { throw new AggregateException("Installation failed. Backup retained at " + backup,error,rollback); }
            throw;
        }
    }
    public static void Restore(string backup,string expectedGame,bool protectModified=true)
    {
        Games.RequireClosed();var record=Path.Combine(backup,"transaction.json");var journal=Data.Read<InstallJournal>(record);
        if(!Path.GetFullPath(expectedGame).Equals(journal.Game,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Backup belongs to another game");
        var attempted=journal.Changes.Where(c=>journal.Applied.Contains(c.Relative)).ToArray();
        foreach(var c in attempted)
        {
            var target=Data.Inside(expectedGame,c.Relative);
            if(c.Existed && (!File.Exists(Data.Inside(backup,"original/"+c.Relative.Replace('\\','/'))) || Data.Hash(Data.Inside(backup,"original/"+c.Relative.Replace('\\','/')))!=c.BeforeHash))throw new InvalidDataException("Damaged backup");
            if(protectModified && (c.AfterHash is null ? File.Exists(target) : !File.Exists(target)||Data.Hash(target)!=c.AfterHash))throw new IOException("File changed since this installation; restore stopped: "+c.Relative);
            if(!protectModified)
            {
                var current=File.Exists(target)?Data.Hash(target):null;
                if(current!=c.BeforeHash && current!=c.AfterHash)throw new IOException("External change prevents rollback: "+c.Relative);
            }
        }
        foreach(var c in attempted.Reverse())
        {
            var target=Data.Inside(expectedGame,c.Relative);
            if(c.Existed)AtomicCopy(Data.Inside(backup,"original/"+c.Relative.Replace('\\','/')),target);else if(File.Exists(target))File.Delete(target);
        }
        journal.Status="Restored";Data.Write(record,journal);
    }
    private static void AtomicCopy(string source,string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);var temp=target+".mci-"+Guid.NewGuid().ToString("N");
        try{File.Copy(source,temp);File.Move(temp,target,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
