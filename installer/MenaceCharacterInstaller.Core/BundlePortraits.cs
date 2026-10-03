using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using StbImageWriteSharp;

namespace MenaceCharacterInstaller;
public static class BundlePortraits
{
    public static Dictionary<string,string> Read(IEnumerable<string> bundles,string output,CancellationToken ct)
    {
        Directory.CreateDirectory(output);var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in bundles)
        {
            ct.ThrowIfCancellationRequested();var manager=new AssetsManager();
            try
            {
                var bundle=manager.LoadBundleFile(path,true);
                for(int index=0;index<bundle.file.BlockAndDirInfo.DirectoryInfos.Count;index++)
                {
                    if(!bundle.file.IsAssetsFile(index))continue;
                    var file=manager.LoadAssetsFileFromBundle(bundle,index,false);
                    foreach(var info in file.file.GetAssetsOfType(AssetClassID.Texture2D))
                    {
                        var field=manager.GetBaseField(file,info);var w=field["m_Width"].AsInt;var h=field["m_Height"].AsInt;
                        if(w<32||h<32||w>512||h>512)continue;
                        var texture=TextureFile.ReadTextureFile(field);texture.SetPictureDataFromBundle(bundle);var pixels=texture.DecodeTextureRaw(texture.FillPictureData(file));if(pixels is null)continue;
                        var rgba=new byte[w*h*4];
                        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                        {int s=((h-1-y)*w+x)*4,d=(y*w+x)*4;rgba[d]=pixels[s+2];rgba[d+1]=pixels[s+1];rgba[d+2]=pixels[s];rgba[d+3]=pixels[s+3];}
                        var name=field["m_Name"].AsString;var png=Path.Combine(output,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant()+".png");
                        using(var stream=File.Create(png))new ImageWriter().WritePng(rgba,w,h,ColorComponents.RedGreenBlueAlpha,stream);
                        result[name]=png;
                    }
                }
            }
            catch(Exception ex) when(ex is not OperationCanceledException){System.Diagnostics.Trace.WriteLine("Portrait bundle: "+ex.Message);}
            finally {manager.UnloadAll();}
        }
        return result;
    }
}
