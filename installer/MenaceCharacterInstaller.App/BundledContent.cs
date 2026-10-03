using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MenaceCharacterInstaller;
public static class BundledContent
{
    private const string Prefix="InstallerContent/";
    private static readonly Assembly Assembly=typeof(BundledContent).Assembly;
    private static byte[] Bytes(string name)
    {using var stream=Assembly.GetManifestResourceStream(name)??throw new FileNotFoundException(name);using var memory=new MemoryStream();stream.CopyTo(memory);return memory.ToArray();}
    public static string DefaultRoot
    {
        get
        {
            var hash=Convert.ToHexString(SHA256.HashData(Bytes(Prefix+"catalog.json"))).ToLowerInvariant()[..24];
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MenaceCharacterInstaller","bundled",hash);
        }
    }
    public static void Ensure(string root)
    {
        foreach(var name in Assembly.GetManifestResourceNames().Where(n=>n.StartsWith(Prefix,StringComparison.Ordinal)))
        {
            var relative=name[Prefix.Length..].Replace('\\','/');var path=Data.Inside(root,relative);
            using var resource=Assembly.GetManifestResourceStream(name)!;
            var expected=Convert.ToHexString(SHA256.HashData(resource));resource.Position=0;
            if(File.Exists(path)&&Data.Hash(path)==expected)continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+".tmp-"+Guid.NewGuid().ToString("N");
            try{using(var output=File.Create(temp))resource.CopyTo(output);File.Move(temp,path,true);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
