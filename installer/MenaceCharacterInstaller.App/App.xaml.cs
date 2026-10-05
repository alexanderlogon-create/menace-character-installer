using System.Windows;
using System.IO;
namespace MenaceCharacterInstaller;
public partial class App : Application
{
    private Mutex? instance;
    private static readonly string Settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MenaceCharacterInstaller","settings.json");
    public static void SetLanguage(string language,bool save=true)
    {
        Localize.Language=language=="en"?"en":"ru";
        foreach(var (key,pair) in Localize.Strings)Current.Resources[key]=Localize.Language=="en"?pair.En:pair.Ru;
        if(save)try{Data.Write(Settings,new Dictionary<string,string>{{"language",Localize.Language}});}catch(IOException){ }
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        var language="ru";
        try{if(File.Exists(Settings))language=Data.Read<Dictionary<string,string>>(Settings).GetValueOrDefault("language","ru");}catch{ }
        SetLanguage(language,false);
        var verify=Array.IndexOf(e.Args,"--verify-content");
        if(verify>=0&&verify+1<e.Args.Length)
        {
            try
            {
                var root=Path.GetFullPath(e.Args[verify+1]);BundledContent.Ensure(root);
                var catalog=Data.Read<Catalog>(Path.Combine(root,"catalog.json"));
                foreach(var file in new[]{catalog.Shared}.Concat(catalog.Characters.Select(c=>c.Package)))
                    if(!Data.Hash(Data.Inside(root,file.Path)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(file.Path);
                Data.Write(Path.Combine(root,"verification.json"),new{version="0.3.2",characters=catalog.Characters.Count,portraits=catalog.Characters.Count(c=>File.Exists(Data.Inside(root,c.Portrait))),languages=Localize.Strings.Count});
                Shutdown(0);
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(Path.GetTempPath(),"mci-verify-error.txt"),ex.ToString());Shutdown(1);}
            return;
        }
        instance=new Mutex(true,"Local\\MenaceCharacterInstaller",out var first);
        if(!first){MessageBox.Show(Localize.T("alreadyrunning"));Shutdown();return;}
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e){instance?.Dispose();base.OnExit(e);}
}
