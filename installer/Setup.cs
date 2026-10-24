using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

[assembly: AssemblyTitle("NoiseReduction Setup")]
[assembly: AssemblyVersion("1.0.0.0")]
public class InstallFileState { public string Name; public bool Existed; }
public class InstallTransaction {
 public string Id; public int Version; public bool Complete,Committed; public int FileCount=-1,EngineCount=-1,SetupCount=-1;
 public bool GraphRestoreRequired,GraphSnapshotTaken,FilesRestored; public string[] OriginalRunningServices;
 public InstallFileState[] Files; public string[] EngineCheckpoint,SetupCheckpoint;
}
class Setup {
 const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NoiseReduction";
 static bool silent;
 static string InstallRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NoiseReduction"); } }
 static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),"NoiseReduction.lnk"); } }
 static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"NoiseReduction"); } }
 static string[] PayloadNames() { return Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n=>n.StartsWith("payload.",StringComparison.Ordinal)).Select(n=>n.Substring(8)).ToArray(); }
 static string SafeFile(string directory,string name) {
  if(String.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("Unsafe package filename");
  string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
  string path=Path.GetFullPath(Path.Combine(root,name)); if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new IOException("Package path escapes destination");
  for(var current=new DirectoryInfo(root);current != null;current=current.Parent) if(current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point installation paths are not supported");
  if(File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point files are not supported"); return path;
 }
 static void Extract(string directory) {
  Directory.CreateDirectory(directory);
  foreach(var name in PayloadNames()) using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload."+name)) using(var output=new FileStream(SafeFile(directory,name),FileMode.CreateNew,FileAccess.Write)) input.CopyTo(output);
 }
 static string Hash(string file) { using(var sha=SHA256.Create()) using(var input=File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant(); }
 static void ValidateManifest(string directory) {
  string manifest=SafeFile(directory,"installed-files.txt"); if(!File.Exists(manifest)) throw new IOException("Installed-files manifest is missing");
  foreach(var line in File.ReadAllLines(manifest)) { var parts=line.Split('|'); if(parts.Length != 2 || parts[1].Length != 64) throw new IOException("Invalid installed-files manifest"); SafeFile(directory,parts[0]); }
 }
 static string[] ManifestFiles(string directory) { ValidateManifest(directory); return File.ReadAllLines(SafeFile(directory,"installed-files.txt")).Select(line=>line.Split('|')[0]).ToArray(); }
 static void MakeShortcut(string target) {
  Type type=Type.GetTypeFromProgID("WScript.Shell"); dynamic shell=Activator.CreateInstance(type); dynamic link=shell.CreateShortcut(Shortcut);
  try { link.TargetPath=target; link.WorkingDirectory=Path.GetDirectoryName(target); link.Description="NoiseReduction microphone settings"; link.Save(); } finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
 }
 static RegistryEngine RegistryJournal(RegistryKey root, string name) { return new RegistryEngine(root,Path.Combine(DataRoot,name)); }
 static InstallTransaction ReadTransaction(string directory,string transaction) {
  InstallTransaction state; using(var stream=File.OpenRead(transaction)) state=(InstallTransaction)new XmlSerializer(typeof(InstallTransaction)).Deserialize(stream);
  Guid transactionId;
  if((state.Version!=2 && state.Version!=3) || !Guid.TryParse(state.Id,out transactionId) || !state.Complete || state.Files==null || state.EngineCheckpoint==null || state.SetupCheckpoint==null || state.FileCount!=state.Files.Length || state.EngineCount!=state.EngineCheckpoint.Length || state.SetupCount!=state.SetupCheckpoint.Length || state.FileCount==0) throw new IOException("Incomplete or unsupported installation transaction");
  if(state.Version==3 && (state.OriginalRunningServices==null || (state.GraphSnapshotTaken && !state.OriginalRunningServices.Contains("Audiosrv",StringComparer.OrdinalIgnoreCase)) || state.OriginalRunningServices.Any(name=>String.IsNullOrEmpty(name) || name.IndexOfAny(new[]{'\\','/','\0'})>=0))) throw new IOException("Incomplete installation service recovery snapshot");
  var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in state.Files) { if(file==null || !names.Add(file.Name)) throw new IOException("Invalid installation transaction ownership list"); SafeFile(directory,file.Name); }
  return state;
 }
 static void PublishTransaction(string transaction,InstallTransaction state) {
  string pending=transaction+".pending"; File.Delete(pending);
  using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { new XmlSerializer(typeof(InstallTransaction)).Serialize(stream,state); stream.Flush(true); }
  if(File.Exists(transaction)) File.Replace(pending,transaction,null); else File.Move(pending,transaction);
 }
 static void RollbackRegistry(InstallTransaction state) { using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) { RegistryJournal(root,"Journal").RestoreTransaction(state.Id); RegistryJournal(root,"SetupJournal").RestoreTransaction(state.Id); } }
 static void CleanupCommitted(string directory,string transaction,InstallTransaction state) {
  foreach(var file in state.Files) File.Delete(SafeFile(directory,file.Name+".nr-backup"));
  File.Delete(transaction);
 }
 static void RestoreFiles(string directory,string transaction,InstallTransaction state,bool sandbox) {
  if(!sandbox) RollbackRegistry(state);
  foreach(var file in state.Files.Reverse()) { string destination=SafeFile(directory,file.Name),backup=SafeFile(directory,file.Name+".nr-backup"); if(File.Exists(backup)) { if(File.Exists(destination)) File.Replace(backup,destination,null); else File.Move(backup,destination); } else if(!file.Existed) File.Delete(destination); }
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
  Action recover=delegate { if(!sandbox && useGraph) WaitForPayloadRelease(directory,state.Files.Select(file=>file.Name).Concat(state.Files.Select(file=>file.Name+".nr-backup")),10000); if(state.Committed) { foreach(var file in state.Files) File.Delete(SafeFile(directory,file.Name+".nr-backup")); } else RestoreFiles(directory,transaction,state,sandbox); };
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
  foreach(var name in names) if(File.Exists(SafeFile(directory,name)) && !owned.Contains(name)) throw new IOException("Installation destination contains an unowned conflicting file: "+name);
  foreach(var name in names) { SafeFile(directory,name); if(File.Exists(SafeFile(directory,name+".nr-backup"))) throw new IOException("An unfinished installation backup exists"); }
  if(!sandbox) Integration.PrepareDirectories();
  string stage=Path.Combine(directory,"stage-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
  bool committed=false; string previousTransaction=RegistryEngine.CurrentTransactionId;
  try {
   Extract(stage); File.Copy(Assembly.GetExecutingAssembly().Location,SafeFile(stage,"NoiseReduction.Setup.exe"));
   File.WriteAllLines(SafeFile(stage,"installed-files.txt"),names.Where(n=>n!="installed-files.txt").Select(n=>n+"|"+Hash(SafeFile(stage,n))).ToArray());
   var state=new InstallTransaction { Id=Guid.NewGuid().ToString("N"),Version=3,Complete=true,OriginalRunningServices=new string[0],GraphRestoreRequired=owned.Contains("NoiseReduction.Apo.dll") && (!sandbox || services!=null),Files=names.Select(name=>new InstallFileState { Name=name,Existed=File.Exists(SafeFile(directory,name)) }).ToArray(),EngineCheckpoint=new string[0],SetupCheckpoint=new string[0] };
   if(!sandbox) using(var registry=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) { state.EngineCheckpoint=RegistryJournal(registry,"Journal").Checkpoint(); state.SetupCheckpoint=RegistryJournal(registry,"SetupJournal").Checkpoint(); }
   state.FileCount=state.Files.Length; state.EngineCount=state.EngineCheckpoint.Length; state.SetupCount=state.SetupCheckpoint.Length;
   PublishTransaction(transaction,state);
   RegistryEngine.CurrentTransactionId=state.Id;
   Action replace=delegate {
    try {
    if(!sandbox && state.GraphRestoreRequired) WaitForPayloadRelease(directory,names,10000);
    foreach(var name in names) { string destination=SafeFile(directory,name); if(File.Exists(destination)) File.Replace(SafeFile(stage,name),destination,SafeFile(directory,name+".nr-backup")); else File.Move(SafeFile(stage,name),destination); if(afterReplace!=null) afterReplace(name); }
   if(!sandbox) { Integration.RegisterEngine(directory);
   using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) {
    var e=new RegistryEngine(root,Path.Combine(DataRoot,"SetupJournal")); e.Recover();
    e.Set(UninstallKey,"DisplayName","NoiseReduction",RegistryValueKind.String); e.Set(UninstallKey,"DisplayVersion","1.0.0",RegistryValueKind.String);
    e.Set(UninstallKey,"Publisher","NoiseReduction contributors",RegistryValueKind.String); e.Set(UninstallKey,"InstallLocation",directory,RegistryValueKind.String);
    e.Set(UninstallKey,"UninstallString","\""+Path.Combine(directory,"NoiseReduction.Setup.exe")+"\" --uninstall",RegistryValueKind.String);
    e.Set(UninstallKey,"QuietUninstallString","\""+Path.Combine(directory,"NoiseReduction.Setup.exe")+"\" --uninstall --silent",RegistryValueKind.String);
    e.Set(UninstallKey,"DisplayIcon",Path.Combine(directory,"NoiseReduction.exe"),RegistryValueKind.String); e.Set(UninstallKey,"NoModify",1,RegistryValueKind.DWord); e.Set(UninstallKey,"NoRepair",1,RegistryValueKind.DWord);
   }
   MakeShortcut(Path.Combine(directory,"NoiseReduction.exe")); }
    } catch(Exception replacementError) { try { RestoreFiles(directory,transaction,state,sandbox); } catch(Exception rollbackError) { throw new AggregateException("Installation replacement and rollback failed",replacementError,rollbackError); } throw; }
   };
   if(state.GraphRestoreRequired) { var clock=Stopwatch.StartNew(); GraphMaintenance(transaction,state,replace,services ?? new WindowsAudioServices(),milliseconds ?? delegate { return clock.ElapsedMilliseconds; }); }
   else replace();
   state.Committed=true; PublishTransaction(transaction,state); committed=true; CleanupCommitted(directory,transaction,state);
  } catch {
   if(!committed) RecoverInstall(directory,transaction,sandbox,services,milliseconds);
   throw;
  } finally { RegistryEngine.CurrentTransactionId=previousTransaction; foreach(var name in names) File.Delete(SafeFile(stage,name)); if(Directory.Exists(stage) && Directory.GetFileSystemEntries(stage).Length==0) Directory.Delete(stage); }
 }
 static void Uninstall(string directory,bool sandbox) {
  RecoverInstall(directory,SafeFile(directory,"install-transaction.txt"),sandbox);
  var names=ManifestFiles(directory);
  if(!sandbox) {
  bool hadEffects=Integration.EnumerateMicrophones().Any(device=>device.Attached);
  Integration.DetachAll();
  if(hadEffects) Integration.RefreshAudioGraph();
  try { foreach(var name in names) { string file=SafeFile(directory,name); if(File.Exists(file)) using(var probe=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { } } } catch(IOException ex) { throw new IOException("Microphone effects were detached. Close NoiseReduction and other microphone applications, then retry uninstall",ex); }
  Integration.UnregisterEngine();
  using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) { var e=new RegistryEngine(root,Path.Combine(DataRoot,"SetupJournal")); e.Recover(); e.RestorePrefix(UninstallKey); using(var key=root.OpenSubKey(UninstallKey)) if(key != null && key.ValueCount==0 && key.SubKeyCount==0) { key.Dispose(); root.DeleteSubKey(UninstallKey,false); } }
  File.Delete(Shortcut);
  using(var startup=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)) if(startup!=null) { string value=startup.GetValue("NoiseReduction") as string; string expected="\""+Path.Combine(directory,"NoiseReduction.exe")+"\" --tray"; if(String.Equals(value,expected,StringComparison.OrdinalIgnoreCase)) startup.DeleteValue("NoiseReduction",false); }
  }
  foreach(var name in names) { string file=SafeFile(directory,name); if(File.Exists(file)) File.Delete(file); }
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
  try { Extract(directory); var names=PayloadNames(); if(!names.Contains("NoiseReduction.exe") || !names.Contains("NoiseReduction.Apo.dll") || !names.Contains("rnnoise.dll")) throw new Exception("Required payload missing");
   if(names.Any(n=>new[]{".mp3",".wav",".flac",".ogg",".json"}.Contains(Path.GetExtension(n).ToLowerInvariant()))) throw new Exception("Unexpected private/audio payload");
   foreach(var name in names) File.Delete(SafeFile(directory,name));
   Install(directory,true); ValidateManifest(directory);
   File.WriteAllText(SafeFile(directory,"user-recording.wav"),"keep me");
   Install(directory,true); ValidateManifest(directory);
   var expected=ManifestFiles(directory).ToDictionary(n=>n,n=>Hash(SafeFile(directory,n)));
   bool failed=false;
   using(var locked=new FileStream(SafeFile(directory,"rnnoise.dll"),FileMode.Open,FileAccess.Read,FileShare.Read)) { try { Install(directory,true); } catch(IOException) { failed=true; } }
   if(!failed || expected.Any(pair=>Hash(SafeFile(directory,pair.Key))!=pair.Value)) throw new Exception("Failed upgrade did not restore prior files");
   string interrupted=SafeFile(directory,"install-transaction.txt"); string app=SafeFile(directory,"NoiseReduction.exe"),backup=SafeFile(directory,"NoiseReduction.exe.nr-backup");
   File.Copy(app,backup); PublishTransaction(interrupted,new InstallTransaction{ Id=Guid.NewGuid().ToString("N"),Version=2,Complete=true,FileCount=2,EngineCount=0,SetupCount=0,EngineCheckpoint=new string[0],SetupCheckpoint=new string[0],Files=new[]{new InstallFileState{Name="NoiseReduction.exe",Existed=true},new InstallFileState{Name="partial-new.txt",Existed=false}}}); File.WriteAllText(app,"interrupted"); File.WriteAllText(SafeFile(directory,"partial-new.txt"),"partial");
   RecoverInstall(directory,interrupted,true); if(Hash(app)!=expected["NoiseReduction.exe"] || File.Exists(SafeFile(directory,"partial-new.txt"))) throw new Exception("Interrupted install recovery failed");
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
   var workerAcl=WorkerAccess(); var workerRules=workerAcl.GetAccessRules(true,true,typeof(SecurityIdentifier));
   if(!workerAcl.AreAccessRulesProtected || workerRules.Count!=2 || !workerAcl.GetOwner(typeof(SecurityIdentifier)).Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null))) throw new Exception("Uninstall worker directory is not protected");
   foreach(FileSystemAccessRule rule in workerRules) if(rule.AccessControlType!=AccessControlType.Allow || rule.FileSystemRights!=FileSystemRights.FullControl || (!rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)) && !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)))) throw new Exception("Uninstall worker allows unprivileged write access");
   if(!WorkerDirectory().StartsWith(Path.GetFullPath(DataRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Uninstall worker staged outside protected ProgramData root");
   bool rejected=false; try { SafeFile(directory,"..\\outside"); } catch(IOException) { rejected=true; } if(!rejected) throw new Exception("Path validation failed");
   Console.WriteLine("PASS package extraction, upgrade/failure/crash rollback, checkpoint validation, committed cleanup retry, stopped upgrade rollback/service recovery, ownership conflict, uninstall recovery/preservation, path validation: "+names.Length+" files"); return 0;
  } finally { foreach(var name in PayloadNames()) File.Delete(SafeFile(directory,name)); if(Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length==0) Directory.Delete(directory); }
 }
 [STAThread] static int Main(string[] args) {
  silent=args.Contains("--silent");
  try {
   if(args.Length>0 && args[0]=="--extract") { if(args.Length!=2) throw new ArgumentException("Usage: --extract <empty-directory>"); Extract(Path.GetFullPath(args[1])); return 0; }
   if(args.Contains("--self-test")) return SelfTest();
   if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) { var elevated=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,String.Join(" ",args.Select(a=>"\""+a+"\""))) { UseShellExecute=true,Verb="runas" }); elevated.WaitForExit(); return elevated.ExitCode; }
   RequireAdmin();
   if(args.Contains("--uninstall-worker")) { int index=Array.IndexOf(args,"--uninstall-worker"); int pid=Int32.Parse(args[index+1]); try { using(var parent=Process.GetProcessById(pid)) if(!parent.WaitForExit(30000)) throw new IOException("Original setup process did not exit"); } catch(ArgumentException) { } }
   using(var operation = new Integration.OperationLease()) {
   bool remove=args.Contains("--uninstall") || args.Contains("--uninstall-worker");
   if(!silent && !args.Contains("--uninstall-worker") && MessageBox.Show(remove?"Remove NoiseReduction and restore microphone effects?":"Install NoiseReduction microphone controls? Windows audio protections will remain enabled", "NoiseReduction",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK) return 1;
   if(args.Contains("--uninstall") && String.Equals(Path.GetFullPath(Assembly.GetExecutingAssembly().Location),Path.Combine(InstallRoot,"NoiseReduction.Setup.exe"),StringComparison.OrdinalIgnoreCase)) {
    string temp=StageUninstallWorker();
    var process=Process.Start(new ProcessStartInfo(temp,"--uninstall-worker "+Process.GetCurrentProcess().Id+" --silent") {UseShellExecute=false,CreateNoWindow=true}); if(process==null) throw new IOException("Cannot launch uninstall worker"); return 0;
   }
   if(args.Contains("--uninstall-worker")) Uninstall(InstallRoot,false);
   else if(remove) Uninstall(InstallRoot,false); else Install(InstallRoot,false);
   if(!silent) MessageBox.Show(remove?"NoiseReduction was removed":"NoiseReduction installed. Open settings and select a microphone to enable processing", "NoiseReduction",MessageBoxButtons.OK,MessageBoxIcon.Information);
   return 0; }
  } catch(Exception ex) { Console.Error.WriteLine(args.Contains("--self-test") ? ex.ToString() : ex.Message); try { if(!args.Contains("--self-test") && !args.Contains("--extract")) { Directory.CreateDirectory(DataRoot); File.AppendAllText(Path.Combine(DataRoot,"setup-errors.log"),DateTime.UtcNow.ToString("o")+" "+ex+Environment.NewLine); } } catch { } if(!silent) MessageBox.Show(ex.Message,"NoiseReduction setup",MessageBoxButtons.OK,MessageBoxIcon.Error); return 2; }
 }
}
