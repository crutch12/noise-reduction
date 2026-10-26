using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.ServiceProcess;
using System.Threading;
using System.Security.AccessControl;
using Microsoft.Win32;
using NoiseReduction.Platform;
using NoiseReduction.Desktop;

[assembly: AssemblyTitle("NoiseReduction Setup")]
[assembly: AssemblyVersion("1.1.0.0")]
public class InstallFileState { public string Name; public bool Existed; }
public class InstallTransaction {
 public string Id; public int Version; public bool Complete,Committed; public int FileCount=-1,EngineCount=-1,SetupCount=-1;
 public bool GraphRestoreRequired,GraphSnapshotTaken,FilesRestored; public string[] OriginalRunningServices;
 public InstallFileState[] Files; public string[] EngineCheckpoint,SetupCheckpoint;
}
class Setup {
 const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NoiseReduction";
 static bool silent;
 // шаги для окна установки, в тихом режиме никуда не выводятся
 static Action<int,string> Step=delegate { };
 static string T(string source) { return UiText.Get(source); }
 static string InstallRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NoiseReduction"); } }
 static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),"Noise Reduction.lnk"); } }
 static string LegacyShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),"NoiseReduction.lnk"); } }
 static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"NoiseReduction"); } }
 static string[] PayloadNames() { return Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n=>n.StartsWith("payload.",StringComparison.Ordinal)).Select(n=>n.Substring(8)).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase).ToArray(); }
 static string RelativeName(string name) {
  if(String.IsNullOrEmpty(name) || Path.IsPathRooted(name)) throw new IOException("Unsafe package filename");
  var parts=name.Replace('\\','/').Split('/');
  foreach(var part in parts) {
   if(part.Length==0 || part=="." || part==".." || part.EndsWith(".",StringComparison.Ordinal) || part.EndsWith(" ",StringComparison.Ordinal) || part.IndexOfAny(Path.GetInvalidFileNameChars())>=0) throw new IOException("Unsafe package filename");
   string stem=part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
   if(stem=="CON" || stem=="PRN" || stem=="AUX" || stem=="NUL" || stem=="CONIN$" || stem=="CONOUT$" || stem=="CLOCK$" || System.Text.RegularExpressions.Regex.IsMatch(stem,@"\A(COM|LPT)[1-9¹²³]\z")) throw new IOException("Reserved package filename");
  }
  return String.Join("/",parts);
 }
 static FileAttributes? ExistingAttributes(string path) { try { return File.GetAttributes(path); } catch(FileNotFoundException) { return null; } catch(DirectoryNotFoundException) { return null; } }
 static string SafeFile(string directory,string name) {
  name=RelativeName(name);
  string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
  string path=Path.GetFullPath(Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar))); if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new IOException("Package path escapes destination");
  for(var current=new DirectoryInfo(Path.GetDirectoryName(path));current != null;current=current.Parent) { var attributes=ExistingAttributes(current.FullName); if(attributes.HasValue && ((attributes.Value&FileAttributes.ReparsePoint)!=0 || (attributes.Value&FileAttributes.Directory)==0)) throw new IOException("Reparse-point or non-directory installation paths are not supported"); }
  var target=ExistingAttributes(path); if(target.HasValue && (target.Value&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0) throw new IOException("Reparse-point files or directory destinations are not supported"); return path;
 }
 static string PrepareFile(string directory,string name) {
  string path=SafeFile(directory,name); Directory.CreateDirectory(Path.GetDirectoryName(path)); return SafeFile(directory,name);
 }
 static void RemoveFile(string directory,string name) { string path=SafeFile(directory,name); if(File.Exists(path)) File.Delete(path); }
 static void PruneEmptyDirectories(string directory,IEnumerable<string> files) {
  var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in files) { string name=RelativeName(file); for(int slash=name.LastIndexOf('/');slash>=0;slash=name.LastIndexOf('/')) { name=name.Substring(0,slash); parents.Add(name); } }
  foreach(var parent in parents.OrderByDescending(name=>name.Length)) {
   string path=Path.GetDirectoryName(SafeFile(directory,parent+"/.nr-directory-check"));
   if(Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length==0) Directory.Delete(path);
  }
 }
 static void Extract(string directory) {
  Directory.CreateDirectory(directory);
  foreach(var name in PayloadNames()) using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload."+name)) using(var unpacked=new GZipStream(input,CompressionMode.Decompress)) using(var output=new FileStream(PrepareFile(directory,name),FileMode.CreateNew,FileAccess.Write)) unpacked.CopyTo(output);
 }
 static string Hash(string file) { using(var sha=SHA256.Create()) using(var input=File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant(); }
 static void ValidateManifest(string directory) {
  string manifest=SafeFile(directory,"installed-files.txt"); if(!File.Exists(manifest)) throw new IOException("Installed-files manifest is missing");
  var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var line in File.ReadAllLines(manifest)) { var parts=line.Split('|'); if(parts.Length != 2 || parts[1].Length != 64 || !names.Add(RelativeName(parts[0]))) throw new IOException("Invalid installed-files manifest"); SafeFile(directory,parts[0]); }
 }
 static string[] ManifestFiles(string directory) { ValidateManifest(directory); return File.ReadAllLines(SafeFile(directory,"installed-files.txt")).Select(line=>RelativeName(line.Split('|')[0])).ToArray(); }
 static void MakeShortcut(string target) {
  Type type=Type.GetTypeFromProgID("WScript.Shell"); dynamic shell=Activator.CreateInstance(type); dynamic link=shell.CreateShortcut(Shortcut);
  try { link.TargetPath=target; link.WorkingDirectory=Path.GetDirectoryName(target); link.Description="Noise Reduction microphone settings"; link.Save(); } finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
  File.Delete(LegacyShortcut);
 }
 static RegistryEngine RegistryJournal(RegistryKey root, string name) { return new RegistryEngine(root,Path.Combine(DataRoot,name)); }
 static InstallTransaction ReadTransaction(string directory,string transaction) {
  InstallTransaction state; using(var stream=File.OpenRead(transaction)) state=(InstallTransaction)new XmlSerializer(typeof(InstallTransaction)).Deserialize(stream);
  Guid transactionId;
  if((state.Version!=2 && state.Version!=3) || !Guid.TryParse(state.Id,out transactionId) || !state.Complete || state.Files==null || state.EngineCheckpoint==null || state.SetupCheckpoint==null || state.FileCount!=state.Files.Length || state.EngineCount!=state.EngineCheckpoint.Length || state.SetupCount!=state.SetupCheckpoint.Length || state.FileCount==0) throw new IOException("Incomplete or unsupported installation transaction");
  if(state.Version==3 && (state.OriginalRunningServices==null || (state.GraphSnapshotTaken && !state.OriginalRunningServices.Contains("Audiosrv",StringComparer.OrdinalIgnoreCase)) || state.OriginalRunningServices.Any(name=>String.IsNullOrEmpty(name) || name.IndexOfAny(new[]{'\\','/','\0'})>=0))) throw new IOException("Incomplete installation service recovery snapshot");
  var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in state.Files) { if(file==null || !names.Add(RelativeName(file.Name))) throw new IOException("Invalid installation transaction ownership list"); SafeFile(directory,file.Name); }
  return state;
 }
 static void PublishTransaction(string transaction,InstallTransaction state) {
  string pending=transaction+".pending"; File.Delete(pending);
  using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { new XmlSerializer(typeof(InstallTransaction)).Serialize(stream,state); stream.Flush(true); }
  if(File.Exists(transaction)) File.Replace(pending,transaction,null); else File.Move(pending,transaction);
 }
 static void RollbackRegistry(InstallTransaction state) { using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) { RegistryJournal(root,"Journal").RestoreTransaction(state.Id); RegistryJournal(root,"SetupJournal").RestoreTransaction(state.Id); } }
 static void CleanupCommitted(string directory,string transaction,InstallTransaction state) {
  foreach(var file in state.Files) RemoveFile(directory,file.Name+".nr-backup");
  PruneEmptyDirectories(directory,state.Files.Select(file=>file.Name));
  File.Delete(transaction);
 }
 static void RestoreFiles(string directory,string transaction,InstallTransaction state,bool sandbox) {
  if(!sandbox) RollbackRegistry(state);
  foreach(var file in state.Files.Reverse()) { string destination=SafeFile(directory,file.Name),backup=SafeFile(directory,file.Name+".nr-backup"); if(File.Exists(backup)) { if(File.Exists(destination)) File.Replace(backup,destination,null); else File.Move(backup,PrepareFile(directory,file.Name)); } else if(!file.Existed) RemoveFile(directory,file.Name); }
  PruneEmptyDirectories(directory,state.Files.Select(file=>file.Name));
  state.FilesRestored=true; PublishTransaction(transaction,state);
 }
 static void GraphMaintenance(string transaction,InstallTransaction state,Action action,IAudioServices services,Func<long> milliseconds) {
  AudioGraphRefresh.Run(services,milliseconds,action,delegate(string[] running) { state.OriginalRunningServices=running; state.GraphSnapshotTaken=true; state.GraphRestoreRequired=true; PublishTransaction(transaction,state); },state.GraphSnapshotTaken ? state.OriginalRunningServices : null);
 }
 static void RecoverInstall(string directory,string transaction,bool sandbox,IAudioServices services=null,Func<long> milliseconds=null) {
  if(!File.Exists(transaction)) return;
  InstallTransaction state=ReadTransaction(directory,transaction);
  var clock=Stopwatch.StartNew(); milliseconds=milliseconds ?? delegate { return clock.ElapsedMilliseconds; };
  bool useGraph=(!sandbox || services!=null) && (state.GraphRestoreRequired || state.Files.Any(file=>file.Name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) && File.Exists(SafeFile(directory,file.Name+".nr-backup"))));
  services=services ?? new WindowsAudioServices();
  if(state.FilesRestored) { if(useGraph && state.GraphSnapshotTaken) AudioGraphRefresh.Restore(services,milliseconds,state.OriginalRunningServices); File.Delete(transaction); return; }
  Action recover=delegate { if(!sandbox && useGraph) WaitForPayloadRelease(directory,state.Files.Select(file=>file.Name).Concat(state.Files.Select(file=>file.Name+".nr-backup")),10000); if(state.Committed) { foreach(var file in state.Files) RemoveFile(directory,file.Name+".nr-backup"); PruneEmptyDirectories(directory,state.Files.Select(file=>file.Name)); } else RestoreFiles(directory,transaction,state,sandbox); };
  if(useGraph) GraphMaintenance(transaction,state,recover,services,milliseconds); else recover();
  File.Delete(transaction);
 }
 static void WaitForPayloadRelease(string directory,IEnumerable<string> names,int timeoutMilliseconds) {
  var clock=Stopwatch.StartNew();
  foreach(var name in names.Where(value=>value.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) || value.EndsWith(".dll.nr-backup",StringComparison.OrdinalIgnoreCase))) {
   string path=SafeFile(directory,name); if(!File.Exists(path)) continue;
   while(true) {
    try { using(var probe=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { } break; }
    catch(Exception error) {
     if(!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
     int remaining=timeoutMilliseconds-(int)clock.ElapsedMilliseconds;
     if(remaining<=0) throw new IOException("Close applications using "+name+" and retry the update; the original installation is preserved",error);
     // программа могла успеть запуститься после прошлой установки, закрываем её снова
     StopApp();
     Thread.Sleep(Math.Min(100,remaining));
    }
   }
  }
 }
 static void Install(string directory,bool sandbox,IAudioServices services=null,Func<long> milliseconds=null,Action<string> afterReplace=null) { Directory.CreateDirectory(directory);
  string[] names=PayloadNames().Concat(new[]{"NoiseReduction.Setup.exe","installed-files.txt"}).ToArray();
  string transaction=Path.Combine(directory,"install-transaction.txt");
  RecoverInstall(directory,transaction,sandbox,services,milliseconds);
  var owned=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  if(File.Exists(SafeFile(directory,"installed-files.txt"))) { foreach(var name in ManifestFiles(directory)) owned.Add(name); owned.Add("installed-files.txt"); }
  string[] obsolete=owned.Except(names,StringComparer.OrdinalIgnoreCase).ToArray();
  foreach(var name in names) if(File.Exists(SafeFile(directory,name)) && !owned.Contains(name)) throw new IOException("Installation destination contains an unowned conflicting file: "+name);
  foreach(var name in names.Concat(obsolete)) { SafeFile(directory,name); if(File.Exists(SafeFile(directory,name+".nr-backup"))) throw new IOException("An unfinished installation backup exists"); }
  if(!sandbox) Integration.PrepareDirectories();
  string stage=Path.Combine(directory,"stage-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
  bool committed=false; string previousTransaction=RegistryEngine.CurrentTransactionId;
  try {
   Step(10,T("Распаковка файлов"));
   Extract(stage); File.Copy(Assembly.GetExecutingAssembly().Location,SafeFile(stage,"NoiseReduction.Setup.exe"));
   File.WriteAllLines(SafeFile(stage,"installed-files.txt"),names.Where(n=>n!="installed-files.txt").Select(n=>n+"|"+Hash(SafeFile(stage,n))).ToArray());
   var state=new InstallTransaction { Id=Guid.NewGuid().ToString("N"),Version=3,Complete=true,OriginalRunningServices=new string[0],GraphRestoreRequired=owned.Contains("NoiseReduction.Apo.dll") && (!sandbox || services!=null),Files=names.Concat(obsolete).Select(name=>new InstallFileState { Name=name,Existed=File.Exists(SafeFile(directory,name)) }).ToArray(),EngineCheckpoint=new string[0],SetupCheckpoint=new string[0] };
   if(!sandbox) using(var registry=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) { state.EngineCheckpoint=RegistryJournal(registry,"Journal").Checkpoint(); state.SetupCheckpoint=RegistryJournal(registry,"SetupJournal").Checkpoint(); }
   state.FileCount=state.Files.Length; state.EngineCount=state.EngineCheckpoint.Length; state.SetupCount=state.SetupCheckpoint.Length;
   PublishTransaction(transaction,state);
   RegistryEngine.CurrentTransactionId=state.Id;
   Action replace=delegate {
    try {
    Step(45,T("Замена файлов, звук Windows ненадолго прервётся"));
    if(!sandbox && state.GraphRestoreRequired) WaitForPayloadRelease(directory,names.Concat(obsolete),10000);
    foreach(var name in names) { string destination=PrepareFile(directory,name); if(File.Exists(destination)) File.Replace(SafeFile(stage,name),destination,SafeFile(directory,name+".nr-backup")); else File.Move(SafeFile(stage,name),destination); if(afterReplace!=null) afterReplace(name); }
    foreach(var name in obsolete) { string destination=SafeFile(directory,name),backup=SafeFile(directory,name+".nr-backup"); if(File.Exists(backup)) throw new IOException("An unfinished obsolete-file backup exists"); if(File.Exists(destination)) File.Move(destination,backup); if(afterReplace!=null) afterReplace(name); }
   if(!sandbox) { Step(65,T("Регистрация в Windows")); Integration.RegisterEngine(directory);
   using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) {
    var e=new RegistryEngine(root,Path.Combine(DataRoot,"SetupJournal")); e.Recover();
    e.Set(UninstallKey,"DisplayName","Noise Reduction",RegistryValueKind.String); e.Set(UninstallKey,"DisplayVersion","1.1.0",RegistryValueKind.String);
    e.Set(UninstallKey,"EstimatedSize",(int)(new DirectoryInfo(directory).EnumerateFiles("*",SearchOption.AllDirectories).Sum(file=>file.Length)/1024),RegistryValueKind.DWord);
    e.Set(UninstallKey,"Publisher","Noise Reduction",RegistryValueKind.String); e.Set(UninstallKey,"InstallLocation",directory,RegistryValueKind.String);
    e.Set(UninstallKey,"UninstallString","\""+Path.Combine(directory,"NoiseReduction.Setup.exe")+"\" --uninstall",RegistryValueKind.String);
    e.Set(UninstallKey,"QuietUninstallString","\""+Path.Combine(directory,"NoiseReduction.Setup.exe")+"\" --uninstall --silent",RegistryValueKind.String);
    e.Set(UninstallKey,"DisplayIcon",Path.Combine(directory,"NoiseReduction.exe"),RegistryValueKind.String); e.Set(UninstallKey,"NoModify",1,RegistryValueKind.DWord); e.Set(UninstallKey,"NoRepair",1,RegistryValueKind.DWord);
   }
   MakeShortcut(Path.Combine(directory,"NoiseReduction.exe"));
   }
    } catch(Exception replacementError) { try { RestoreFiles(directory,transaction,state,sandbox); } catch(Exception rollbackError) { throw new AggregateException("Installation replacement and rollback failed",replacementError,rollbackError); } throw; }
   };
   if(state.GraphRestoreRequired) { var clock=Stopwatch.StartNew(); GraphMaintenance(transaction,state,replace,services ?? new WindowsAudioServices(),milliseconds ?? delegate { return clock.ElapsedMilliseconds; }); }
   else replace();
   state.Committed=true; PublishTransaction(transaction,state); committed=true; CleanupCommitted(directory,transaction,state);
   // пакет эффектов ставим после замены файлов, когда звук Windows снова запущен, без него остальное работает
   if(!sandbox) Step(80,T("Установка эффекта для USB микрофонов"));
   if(!sandbox) try { EffectPack.Install(directory); } catch(Exception packError) { try { File.AppendAllText(Path.Combine(DataRoot,"setup-errors.log"),DateTime.UtcNow.ToString("o")+" effect pack: "+packError+Environment.NewLine); } catch { } }
  } catch {
   if(!committed) RecoverInstall(directory,transaction,sandbox,services,milliseconds);
   throw;
  } finally { RegistryEngine.CurrentTransactionId=previousTransaction; foreach(var name in names) RemoveFile(stage,name); PruneEmptyDirectories(stage,names); if(Directory.Exists(stage) && Directory.GetFileSystemEntries(stage).Length==0) Directory.Delete(stage); }
 }
 static void Uninstall(string directory,bool sandbox) {
  RecoverInstall(directory,SafeFile(directory,"install-transaction.txt"),sandbox);
  var names=ManifestFiles(directory);
  if(!sandbox) {
  Step(10,T("Отключение эффектов от микрофонов"));
  bool hadEffects=Integration.EnumerateMicrophones().Any(device=>device.Attached);
  Integration.DetachAll();
  Step(30,T("Перезапуск звука Windows"));
  if(hadEffects) Integration.RefreshAudioGraph();
  Step(55,T("Удаление пакета эффекта"));
  EffectPack.Uninstall();
  Step(75,T("Удаление файлов"));
  // процесс, запустивший удаление, и звук Windows отпускают файлы не мгновенно
  for(int attempt=0;;attempt++) { try { foreach(var name in names) { string file=SafeFile(directory,name); if(File.Exists(file)) using(var probe=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { } } break; } catch(IOException) { if(attempt>=30) break; StopApp(); Thread.Sleep(500); } }
  try { foreach(var name in names) { string file=SafeFile(directory,name); if(File.Exists(file)) using(var probe=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { } } } catch(IOException ex) { throw new IOException("Microphone effects were detached. Close NoiseReduction and other microphone applications, then retry uninstall",ex); }
  Integration.UnregisterEngine();
  using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) { var e=new RegistryEngine(root,Path.Combine(DataRoot,"SetupJournal")); e.Recover(); e.RestorePrefix(UninstallKey); using(var key=root.OpenSubKey(UninstallKey)) if(key != null && key.ValueCount==0 && key.SubKeyCount==0) { key.Dispose(); root.DeleteSubKey(UninstallKey,false); } }
  File.Delete(Shortcut); File.Delete(LegacyShortcut);
  using(var startup=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)) if(startup!=null) { string value=startup.GetValue("NoiseReduction") as string; string expected="\""+Path.Combine(directory,"NoiseReduction.exe")+"\" --tray"; if(String.Equals(value,expected,StringComparison.OrdinalIgnoreCase)) startup.DeleteValue("NoiseReduction",false); }
  // журналы отката пусты, значит всё возвращено и служебная папка больше не нужна
  if(!Directory.EnumerateFiles(DataRoot,"*.xml",SearchOption.AllDirectories).Any()) try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \""+DataRoot+"\"") { UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=Environment.SystemDirectory }); } catch { }
  }
  foreach(var name in names) { string file=SafeFile(directory,name); if(File.Exists(file)) File.Delete(file); }
  PruneEmptyDirectories(directory,names);
  File.Delete(SafeFile(directory,"installed-files.txt"));
  if(Directory.GetFileSystemEntries(directory).Length==0) Directory.Delete(directory);
 }
 static string WorkerDirectory() { return Path.Combine(DataRoot,"Uninstall-"+Guid.NewGuid().ToString("N")); }
 static DirectorySecurity WorkerAccess() {
  var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false); acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
  foreach(var principal in new[]{WellKnownSidType.BuiltinAdministratorsSid,WellKnownSidType.LocalSystemSid}) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
  return acl;
 }
 static string StageUninstallWorker() {
  Integration.PrepareDirectories();
  string directory=WorkerDirectory(); SafeFile(directory,"NoiseReduction.Setup.exe"); Directory.CreateDirectory(directory); Directory.SetAccessControl(directory,WorkerAccess());
  string executable=SafeFile(directory,"NoiseReduction.Setup.exe"); File.Copy(Assembly.GetExecutingAssembly().Location,executable);
  if(Hash(executable)!=Hash(Assembly.GetExecutingAssembly().Location)) throw new IOException("Uninstall worker integrity verification failed");
  return executable;
 }
 static void RequireAdmin() { if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Run setup as administrator"); }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateSymbolicLink(string link,string target,uint flags);
 static void TestPaths(string directory) {
  foreach(var name in new[]{"../outside","a/../../outside","a/./file","a//file","a\\\\file","/absolute","C:/absolute","C:relative",@"\\server\share\file","a/file:stream","a/CON.txt","a/CON .txt","a/CONOUT$","a/LPT1","a/NUL.ext","a/trailing.","a/trailing ",".",""}) {
   bool rejected=false; try { SafeFile(directory,name); } catch(IOException) { rejected=true; } if(!rejected) throw new Exception("Unsafe package path accepted: "+name);
  }
  string victim=Path.Combine(directory,"path-victim"),alias=Path.Combine(directory,"path-link"); Directory.CreateDirectory(victim); File.WriteAllText(Path.Combine(victim,"keep.txt"),"unchanged");
  try {
   if(CreateSymbolicLink(alias,victim,3) || CreateSymbolicLink(alias,victim,1)) {
    try { bool rejected=false; try { PrepareFile(directory,"path-link/nested/new.txt"); } catch(IOException) { rejected=true; } if(!rejected || Directory.Exists(Path.Combine(victim,"nested"))) throw new Exception("Nested package path followed a directory link"); }
    finally { Directory.Delete(alias); }
   } else { int error=Marshal.GetLastWin32Error(); if(error!=1314 && error!=5) throw new System.ComponentModel.Win32Exception(error); Console.WriteLine("SKIP package symlink fixture: privilege unavailable"); }
   if(File.ReadAllText(Path.Combine(victim,"keep.txt"))!="unchanged") throw new Exception("Package path validation changed unrelated data");
  } finally { File.Delete(Path.Combine(victim,"keep.txt")); Directory.Delete(victim); }
 }
 sealed class SandboxAudioServices : IAudioServices {
  internal readonly Dictionary<string,ServiceControllerStatus> States=new Dictionary<string,ServiceControllerStatus> { {"Audiosrv",ServiceControllerStatus.Running},{"Rtk",ServiceControllerStatus.Running},{"Idle",ServiceControllerStatus.Stopped} };
  internal readonly List<string> Calls=new List<string>(); internal Action OnAudioStart;
  public string[] Dependents(string name) { return name=="Audiosrv" ? new[]{"Rtk","Idle"} : new string[0]; }
  public ServiceControllerStatus Status(string name) { return States[name]; }
  public void Stop(string name) { Calls.Add("stop:"+name); States[name]=ServiceControllerStatus.StopPending; }
  public void Start(string name) { Calls.Add("start:"+name); if(name=="Audiosrv" && OnAudioStart!=null) OnAudioStart(); States[name]=ServiceControllerStatus.StartPending; }
  public void Wait(string name,ServiceControllerStatus desired,TimeSpan timeout) { if(timeout<=TimeSpan.Zero || timeout>TimeSpan.FromSeconds(30)) throw new Exception("Unbounded sandbox service wait"); States[name]=desired; }
 }
 static InstallTransaction SandboxTransaction(string name,bool committed) { return new InstallTransaction{ Id=Guid.NewGuid().ToString("N"),Version=3,Complete=true,Committed=committed,GraphRestoreRequired=true,GraphSnapshotTaken=true,OriginalRunningServices=new[]{"Rtk","Audiosrv"},FileCount=1,EngineCount=0,SetupCount=0,EngineCheckpoint=new string[0],SetupCheckpoint=new string[0],Files=new[]{new InstallFileState{Name=name,Existed=true}}}; }
 static int SelfTest() {
  string directory=Path.Combine(Path.GetTempPath(),"nr-package-"+Guid.NewGuid().ToString("N"));
  try { Extract(directory); var names=PayloadNames(); foreach(var required in new[]{"NoiseReduction.exe","NoiseReduction.dll","NoiseReduction.Engine.exe","NoiseReduction.Engine.exe.config","NoiseReduction.deps.json","NoiseReduction.runtimeconfig.json","NoiseReduction.pri","NoiseReduction.ico","Microphone.png","NoiseReduction.Apo.dll","rnnoise.dll"}) if(!names.Contains(required,StringComparer.OrdinalIgnoreCase)) throw new Exception("Required payload missing: "+required);
   var allowedJson=new[]{"NoiseReduction.deps.json","NoiseReduction.runtimeconfig.json","workloads.json","workloads.365.json","workloads.j32.json","workloads.lnl.json","workloads.qnn.json","workloads.stx.json"};
   if(names.Any(n=>new[]{".mp3",".wav",".flac",".ogg",".m4a",".aac",".pcm",".cs",".cpp",".h",".ps1",".pdb",".py",".zip"}.Contains(Path.GetExtension(n).ToLowerInvariant()) || (Path.GetExtension(n).Equals(".json",StringComparison.OrdinalIgnoreCase) && !allowedJson.Contains(n,StringComparer.OrdinalIgnoreCase)))) throw new Exception("Unexpected private/audio/source payload");
   string nested=names.FirstOrDefault(name=>name.IndexOf('/')>=0); if(nested==null) throw new Exception("WinUI localized runtime payload is missing");
   foreach(var line in File.ReadAllLines(SafeFile(directory,"payload-sha256.txt"))) { var parts=line.Split('|'); if(parts.Length!=2 || Hash(SafeFile(directory,parts[0]))!=parts[1]) throw new Exception("Extracted payload hash mismatch"); }
   TestPaths(directory);
   foreach(var name in names) File.Delete(SafeFile(directory,name));
   PruneEmptyDirectories(directory,names);
   Install(directory,true); ValidateManifest(directory);
   File.WriteAllText(SafeFile(directory,"user-recording.wav"),"keep me");
   string nestedRecording=RelativeName(nested).Substring(0,nested.LastIndexOf('/')+1)+"user-recording.wav"; File.WriteAllText(PrepareFile(directory,nestedRecording),"keep nested recording");
   Install(directory,true); ValidateManifest(directory);
   var expected=ManifestFiles(directory).ToDictionary(n=>n,n=>Hash(SafeFile(directory,n)));
   bool failed=false;
   using(var locked=new FileStream(SafeFile(directory,"rnnoise.dll"),FileMode.Open,FileAccess.Read,FileShare.Read)) { try { Install(directory,true); } catch(IOException) { failed=true; } }
   if(!failed || expected.Any(pair=>Hash(SafeFile(directory,pair.Key))!=pair.Value)) throw new Exception("Failed upgrade did not restore prior files");
   string interrupted=SafeFile(directory,"install-transaction.txt"); string app=SafeFile(directory,"NoiseReduction.exe"),backup=SafeFile(directory,"NoiseReduction.exe.nr-backup");
   File.Copy(app,backup); PublishTransaction(interrupted,new InstallTransaction{ Id=Guid.NewGuid().ToString("N"),Version=2,Complete=true,FileCount=2,EngineCount=0,SetupCount=0,EngineCheckpoint=new string[0],SetupCheckpoint=new string[0],Files=new[]{new InstallFileState{Name="NoiseReduction.exe",Existed=true},new InstallFileState{Name="partial-new.txt",Existed=false}}}); File.WriteAllText(app,"interrupted"); File.WriteAllText(SafeFile(directory,"partial-new.txt"),"partial");
   RecoverInstall(directory,interrupted,true); if(Hash(app)!=expected["NoiseReduction.exe"] || File.Exists(SafeFile(directory,"partial-new.txt"))) throw new Exception("Interrupted install recovery failed");
   string nestedFile=SafeFile(directory,nested),nestedBackup=SafeFile(directory,nested+".nr-backup"); File.Copy(nestedFile,nestedBackup); File.WriteAllText(nestedFile,"interrupted nested replacement"); PublishTransaction(interrupted,SandboxTransaction(nested,false));
   RecoverInstall(directory,interrupted,true); if(Hash(nestedFile)!=expected[nested] || File.Exists(nestedBackup)) throw new Exception("Nested v3 rollback did not restore the owned runtime file");
   File.Copy(app,backup); File.WriteAllText(app,"committed new payload"); string committedHash=Hash(app);
   PublishTransaction(interrupted,new InstallTransaction{ Id=Guid.NewGuid().ToString("N"),Version=2,Complete=true,Committed=true,FileCount=1,EngineCount=0,SetupCount=0,EngineCheckpoint=new string[0],SetupCheckpoint=new string[0],Files=new[]{new InstallFileState{Name="NoiseReduction.exe",Existed=true}}});
   bool cleanupBlocked=false;
   using(var lockedBackup=new FileStream(backup,FileMode.Open,FileAccess.Read,FileShare.Read)) { try { RecoverInstall(directory,interrupted,true); } catch(IOException) { cleanupBlocked=true; } }
   if(!cleanupBlocked || !File.Exists(interrupted) || Hash(app)!=committedHash) throw new Exception("Committed cleanup failure lost new payload or recovery record");
   RecoverInstall(directory,interrupted,true); if(File.Exists(backup) || File.Exists(interrupted) || Hash(app)!=committedHash) throw new Exception("Committed recovery rolled back new payload");
   Install(directory,true); if(Hash(app)!=expected["NoiseReduction.exe"]) throw new Exception("Committed cleanup prevented upgrade retry");
   bool dllLockRejected=false;
   using(var heldDll=new FileStream(SafeFile(directory,"NoiseReduction.Apo.dll"),FileMode.Open,FileAccess.Read,FileShare.Read)) { try { WaitForPayloadRelease(directory,new[]{"NoiseReduction.Apo.dll"},1); } catch(IOException) { dllLockRejected=true; } }
   if(!dllLockRejected) throw new Exception("Mapped DLL preflight did not enforce its bounded wait"); WaitForPayloadRelease(directory,new[]{"NoiseReduction.Apo.dll"},10);
   string apo=SafeFile(directory,"NoiseReduction.Apo.dll"); File.WriteAllText(apo,"previous installed native revision"); string oldApoHash=Hash(apo);
   var beforeFailure=ManifestFiles(directory).ToDictionary(name=>name,name=>Hash(SafeFile(directory,name)));
   var audio=new SandboxAudioServices(); bool injected=false;
   audio.OnAudioStart=delegate { if(Hash(apo)!=oldApoHash) throw new Exception("Old native payload was not restored before restarting audio"); };
   try { Install(directory,true,audio,delegate{return 0;},delegate(string name) { if(audio.States["Audiosrv"]!=ServiceControllerStatus.Stopped) throw new Exception("Replacement occurred with audio running"); var pending=ReadTransaction(directory,interrupted); if(!pending.GraphSnapshotTaken || !pending.OriginalRunningServices.Contains("Rtk")) throw new Exception("Original running services were not durable before replacement"); if(name=="NoiseReduction.Apo.dll") throw new IOException("injected failure after native replacement"); }); } catch(InvalidOperationException) { injected=true; }
   if(!injected || beforeFailure.Any(pair=>Hash(SafeFile(directory,pair.Key))!=pair.Value) || audio.States["Rtk"]!=ServiceControllerStatus.Running || File.Exists(interrupted)) throw new Exception("Stopped upgrade rollback did not restore old files and services");
   audio=new SandboxAudioServices(); Install(directory,true,audio,delegate{return 0;},delegate(string name) { if(audio.States["Audiosrv"]!=ServiceControllerStatus.Stopped) throw new Exception("Successful upgrade replaced files outside stopped window"); });
   expected=ManifestFiles(directory).ToDictionary(name=>name,name=>Hash(SafeFile(directory,name)));
   File.Copy(app,backup); File.WriteAllText(app,"process lost while graph stopped"); PublishTransaction(interrupted,SandboxTransaction("NoiseReduction.exe",false));
   audio=new SandboxAudioServices(); audio.States["Audiosrv"]=audio.States["Rtk"]=ServiceControllerStatus.Stopped;
   audio.OnAudioStart=delegate { if(Hash(app)!=expected["NoiseReduction.exe"]) throw new Exception("Crash recovery restarted audio before restoring old files"); };
   RecoverInstall(directory,interrupted,true,audio,delegate{return 0;});
   if(File.Exists(interrupted) || audio.States["Rtk"]!=ServiceControllerStatus.Running || audio.Calls.Contains("start:Idle")) throw new Exception("Crash recovery did not restore the durable original service snapshot");
   string apoBackup=SafeFile(directory,"NoiseReduction.Apo.dll.nr-backup"); File.Copy(apo,apoBackup); string newApoHash=Hash(apo); PublishTransaction(interrupted,SandboxTransaction("NoiseReduction.Apo.dll",true));
   audio=new SandboxAudioServices(); audio.OnAudioStart=delegate { if(File.Exists(apoBackup) || Hash(apo)!=newApoHash) throw new Exception("Committed cleanup did not unload and remove backup before audio restart"); };
   RecoverInstall(directory,interrupted,true,audio,delegate{return 0;}); if(File.Exists(interrupted) || !audio.Calls.Contains("stop:Audiosrv")) throw new Exception("Committed DLL backup cleanup skipped graph unloading");
   string obsolete="obsolete-locale/previous.resources.dll",obsoletePath=PrepareFile(directory,obsolete); File.WriteAllText(obsoletePath,"old owned runtime"); string obsoleteHash=Hash(obsoletePath); File.AppendAllText(SafeFile(directory,"installed-files.txt"),obsolete+"|"+obsoleteHash+Environment.NewLine);
   bool obsoleteFailure=false; try { Install(directory,true,null,null,delegate(string name) { if(name==obsolete) throw new IOException("injected obsolete removal failure"); }); } catch(IOException) { obsoleteFailure=true; }
   if(!obsoleteFailure || Hash(obsoletePath)!=obsoleteHash || !ManifestFiles(directory).Contains(obsolete)) throw new Exception("Failed upgrade lost an obsolete owned runtime file");
   Install(directory,true); if(File.Exists(obsoletePath) || Directory.Exists(Path.GetDirectoryName(obsoletePath)) || ManifestFiles(directory).Contains(obsolete)) throw new Exception("Upgrade left an obsolete owned runtime file behind");
   string ownedManifest=SafeFile(directory,"installed-files.txt"), savedManifest=File.ReadAllText(ownedManifest); string originalModelHash=Hash(SafeFile(directory,"rnnoise.dll"));
   File.WriteAllLines(ownedManifest,File.ReadAllLines(ownedManifest).Where(line=>!line.StartsWith("rnnoise.dll|",StringComparison.Ordinal)).ToArray());
   bool ownershipRejected=false; try { Install(directory,true); } catch(IOException) { ownershipRejected=true; }
   if(!ownershipRejected || Hash(SafeFile(directory,"rnnoise.dll"))!=originalModelHash) throw new Exception("Upgrade overwrote an unowned incoming destination"); File.WriteAllText(ownedManifest,savedManifest);
   File.WriteAllText(interrupted,"<InstallTransaction><Id>"+Guid.NewGuid().ToString("N")+"</Id><Version>2</Version><Complete>true</Complete></InstallTransaction>");
   bool incompleteRejected=false; try { RecoverInstall(directory,interrupted,true); } catch(IOException) { incompleteRejected=true; }
   if(!incompleteRejected || Hash(app)!=expected["NoiseReduction.exe"]) throw new Exception("Incomplete checkpoint journal changed installed files"); File.Delete(interrupted);
   File.WriteAllText(interrupted+".pending","<InstallTransaction><truncated"); Install(directory,true);
   File.Copy(app,backup); PublishTransaction(interrupted,new InstallTransaction{ Id=Guid.NewGuid().ToString("N"),Version=2,Complete=true,FileCount=1,EngineCount=0,SetupCount=0,EngineCheckpoint=new string[0],SetupCheckpoint=new string[0],Files=new[]{new InstallFileState{Name="NoiseReduction.exe",Existed=true}}}); File.WriteAllText(app,"interrupted before uninstall");
   Uninstall(directory,true); if(File.Exists(backup) || File.Exists(interrupted)) throw new Exception("Uninstall skipped interrupted install recovery");
   if(File.ReadAllText(SafeFile(directory,"user-recording.wav"))!="keep me") throw new Exception("User file lost"); File.Delete(SafeFile(directory,"user-recording.wav"));
   if(File.ReadAllText(SafeFile(directory,nestedRecording))!="keep nested recording") throw new Exception("Nested unrelated user recording lost"); File.Delete(SafeFile(directory,nestedRecording)); PruneEmptyDirectories(directory,new[]{nestedRecording});
   var workerAcl=WorkerAccess(); var workerRules=workerAcl.GetAccessRules(true,true,typeof(SecurityIdentifier));
   if(!workerAcl.AreAccessRulesProtected || workerRules.Count!=2 || !workerAcl.GetOwner(typeof(SecurityIdentifier)).Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null))) throw new Exception("Uninstall worker directory is not protected");
   foreach(FileSystemAccessRule rule in workerRules) if(rule.AccessControlType!=AccessControlType.Allow || rule.FileSystemRights!=FileSystemRights.FullControl || (!rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)) && !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)))) throw new Exception("Uninstall worker allows unprivileged write access");
   if(!WorkerDirectory().StartsWith(Path.GetFullPath(DataRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Uninstall worker staged outside protected ProgramData root");
   bool rejected=false; try { SafeFile(directory,"..\\outside"); } catch(IOException) { rejected=true; } if(!rejected) throw new Exception("Path validation failed");
   Console.WriteLine("PASS nested package extraction, upgrade/failure/crash rollback, v2/v3 recovery, obsolete runtime removal, checkpoint validation, committed cleanup retry, stopped upgrade rollback/service recovery, ownership conflict, uninstall preservation, traversal/reparse validation: "+names.Length+" files"); return 0;
  } finally { foreach(var name in PayloadNames()) RemoveFile(directory,name); PruneEmptyDirectories(directory,PayloadNames()); if(Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length==0) Directory.Delete(directory); }
 }
 // установленная копия держит файлы, перед заменой её нужно закрыть
 static void StopApp() {
  string target=Path.Combine(InstallRoot,"NoiseReduction.exe");
  foreach(var process in Process.GetProcessesByName("NoiseReduction")) using(process) {
   try { if(!String.Equals(process.MainModule.FileName,target,StringComparison.OrdinalIgnoreCase)) continue; process.Kill(); process.WaitForExit(10000); } catch(Exception) { }
  }
 }
 // explorer запускает программу без повышенных прав, как обычный ярлык
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 static string StartupValue() { using(var key=Registry.CurrentUser.OpenSubKey(RunKey)) return key==null ? null : key.GetValue("NoiseReduction") as string; }
 // автозапуск того же вида, что ставит сама программа
 static void SetStartup(bool enabled) {
  using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) { if(enabled) key.SetValue("NoiseReduction","\""+Path.Combine(InstallRoot,"NoiseReduction.exe")+"\" --tray"); else key.DeleteValue("NoiseReduction",false); }
 }
 static void StartApp() {
  string target=Path.Combine(InstallRoot,"NoiseReduction.exe");
  if(File.Exists(target)) try { using(Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"\""+target+"\""))) { } } catch(Exception) { }
 }
 [STAThread] static int Main(string[] args) {
  silent=args.Contains("--silent");
  try {
   if(args.Length>0 && args[0]=="--extract") { if(args.Length!=2) throw new ArgumentException("Usage: --extract <empty-directory>"); Extract(Path.GetFullPath(args[1])); return 0; }
   if(args.Contains("--self-test")) return SelfTest();
   if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) { var elevated=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,String.Join(" ",args.Select(a=>"\""+a+"\""))) { UseShellExecute=true,Verb="runas" }); elevated.WaitForExit(); return elevated.ExitCode; }
   RequireAdmin();
   if(args.Contains("--uninstall-worker")) { int index=Array.IndexOf(args,"--uninstall-worker"); int pid=Int32.Parse(args[index+1]); try { using(var parent=Process.GetProcessById(pid)) if(!parent.WaitForExit(30000)) throw new IOException("Original setup process did not exit"); } catch(ArgumentException) { } }
   bool remove=args.Contains("--uninstall") || args.Contains("--uninstall-worker");
   bool worker=args.Contains("--uninstall-worker");
   // установленная копия не может удалить сама себя, удаление продолжает её временная копия
   if(args.Contains("--uninstall") && String.Equals(Path.GetFullPath(Assembly.GetExecutingAssembly().Location),Path.Combine(InstallRoot,"NoiseReduction.Setup.exe"),StringComparison.OrdinalIgnoreCase)) {
    Action<Action<int,string>> handOff=delegate {
     using(var operation = new Integration.OperationLease()) {
      string temp=StageUninstallWorker();
      var process=Process.Start(new ProcessStartInfo(temp,"--uninstall-worker "+Process.GetCurrentProcess().Id+(silent?" --silent":"")) {UseShellExecute=false,CreateNoWindow=true}); if(process==null) throw new IOException("Cannot launch uninstall worker");
     }
    };
    if(silent) { handOff(delegate { }); return 0; }
    return SetupWindow.Run(T("Удаление Noise Reduction"),T("Эффекты микрофонов вернутся к настройкам Windows"),T("Удалить"),true,null,handOff) ? 0 : 1;
   }
   Action<Action<int,string>> work=report => {
    Step=report;
    using(var operation = new Integration.OperationLease()) { StopApp(); if(remove) Uninstall(InstallRoot,false); else Install(InstallRoot,false); }
   };
   if(silent) work(delegate { });
   else if(remove) { if(!SetupWindow.Run(T("Удаление Noise Reduction"),T("Эффекты микрофонов вернутся к настройкам Windows"),T("Удалить"),!worker,T("Noise Reduction удалён"),work)) return 1; }
   else {
    bool installed=File.Exists(Path.Combine(InstallRoot,"NoiseReduction.exe"));
    string heading=installed?T("Переустановка Noise Reduction"):T("Установка Noise Reduction");
    string question=installed?T("Noise Reduction уже установлен и будет переустановлен. Настройки микрофонов сохранятся"):T("Шумоподавление для всех микрофонов в любых программах. Защита звука Windows остаётся включённой");
    bool startup=!installed || StartupValue()!=null;
    if(!SetupWindow.Run(heading,question,installed?T("Переустановить"):T("Установить"),true,null,T("Запускать при входе в Windows"),startup,report => { work(report); SetStartup(SetupWindow.OptionChecked); })) return 1;
   }
   if(!remove) StartApp();
   return 0;
  } catch(Exception ex) { Console.Error.WriteLine(args.Contains("--self-test") ? ex.ToString() : ex.Message); try { if(!args.Contains("--self-test") && !args.Contains("--extract")) { Directory.CreateDirectory(DataRoot); File.AppendAllText(Path.Combine(DataRoot,"setup-errors.log"),DateTime.UtcNow.ToString("o")+" "+ex+Environment.NewLine); } } catch { } if(!silent) MessageBox.Show(ex.Message,"Noise Reduction setup",MessageBoxButtons.OK,MessageBoxIcon.Error); return 2; }
 }
}
