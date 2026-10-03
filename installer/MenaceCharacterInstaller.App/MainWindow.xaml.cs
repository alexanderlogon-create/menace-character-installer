using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MenaceCharacterInstaller;
public sealed class LeaderRow(Leader leader,string root) : INotifyPropertyChanged
{
    public Leader Leader {get;}=leader;
    public string Name=>Leader.Name;
    public string Kind=>Localize.T(!Leader.IsClone?"replacement":Leader.LeaderId.StartsWith("pilot.")?"vehicle":"infantry");
    public string? Portrait=>Leader.Portrait.Length==0?null:Data.Inside(root,Leader.Portrait);
    public bool Installed {get;set;}
    public string InstalledLabel=>Installed?Localize.T("installed"):"";
    private bool selected;
    public bool Selected {get=>selected;set {if(selected==value)return;selected=value;PropertyChanged?.Invoke(this,new(nameof(Selected)));}}
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh(){PropertyChanged?.Invoke(this,new(nameof(InstalledLabel)));PropertyChanged?.Invoke(this,new(nameof(Kind)));}
}
public sealed class DependencyRow(DependencyStatus dependency)
{
    public string Name=>dependency.Name;
    public Brush Light=>dependency.Required?Brushes.IndianRed:Brushes.LightGreen;
    public string VersionText=>(dependency.Installed=="Not installed"?Localize.T("notinstalled"):dependency.Installed=="Unknown"?Localize.T("unknown"):dependency.Installed)+" · "+dependency.Explanation;
    public string Status=>Localize.T(dependency.Required?"missing":"installed");
    public string DownloadPage=>dependency.DownloadPage;
    public string WebsiteLabel=>Localize.T(dependency.Name=="All Leaders Pickable"?"download":"website");
    public string Hint=>Localize.T(dependency.Automatic?"depautohint":"deplocalhint");
    public Visibility DownloadVisibility=>dependency.Required?Visibility.Visible:Visibility.Collapsed;
    public Visibility WebsiteVisibility=>dependency.Required&&!dependency.Automatic?Visibility.Visible:Visibility.Collapsed;
}
public partial class MainWindow : Window
{
    private readonly string stateRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MenaceCharacterInstaller");
    private readonly string bundledRoot;
    private readonly string logPath;
    private string contentRoot="";
    private Catalog catalog=null!;
    private Installer installer=null!;
    private List<LeaderRow> rows=[];
    private GameInfo? gameInfo;
    private DependencyStatus[] dependencies=[];
    private bool busy,adjusting;
    private CancellationTokenSource? cancellation;
    public MainWindow()
    {
        InitializeComponent();MaxHeight=SystemParameters.WorkArea.Height;MaxWidth=SystemParameters.WorkArea.Width;
        var args=Environment.GetCommandLineArgs();var arg=Array.IndexOf(args,"--content");
        bundledRoot=Path.GetFullPath(arg>=0&&arg+1<args.Length?args[arg+1]:BundledContent.DefaultRoot);
        Directory.CreateDirectory(Path.Combine(stateRoot,"logs"));logPath=Path.Combine(stateRoot,"logs",DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");
        LanguageBox.SelectedIndex=Localize.Language=="en"?1:0;
        Loaded+=async(_,_)=>{
            SetBusy(true);
            try
            {
                if(arg<0)await Task.Run(()=>BundledContent.Ensure(bundledRoot));
                LoadCatalog(Data.Read<Catalog>(Path.Combine(bundledRoot,"catalog.json")),bundledRoot,"");
                var games=Games.Detect();if(games.Count==1){GamePath.Text=games[0];await InspectAsync(true);}else {dependencies=Installer.Dependencies("",catalog);RefreshText();Report(Localize.T(games.Count==0?"choosegame":"manygames"));}
            }
            catch(Exception ex){Fail(ex);InstallButton.IsEnabled=false;}
            finally {SetBusy(false);}
        };
    }
    private void LoadCatalog(Catalog next,string root,string source)
    {
        if(next.SchemaVersion!=1||next.Characters.Count==0||next.Characters.Select(c=>c.Id).Distinct().Count()!=next.Characters.Count||next.Module!=Path.GetFileName(next.Module))throw new InvalidDataException("Unsupported or invalid catalogue");
        Installer.Dependencies("",next);
        catalog=next;contentRoot=root;installer=new(catalog,contentRoot,stateRoot,bundledRoot);
        rows=catalog.Characters.Select(c=>new LeaderRow(c,contentRoot)).ToList();
        foreach(var row in rows)row.PropertyChanged+=Selection_Changed;
        SourcePath.Text=source.Length==0?Localize.T("bundled"):source;SourcePath.Tag=source;
        Search.Clear();Roster.ItemsSource=rows;Roster.SelectedIndex=0;RefreshText();
    }
    private void Language_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(LanguageBox.SelectedItem is not ComboBoxItem choice)return;
        App.SetLanguage(choice.Tag.ToString()!);
        if(catalog is not null){RefreshText();Report(Localize.T("loaded",rows.Count));}
    }
    private void RefreshText()
    {
        if(catalog is null)return;
        if(SourcePath.Tag is string source&&source.Length==0)SourcePath.Text=Localize.T("bundled");
        PackInfo.Text=Localize.T("packinfo",catalog.Name,catalog.Version,catalog.Format=="CustomLeaders"?"Custom Leaders":"Jiangyu",rows.Count);
        var notices=(catalog.Notices??[]).Where(Localize.Strings.ContainsKey).Select(n=>Localize.T(n)).ToList();
        if(gameInfo is not null&&File.Exists(Path.Combine(gameInfo.Path,"Mods","AllLeadersPickable.dll")))notices.Add(Localize.T("pickable"));
        PackNotices.Text=string.Join("\n",notices);
        if(gameInfo is not null)GameStatus.Text=Localize.T(gameInfo.Hash.Equals(catalog.GameAssemblySha256,StringComparison.OrdinalIgnoreCase)?"known":"unverified",gameInfo.Hash.Equals(catalog.GameAssemblySha256,StringComparison.OrdinalIgnoreCase)?catalog.TestedGame:gameInfo.Version);
        DependenciesList.ItemsSource=dependencies.Select(d=>new DependencyRow(d)).ToArray();
        foreach(var row in rows)row.Refresh();
        UpdateSelection();ShowDetails();
    }
    private void Selection_Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(LeaderRow.Selected)||adjusting||sender is not LeaderRow changed)return;
        adjusting=true;
        try
        {
            var group=rows.Where(r=>r==changed||changed.Leader.Requires?.Contains(r.Leader.Id)==true||r.Leader.Requires?.Contains(changed.Leader.Id)==true);
            foreach(var row in group)row.Selected=changed.Selected;
        }
        finally {adjusting=false;}
        UpdateSelection();
    }
    private void UpdateSelection()=>SelectionCount.Text=Localize.T("selected",rows.Count(r=>r.Selected),rows.Count);
    private void Report(string text)
    {
        Status.Text=text;File.AppendAllText(logPath,DateTimeOffset.Now.ToString("O")+" "+text+Environment.NewLine);
    }
    private void Fail(Exception ex)
    {
        Report(Localize.T("failed"));File.AppendAllText(logPath,ex+Environment.NewLine);
        MessageBox.Show(this,Localize.Diagnostic(ex.Message),"MENACE Character Installer",MessageBoxButton.OK,MessageBoxImage.Warning);
    }
    private void SetBusy(bool value)
    {
        busy=value;Progress.IsIndeterminate=value;
        foreach(var c in new Control[]{DetectButton,BrowseButton,CheckButton,GamePath,AllButton,NoneButton,Roster,InstallButton,RestoreButton,Search,AllowUnverified,SourceButton,BundledButton,LanguageBox,DependenciesList})c.IsEnabled=!value;
        InstallButton.IsEnabled=!value&&catalog is not null;
        RestoreButton.IsEnabled=!value&&catalog is not null;
        CancelButton.IsEnabled=value&&cancellation is not null;
    }
    private async Task InspectAsync(bool applySelection)
    {
        var path=GamePath.Text;SetBusy(true);
        try
        {
            var result=await Task.Run(()=>{var info=Games.Inspect(path);return (info,deps:Installer.Dependencies(info.Path,catalog),ids:Games.InstalledIds(info.Path,catalog));});
            gameInfo=result.info;GamePath.Text=gameInfo.Path;dependencies=result.deps;
            var known=gameInfo.Hash.Equals(catalog.GameAssemblySha256,StringComparison.OrdinalIgnoreCase);
            AllowUnverified.Visibility=known?Visibility.Collapsed:Visibility.Visible;AllowUnverified.IsChecked=false;
            adjusting=true;
            try{foreach(var row in rows){row.Installed=result.ids.Contains(row.Leader.Id);if(applySelection)row.Selected=row.Installed;}}
            finally{adjusting=false;}
            RefreshText();Report(Localize.T("checked"));
        }
        catch
        {gameInfo=null;dependencies=Installer.Dependencies("",catalog);GameStatus.Text=Localize.T("choosegame");RefreshText();throw;}
        finally {SetBusy(false);}
    }
    private async void Check_Click(object sender,RoutedEventArgs e){try{await InspectAsync(false);}catch(Exception ex){Fail(ex);}}
    private async void Detect_Click(object sender,RoutedEventArgs e)
    {try{var games=Games.Detect();if(games.Count==1){GamePath.Text=games[0];await InspectAsync(true);}else Browse_Click(sender,e);}catch(Exception ex){Fail(ex);}}
    private async void Browse_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFolderDialog{Title=Localize.T("choosefolder")};
        if(dialog.ShowDialog(this)!=true)return;GamePath.Text=dialog.FolderName;
        try{await InspectAsync(true);}catch(Exception ex){Fail(ex);}
    }
    private async void Source_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Title=Localize.T("choosezip"),Filter=Localize.T("zipfilter"),CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        try
        {
            cancellation=new();SetBusy(true);Report(Localize.T("importing"));
            var archive=dialog.FileName;
            var imported=await Task.Run(()=>PackImport.Open(archive,Path.Combine(stateRoot,"imports"),cancellation.Token));
            LoadCatalog(imported.Catalog,imported.Root,imported.Archive);
            if(!string.IsNullOrWhiteSpace(GamePath.Text))await InspectAsync(true);
            else {dependencies=Installer.Dependencies("",catalog);RefreshText();}
            Report(Localize.T("loaded",rows.Count));
        }
        catch(OperationCanceledException){Report(Localize.T("cancelled"));}
        catch(Exception ex){Fail(ex);}
        finally {cancellation?.Dispose();cancellation=null;SetBusy(false);}
    }
    private async void Bundled_Click(object sender,RoutedEventArgs e)
    {
        try{LoadCatalog(Data.Read<Catalog>(Path.Combine(bundledRoot,"catalog.json")),bundledRoot,"");if(!string.IsNullOrWhiteSpace(GamePath.Text))await InspectAsync(true);else {dependencies=Installer.Dependencies("",catalog);RefreshText();}}
        catch(Exception ex){Fail(ex);}
    }
    private void Download_Click(object sender,RoutedEventArgs e)
    {
        try{if(sender is Button b&&b.Tag is string url&&Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="https")Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
        catch(Exception ex){Fail(ex);}
    }
    private void Search_Changed(object sender,TextChangedEventArgs e)
    {
        if(Roster?.ItemsSource is null)return;
        var q=Search.Text.Trim();CollectionViewSource.GetDefaultView(rows).Filter=o=>o is LeaderRow r&&(r.Name+" "+r.Leader.Role+" "+r.Leader.Description).Contains(q,StringComparison.OrdinalIgnoreCase);
    }
    private async void DependencyInstall_Click(object sender,RoutedEventArgs e)
    {
        if(busy||sender is not Button button||button.Tag is not string name)return;
        var game=GamePath.Text;
        try
        {
            Games.RequireClosed();var info=Games.Inspect(game);
            var dependency=Installer.Dependencies(info.Path,catalog).Single(d=>d.Name==name);
            string? archive=null;
            if(dependency.Required&&!dependency.Automatic)
            {
                var dialog=new OpenFileDialog{Title=Localize.T("depchoose",name),Filter=Localize.T("depfilter"),CheckFileExists=true};
                if(dialog.ShowDialog(this)!=true)return;
                archive=dialog.FileName;
            }
            cancellation=new();SetBusy(true);Report(Localize.T("depstarting",name));
            var log=new Progress<string>(s=>Report(Localize.Diagnostic(s)));
            var result=await Task.Run(()=>installer.InstallDependency(game,name,log,cancellation.Token,archive));
            await InspectAsync(false);
            Report(Localize.T(result.RequiresRestart?"deprestart":result.Changed?"depdone":"depready",name));
        }
        catch(OperationCanceledException){Report(Localize.T("cancelled"));}
        catch(Exception ex){Fail(ex);}
        finally {cancellation?.Dispose();cancellation=null;SetBusy(false);}
    }
    private void All_Click(object sender,RoutedEventArgs e){foreach(var row in rows)row.Selected=true;}
    private void None_Click(object sender,RoutedEventArgs e){foreach(var row in rows)row.Selected=false;}
    private void Roster_SelectionChanged(object sender,SelectionChangedEventArgs e)=>ShowDetails();
    private void ShowDetails()
    {
        if(Roster.SelectedItem is not LeaderRow row)return;
        DetailName.Text=row.Name;DetailFullName.Text=row.Leader.FullName;DetailRole.Text=row.Leader.Role;DetailDescription.Text=row.Leader.Description;
        DetailPortrait.Source=row.Portrait is string path?new BitmapImage(new Uri(path)):null;
        NoPortrait.Visibility=row.Portrait is null?Visibility.Visible:Visibility.Collapsed;
        DetailStats.Text=string.Join("\n",row.Leader.Attributes.Where(k=>k.Value>=0).Select(k=>(Localize.Strings.ContainsKey(k.Key)?Localize.T(k.Key):k.Key)+": "+k.Value));
    }
    private async void Install_Click(object sender,RoutedEventArgs e)
    {
        var selected=rows.Where(r=>r.Selected).Select(r=>r.Leader.Id).ToArray();
        if(selected.Length==0){MessageBox.Show(this,Localize.T("pickone"));return;}
        var game=GamePath.Text;
        try
        {
            SetBusy(true);Games.RequireClosed();var info=await Task.Run(()=>Games.Inspect(game));
            if(!info.Hash.Equals(catalog.GameAssemblySha256,StringComparison.OrdinalIgnoreCase)&&AllowUnverified.IsChecked!=true)throw new IOException(Localize.T("unverifiederror"));
            Games.CheckDuplicates(info.Path,catalog);
            var required=Installer.Dependencies(info.Path,catalog).Where(d=>d.Required).ToArray();
            if(required.Any(d=>!d.Automatic))throw new IOException("Install missing dependencies using their Install buttons: "+string.Join(", ",required.Where(d=>!d.Automatic).Select(d=>d.Name)));
            var old=Games.InstalledIds(info.Path,catalog);var removed=rows.Where(r=>old.Contains(r.Leader.Id)&&!r.Selected).Select(r=>r.Name).ToArray();
            var message=Localize.T("installnames",string.Join(", ",rows.Where(r=>r.Selected).Select(r=>r.Name)));
            if(removed.Length>0)message+=Localize.T("removenames",string.Join(", ",removed));
            message+=required.Length>0?Localize.T("downloadnames",string.Join(", ",required.Select(d=>d.Name))):Localize.T("readydeps");
            message+=Localize.T("confirm");
            if(MessageBox.Show(this,message,Localize.T("installtitle"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            cancellation=new();SetBusy(true);var log=new Progress<string>(s=>Report(Localize.Diagnostic(s)));var allow=AllowUnverified.IsChecked==true;
            await Task.Run(()=>installer.Install(game,selected,allow,log,cancellation.Token));
            await InspectAsync(false);Report(Localize.T("done",selected.Length));
        }
        catch(OperationCanceledException){Report(Localize.T("cancelled"));}
        catch(Exception ex){Fail(ex);}
        finally {cancellation?.Dispose();cancellation=null;SetBusy(false);}
    }
    private void Cancel_Click(object sender,RoutedEventArgs e){cancellation?.Cancel();CancelButton.IsEnabled=false;Report(Localize.T("cancelpending"));}
    private async void Restore_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var game=GamePath.Text;var backup=installer.LatestBackup(game)??throw new IOException(Localize.T("nobackup"));
            if(MessageBox.Show(this,Localize.T("restoreconfirm"),Localize.T("restore"),MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            SetBusy(true);await Task.Run(()=>FileTransaction.Restore(backup,game,Data.Read<InstallJournal>(Path.Combine(backup,"transaction.json")).Status=="Complete"));
            await InspectAsync(true);Report(Localize.T("restored"));
        }
        catch(Exception ex){Fail(ex);}finally{SetBusy(false);}
    }
    private void Mods_Click(object sender,RoutedEventArgs e){try{var p=Path.Combine(Games.Inspect(GamePath.Text).Path,"Mods");if(Directory.Exists(p))Process.Start(new ProcessStartInfo(p){UseShellExecute=true});}catch(Exception ex){Fail(ex);}}
    private void Logs_Click(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo(Path.Combine(stateRoot,"logs")){UseShellExecute=true});
    private void Window_Closing(object? sender,CancelEventArgs e){if(!busy)return;e.Cancel=true;Report(Localize.T("wait"));}
}
