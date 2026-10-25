using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Serialization;
using System.Threading;
using System.ServiceProcess;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography.X509Certificates;

namespace NoiseReduction.Platform {
 public class DeviceInfo { public string Id, Name, Guid; public bool Attached, EnhancementsEnabled, Active, EffectPack; public int Channels = 1; }
 public static class Integration {
  public const string Clsid = "{752DE85C-174D-42C2-A92C-631A4F77D08B}";
  public const string CompositeKey = "{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D},15";
  internal const string LegacyKey = "{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D},7";
  internal const string ModesKey = "{D3993A3F-99C2-4402-B5EC-A92A0367664B},7";
  internal const string DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
  const string Capture = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
  static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NoiseReduction"); } }
  public static string ControlRoot { get { return Path.Combine(DataRoot, "Endpoints"); } }
  public sealed class OperationLease : IDisposable {
   readonly Mutex mutex;
   public OperationLease() { mutex=new Mutex(false,@"Global\NoiseReduction.Integration"); try { if(!mutex.WaitOne(10000)) { mutex.Dispose(); throw new InvalidOperationException("Сейчас идёт установка или другое подключение, повторите после него"); } } catch(AbandonedMutexException) { } }
   public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
  }
  static RegistryKey Machine() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }
  static RegistryEngine Engine(RegistryKey root) { PrepareDirectories(); return new RegistryEngine(root, Path.Combine(DataRoot, "Journal")); }
  internal static void PrepareDirectories() {
   var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
   var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
   var users=new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);
   foreach(string directory in new[]{DataRoot,ControlRoot,Path.Combine(DataRoot,"Journal"),Path.Combine(DataRoot,"SetupJournal")}) {
    for(var current=new DirectoryInfo(directory);current != null;current=current.Parent) if(current.Exists && (current.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("NoiseReduction data paths must not contain reparse points");
    if(directory.EndsWith("Journal",StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) foreach(string file in Directory.GetFiles(directory)) {
     if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("Journal must not contain reparse points");
     var owner=new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier)); if(!owner.Equals(admins) && !owner.Equals(system)) throw new UnauthorizedAccessException("Rollback journal has an untrusted owner: " + file);
    }
   }
   foreach(string directory in new[]{DataRoot,Path.Combine(DataRoot,"Journal"),Path.Combine(DataRoot,"SetupJournal"),ControlRoot}) {
    Directory.CreateDirectory(directory); var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false); acl.SetOwner(admins);
    foreach(var principal in new[]{admins,system}) acl.AddAccessRule(new FileSystemAccessRule(principal,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
    acl.AddAccessRule(new FileSystemAccessRule(users,directory==ControlRoot ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow)); new DirectoryInfo(directory).SetAccessControl(acl);
   }
  }
  internal static string EndpointGuid(string id) { int start = id.LastIndexOf('{'); Guid guid; if(start < 0 || !Guid.TryParse(id.Substring(start), out guid)) throw new ArgumentException("Invalid capture endpoint ID"); return guid.ToString("B"); }
  static string Fx(string id) { return Capture + "\\" + EndpointGuid(id) + "\\FxProperties"; }
  static string Properties(string id) { return Capture + "\\" + EndpointGuid(id) + "\\Properties"; }
  public static string GetDefaultMicrophoneId() {
   var enumerator=(IMMDeviceEnumerator)new MMDeviceEnumerator(); IMMDevice device=null;
   try {
    int result=enumerator.GetDefaultAudioEndpoint(1,0,out device);
    if(result==unchecked((int)0x80070490)) return null;
    Marshal.ThrowExceptionForHR(result); string id; Marshal.ThrowExceptionForHR(device.GetId(out id)); return id;
   } finally { if(device!=null) Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator); }
  }
  public static List<DeviceInfo> EnumerateMicrophones() {
   var result = new List<DeviceInfo>(); var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator(); IMMDeviceCollection collection = null;
   try {
    Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(1, 15, out collection)); uint count; collection.GetCount(out count);
    using(var root = Machine()) for(uint i = 0; i < count; i++) {
     IMMDevice device; if(collection.Item(i, out device) != 0 || device == null) continue;
     // у удалённого или сломанного устройства свойства могут не читаться, остальные микрофоны важнее
     try {
      uint state; Marshal.ThrowExceptionForHR(device.GetState(out state)); string id; Marshal.ThrowExceptionForHR(device.GetId(out id)); string guid = EndpointGuid(id); string name = id;
      IPropertyStore store; Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out store));
      try { var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),14); PropVariant value; Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value)); try { if(value.Type == 31) name = Marshal.PtrToStringUni(value.Pointer); } finally { PropVariantClear(ref value); } } finally { Marshal.ReleaseComObject(store); }
      bool attached = false, enabled = true;
      using(var fx = root.OpenSubKey(Fx(id))) if(fx != null) { attached = RegistryEngine.Contains(fx.GetValue(CompositeKey), Clsid) || RegistryEngine.Contains(fx.GetValue(LegacyKey), Clsid); object disabled = fx.GetValue("{1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E},5"); enabled = disabled == null || Convert.ToInt32(disabled) == 0; }
      // число каналов из формата устройства, WAVEFORMATEX начинается после 8 байт заголовка
      int channels = 1;
      using(var properties = root.OpenSubKey(Capture + "\\" + EndpointGuid(id) + "\\Properties")) if(properties != null) { var format = properties.GetValue("{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0") as byte[]; if(format != null && format.Length >= 12) channels = Math.Max(1, (int)BitConverter.ToUInt16(format, 10)); }
      // эффектами такого микрофона управляет пакет Windows, наш пакет выбирается в его свойствах
      bool pack = false;
      using(var fx = root.OpenSubKey(Fx(id))) pack = fx != null && EffectPack.Managed(fx);
      if(pack) using(var properties = root.OpenSubKey(Properties(id))) attached = properties != null && EffectPack.Selected(properties);
      result.Add(new DeviceInfo { Id = id, Guid = guid.Trim('{','}').ToLowerInvariant(), Name = name, Attached = attached, EnhancementsEnabled = enabled, Active = state == 1, Channels = channels, EffectPack = pack });
     } catch(COMException) { } catch(IOException) { } catch(UnauthorizedAccessException) {
     } finally { Marshal.ReleaseComObject(device); }
    }
   } finally { if(collection != null) Marshal.ReleaseComObject(collection); Marshal.ReleaseComObject(enumerator); }
   return result;
  }
  public sealed class VerifiedAttachmentScope : IDisposable {
   OperationLease operation; RegistryKey root; RegistryEngine.VerificationScope scope;
   internal VerifiedAttachmentScope(string endpointId, bool effectPack) {
    operation=new OperationLease();
    try { root=Machine(); scope=Engine(root).BeginVerifiedAttachment(effectPack ? Properties(endpointId) : Fx(endpointId)); }
    catch { if(root!=null) root.Dispose(); operation.Dispose(); throw; }
   }
   public void Commit() { if(scope==null) throw new ObjectDisposedException("VerifiedAttachmentScope"); scope.Commit(); }
   public void Dispose() { if(scope==null) return; try { scope.Dispose(); } finally { scope=null; root.Dispose(); operation.Dispose(); } }
  }
  public static void RefreshAudioGraph() {
   using(var operation=new OperationLease()) using(var root=Machine()) { Engine(root).Recover(); var clock=Stopwatch.StartNew(); AudioGraphRefresh.Run(new WindowsAudioServices(),delegate { return clock.ElapsedMilliseconds; }); }
  }
  public static VerifiedAttachmentScope BeginVerifiedAttachment(string endpointId) { return new VerifiedAttachmentScope(endpointId, false); }
  public static VerifiedAttachmentScope BeginVerifiedAttachment(string endpointId, bool effectPack) { return new VerifiedAttachmentScope(endpointId, effectPack); }
  public static void Attach(string endpointId) { using(var operation = new OperationLease()) using(var root = Machine()) { var e = Engine(root); e.Recover(); if(!EnumerateMicrophones().Any(d => d.Id == endpointId && d.Active)) throw new InvalidOperationException("Capture endpoint is unavailable"); e.Attach(Fx(endpointId)); } }
  public static void Detach(string endpointId) { using(var operation = new OperationLease()) using(var root = Machine()) { var e = Engine(root); e.Recover(); e.Detach(Fx(endpointId)); e.Detach(Properties(endpointId)); } }
  public static void SelectEffectPack(string endpointId) { using(var operation = new OperationLease()) using(var root = Machine()) { var e = Engine(root); e.Recover(); if(!EnumerateMicrophones().Any(d => d.Id == endpointId && d.Active && d.EffectPack)) throw new InvalidOperationException("Capture endpoint is unavailable"); string component = EffectPack.Component(root); if(component == null) throw new InvalidOperationException("Noise Reduction effect pack is not installed"); e.Set(Fx(endpointId), EffectPack.ComponentKey, component, RegistryValueKind.String); e.Set(Properties(endpointId), EffectPack.SelectedKey, EffectPack.Id, RegistryValueKind.String); e.Set(Properties(endpointId), EffectPack.StateKey, EffectPack.State(), RegistryValueKind.Binary); } }
  public static void DetachAll() { using(var operation = new OperationLease()) using(var root = Machine()) { var e = Engine(root); e.Recover(); using(var capture = root.OpenSubKey(Capture)) if(capture != null) foreach(var id in capture.GetSubKeyNames()) { e.Detach(Capture + "\\" + id + "\\FxProperties"); e.Detach(Capture + "\\" + id + "\\Properties"); } e.DetachJournaledEndpoints(); } }
  public static void RegisterEngine(string installationDirectory) {
   string dll = Path.Combine(Path.GetFullPath(installationDirectory), "NoiseReduction.Apo.dll"); if(!File.Exists(dll)) throw new FileNotFoundException("APO library is missing", dll);
   PrepareDirectories();
   using(var operation = new OperationLease()) using(var root = Machine()) { var e = Engine(root); e.Recover(); string com = @"SOFTWARE\Classes\CLSID\" + Clsid + @"\InprocServer32"; e.Set(com,"",dll,RegistryValueKind.String); e.Set(com,"ThreadingModel","Both",RegistryValueKind.String);
    string apo = @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + Clsid;
    e.Set(apo,"FriendlyName","Noise Reduction",RegistryValueKind.String); e.Set(apo,"Copyright","",RegistryValueKind.String);
    foreach(string field in new[]{"MinorVersion","MinInputConnections","MaxInputConnections","MinOutputConnections","MaxOutputConnections","NumAPOInterfaces"}) e.Set(apo,field,1,RegistryValueKind.DWord);
    e.Set(apo,"MajorVersion",0,RegistryValueKind.DWord); e.Set(apo,"Flags",15,RegistryValueKind.DWord); e.Set(apo,"MaxInstances",-1,RegistryValueKind.DWord); e.Set(apo,"APOInterface0","{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}",RegistryValueKind.String);
   }
  }
  public static void UnregisterEngine() { using(var operation = new OperationLease()) using(var root = Machine()) { var e = Engine(root); e.Recover(); using(var capture = root.OpenSubKey(Capture)) if(capture != null) foreach(var id in capture.GetSubKeyNames()) using(var fx = capture.OpenSubKey(id + "\\FxProperties")) if(fx != null && (RegistryEngine.Contains(fx.GetValue(CompositeKey),Clsid) || RegistryEngine.Contains(fx.GetValue(LegacyKey),Clsid))) throw new InvalidOperationException("Detach all microphones before unregistering the engine"); e.RestoreRegistration(); } }
  [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant variant);
 }
 // Пакет эффектов Windows 11: так Windows ставит эффекты на микрофоны, которыми управляет сама (USB и т.п.)
 public static class EffectPack {
  public const string Id = "{3A9F5C0E-7B21-4D8E-9F43-6C2B1D8E0A57}";
  internal const string SelectedKey = "{c876062a-a276-4ed6-b15b-3962e69007f8},2";
  internal const string StateKey = "{c876062a-a276-4ed6-b15b-3962e69007f8},3";
  // связь класса эффекта с устройством-компонентом, где Windows ищет его регистрацию
  internal const string ComponentKey = "{bdd6a3f4-ed4e-4d4d-aa82-d695a550e59f},100";
  internal static string Component(RegistryKey root) {
   using(var drivers = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\SWD\DRIVERENUM")) {
    if(drivers == null) return null;
    var name = drivers.GetSubKeyNames().FirstOrDefault(n => n.StartsWith("{6f1d2c4b-8a9e-4b37-9c51-2e7d4a0f8b16}#NoiseReductionEffectPack", StringComparison.OrdinalIgnoreCase));
    return name == null ? null : @"SWD\DRIVERENUM\" + name;
   }
  }
  const string Subject = "CN=Noise Reduction Effect Pack";
  static readonly string[] Infs = { "noisereductionapo.inf", "noisereductioneffectpack.inf" };
  internal static bool Managed(RegistryKey fx) { return fx.GetValueNames().Any(name => name.EndsWith(",100",StringComparison.Ordinal) && (fx.GetValue(name) as string ?? "").StartsWith(@"SWD\DRIVERENUM\",StringComparison.OrdinalIgnoreCase)); }
  internal static bool Selected(RegistryKey properties) { return String.Equals(properties.GetValue(SelectedKey) as string, Id, StringComparison.OrdinalIgnoreCase); }
  // то же значение пишут Параметры Windows при выборе пакета в "Улучшениях звука"
  internal static byte[] State() {
   var data = new List<byte>(); data.AddRange(BitConverter.GetBytes(0x41)); data.AddRange(BitConverter.GetBytes(1)); data.AddRange(new Guid(Id).ToByteArray());
   for(int i = 0; i < 3; i++) data.AddRange(BitConverter.GetBytes(1));
   return data.ToArray();
  }
  public static void Install(string directory) {
   string pack = Path.Combine(directory, "Pack");
   if(!File.Exists(Path.Combine(pack, "NoiseReductionPack.cer"))) return;
   var certificate = new X509Certificate2(Path.Combine(pack, "NoiseReductionPack.cer"));
   foreach(var name in new[] { StoreName.Root, StoreName.TrustedPublisher }) {
    var store = new X509Store(name, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadWrite);
    try { foreach(var old in store.Certificates.Cast<X509Certificate2>().Where(c => c.Subject == Subject && c.Thumbprint != certificate.Thumbprint).ToArray()) store.Remove(old); store.Add(certificate); }
    finally { store.Close(); }
   }
   // переустановка пакета сбрасывает выбор в микрофонах, после неё выбираем пакет заново
   var selected = Integration.EnumerateMicrophones().Where(device => device.EffectPack && device.Attached).Select(device => device.Id).ToList();
   PnpUtil("/add-driver \"" + Path.Combine(pack, "*.inf") + "\" /install");
   if(selected.Count == 0) return;
   foreach(var id in selected) Integration.SelectEffectPack(id);
   Integration.RefreshAudioGraph();
  }
  public static void Uninstall() {
   foreach(var published in Published()) PnpUtil("/delete-driver " + published + " /uninstall");
   foreach(var name in new[] { StoreName.Root, StoreName.TrustedPublisher }) {
    var store = new X509Store(name, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadWrite);
    try { foreach(var old in store.Certificates.Cast<X509Certificate2>().Where(c => c.Subject == Subject).ToArray()) store.Remove(old); }
    finally { store.Close(); }
   }
  }
  static List<string> Published() {
   var result = new List<string>(); string published = null;
   foreach(var line in PnpUtil("/enum-drivers").Split('\n')) {
    var parts = line.Split(new[] { ':' }, 2); if(parts.Length != 2) continue;
    string key = parts[0].Trim(), value = parts[1].Trim();
    if(key == "Published Name") published = value;
    else if(key == "Original Name" && published != null && Infs.Contains(value.ToLowerInvariant())) result.Add(published);
   }
   return result;
  }
  static string PnpUtil(string arguments) {
   var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
   using(var process = Process.Start(start)) {
    string output = process.StandardOutput.ReadToEnd(); process.WaitForExit();
    // 3010 значит, что части устройств нужна перезагрузка, пакет при этом установлен
    if(process.ExitCode != 0 && process.ExitCode != 3010 && !arguments.StartsWith("/delete-driver")) throw new InvalidOperationException("pnputil " + arguments + " failed: " + output.Trim());
    return output;
   }
  }
 }
 public class ValueSnapshot {
  public bool Exists; public RegistryValueKind Kind; public string[] Strings; public string Text; public byte[] Bytes; public long Number;
  public static ValueSnapshot Read(RegistryKey key,string name) { object value = key == null ? null : key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames); var s = new ValueSnapshot { Exists = value != null }; if(!s.Exists) return s; s.Kind = key.GetValueKind(name); if(value is string[]) s.Strings=(string[])value; else if(value is string) s.Text=(string)value; else if(value is byte[]) s.Bytes=(byte[])value; else s.Number=Convert.ToInt64(value); return s; }
  public object Value() { if(Kind == RegistryValueKind.MultiString) return Strings; if(Kind == RegistryValueKind.String || Kind == RegistryValueKind.ExpandString) return Text; if(Kind == RegistryValueKind.Binary || Kind == RegistryValueKind.None) return Bytes; if(Kind == RegistryValueKind.DWord) return unchecked((int)Number); return Number; }
  public bool Same(ValueSnapshot other) { return new XmlSerializer(typeof(ValueSnapshot)).SerializeString(this) == new XmlSerializer(typeof(ValueSnapshot)).SerializeString(other); }
 }
 static class XmlHelper { public static string SerializeString(this XmlSerializer serializer, object value) { using(var writer = new StringWriter()) { serializer.Serialize(writer,value); return writer.ToString(); } } }
 public class PendingAttachment { public int Version; public string Id,Path; }
 public class RegistryChange { public string Path, Name; public ValueSnapshot Before, After; public bool Committed; public long Sequence; public string TransactionId; }
 public class RegistryEngine {
  [ThreadStatic] internal static string CurrentTransactionId = null;
  readonly RegistryKey root; readonly string journal; public Action BeforeWrite;
  public RegistryEngine(RegistryKey root,string journal) { this.root=root; this.journal=journal; }
  static XmlSerializer Serializer = new XmlSerializer(typeof(RegistryChange));
  string Save(RegistryChange change, bool temporary = false) {
   Directory.CreateDirectory(journal);
   string temporaryFile=System.IO.Path.Combine(journal,System.Guid.NewGuid().ToString("N")+".tmp");
   using(var stream=new FileStream(temporaryFile,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { Serializer.Serialize(stream,change); stream.Flush(true); }
   if(temporary) return temporaryFile;
   string published=System.IO.Path.ChangeExtension(temporaryFile,".xml"); File.Move(temporaryFile,published); return published;
  }
  RegistryChange Load(string file) { using(var stream=File.OpenRead(file)) return (RegistryChange)Serializer.Deserialize(stream); }
  void Write(RegistryChange c, ValueSnapshot value) {
   RegistryKey key=root.OpenSubKey(c.Path,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryRights.QueryValues|RegistryRights.SetValue);
   if(key==null) {
    bool machine=String.Equals(root.Name,"HKEY_LOCAL_MACHINE",StringComparison.OrdinalIgnoreCase);
    bool registration=String.Equals(c.Path,@"SOFTWARE\Classes\CLSID\"+Integration.Clsid+@"\InprocServer32",StringComparison.OrdinalIgnoreCase) || String.Equals(c.Path,@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\"+Integration.Clsid,StringComparison.OrdinalIgnoreCase) || String.Equals(c.Path,@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NoiseReduction",StringComparison.OrdinalIgnoreCase);
    if(machine && !registration) throw new InvalidOperationException("Existing endpoint registry state is unavailable: "+c.Path);
    if(!value.Exists) return;
    key=root.CreateSubKey(c.Path);
   }
   using(key) { if(value.Exists) key.SetValue(c.Name,value.Value(),value.Kind); else key.DeleteValue(c.Name,false); key.Flush(); }
  }
  public void Set(string path,string name,object value,RegistryValueKind kind) {
   ValueSnapshot before; using(var key=root.OpenSubKey(path)) before=ValueSnapshot.Read(key,name);
   var after = new ValueSnapshot{Exists=true,Kind=kind}; if(value is string[]) after.Strings=(string[])value; else if(value is string) after.Text=(string)value; else if(value is byte[]) after.Bytes=(byte[])value; else after.Number=Convert.ToInt64(value);
   if(before.Same(after)) return;
   var c=new RegistryChange{Path=path,Name=name,Before=before,After=after,TransactionId=CurrentTransactionId,Sequence=Math.Max(DateTime.UtcNow.Ticks,Files().Select(f=>Load(f).Sequence).DefaultIfEmpty(0).Max()+1)}; string file=Save(c);
   if(BeforeWrite != null) BeforeWrite();
   Write(c,after); c.Committed=true; string committed=Save(c,true); File.Replace(committed,file,null);
  }
  static readonly XmlSerializer PendingSerializer=new XmlSerializer(typeof(PendingAttachment));
  public sealed class VerificationScope : IDisposable {
   readonly RegistryEngine engine; readonly string previous; readonly PendingAttachment pending; bool completed,disposed;
   internal VerificationScope(RegistryEngine engine,string path) {
    this.engine=engine; previous=CurrentTransactionId;
    using(var key=engine.root.OpenSubKey(path)) { if(key==null) throw new InvalidOperationException("Endpoint registry state is unavailable"); if(Contains(key.GetValue(Integration.CompositeKey),Integration.Clsid) || Contains(key.GetValue(Integration.LegacyKey),Integration.Clsid) || EffectPack.Selected(key)) return; }
    if(!String.IsNullOrEmpty(previous)) throw new InvalidOperationException("A registry verification transaction is already active");
    pending=new PendingAttachment{Version=1,Id=Guid.NewGuid().ToString("N"),Path=path}; engine.PublishPending(pending); CurrentTransactionId=pending.Id;
   }
   public void Commit() { if(disposed) throw new ObjectDisposedException("VerificationScope"); if(pending!=null) File.Delete(engine.PendingFile(pending.Id)); completed=true; }
   public void Dispose() { if(disposed) return; try { if(!completed && pending!=null) engine.RollbackPending(pending); } finally { CurrentTransactionId=previous; disposed=true; } }
  }
  public VerificationScope BeginVerifiedAttachment(string path) { Recover(); return new VerificationScope(this,path); }
  string PendingFile(string id) { return System.IO.Path.Combine(journal,id+".verify"); }
  void PublishPending(PendingAttachment pending) {
   Directory.CreateDirectory(journal); string temporary=System.IO.Path.Combine(journal,Guid.NewGuid().ToString("N")+".tmp");
   using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { PendingSerializer.Serialize(stream,pending); stream.Flush(true); }
   File.Move(temporary,PendingFile(pending.Id));
  }
  void RecoverVerifications() {
   foreach(var file in Directory.Exists(journal) ? Directory.GetFiles(journal,"*.verify") : new string[0]) {
    PendingAttachment pending; using(var stream=File.OpenRead(file)) pending=(PendingAttachment)PendingSerializer.Deserialize(stream);
    Guid id; if(pending.Version!=1 || !Guid.TryParseExact(pending.Id,"N",out id) || String.IsNullOrEmpty(pending.Path) || !String.Equals(file,PendingFile(pending.Id),StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid pending verification record");
    if(!String.Equals(pending.Id,CurrentTransactionId,StringComparison.Ordinal)) RollbackPending(pending);
   }
  }
  static string Parent(string path) { int index=path.LastIndexOf('\\'); return index < 0 ? path : path.Substring(0,index); }
  void RollbackPending(PendingAttachment pending) {
   bool complete=true;
   foreach(var file in ReverseFiles()) {
    var c=Load(file); if(!String.Equals(c.TransactionId,pending.Id,StringComparison.Ordinal)) continue;
    // выбор пакета эффектов меняет и свойства, и эффекты того же микрофона
    if(!String.Equals(Parent(c.Path),Parent(pending.Path),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Verification journal contains an unrelated registry path");
    using(var key=root.OpenSubKey(c.Path)) {
     if(key==null) { complete=false; continue; }
     var now=ValueSnapshot.Read(key,c.Name);
     if(now.Same(c.After)) Write(c,c.Before);
     else if(c.Name==Integration.CompositeKey && now.Exists && now.Kind==RegistryValueKind.MultiString) Write(c,new ValueSnapshot{Exists=true,Kind=RegistryValueKind.MultiString,Strings=((string[])now.Value()).Where(value=>!String.Equals(value,Integration.Clsid,StringComparison.OrdinalIgnoreCase)).ToArray()});
     else if(now.Same(c.Before)) { }
     else if(c.Name==Integration.ModesKey) { }
     else throw new InvalidOperationException("Pending verification rollback conflict at "+c.Path);
    }
    File.Delete(file);
   }
   if(complete) File.Delete(PendingFile(pending.Id));
  }
  public static bool Contains(object value,string clsid) { var list=value as string[]; if(list != null) return list.Any(s=>String.Equals(s,clsid,StringComparison.OrdinalIgnoreCase)); return String.Equals(value as string,clsid,StringComparison.OrdinalIgnoreCase); }
  public void Attach(string path) {
   using(var key=root.OpenSubKey(path)) { if(key == null) throw new InvalidOperationException("Endpoint registry state is unavailable"); object existing=key.GetValue(Integration.CompositeKey); if(Contains(existing,Integration.Clsid)) { if(key.GetValue(Integration.ModesKey) == null) Set(path,Integration.ModesKey,new[]{Integration.DefaultMode},RegistryValueKind.MultiString); return; } if(existing != null && !(existing is string[])) throw new InvalidOperationException("Composite EFX has an incompatible registry type"); var chain=existing == null ? new List<string>() : new List<string>((string[])existing);
    if(existing == null) { object legacy=key.GetValue(Integration.LegacyKey); if(legacy != null) throw new InvalidOperationException("Endpoint has a legacy OEM EFX chain; automatic migration is unsafe and was refused"); }
    if(chain.Any(s=> { Guid g; return !Guid.TryParse(s,out g); })) throw new InvalidOperationException("Invalid OEM EFX CLSID");
    chain.Add(Integration.Clsid);
    Set(path,Integration.CompositeKey,chain.ToArray(),RegistryValueKind.MultiString);
    try { if(key.GetValue(Integration.ModesKey) == null) Set(path,Integration.ModesKey,new[]{Integration.DefaultMode},RegistryValueKind.MultiString); } catch { Detach(path); throw; }
   }
  }
  string[] Files() { return Directory.Exists(journal) ? Directory.GetFiles(journal,"*.xml") : new string[0]; }
  string[] ReverseFiles() { return Files().OrderByDescending(f=> { var c=Load(f); return c.Sequence==0 ? File.GetCreationTimeUtc(f).Ticks : c.Sequence; }).ToArray(); }
  public void Recover() { RecoverVerifications(); foreach(var file in ReverseFiles()) { var c=Load(file); if(c.Committed || (!String.IsNullOrEmpty(CurrentTransactionId) && String.Equals(c.TransactionId,CurrentTransactionId,StringComparison.Ordinal))) continue; ValueSnapshot now; using(var key=root.OpenSubKey(c.Path)) now=ValueSnapshot.Read(key,c.Name); if(now.Same(c.After)) Write(c,c.Before); else if(!now.Same(c.Before)) throw new InvalidOperationException("Registry recovery conflict at " + c.Path + " / " + c.Name); File.Delete(file); } foreach(var temporaryFile in Directory.Exists(journal) ? Directory.GetFiles(journal,"*.tmp") : new string[0]) File.Delete(temporaryFile); }
  public void Detach(string path) {
   foreach(var file in ReverseFiles()) { var c=Load(file); if(!c.Committed || !String.Equals(c.Path,path,StringComparison.OrdinalIgnoreCase)) continue;
    using(var key=root.OpenSubKey(path)) { if(key == null) continue; var now=ValueSnapshot.Read(key,c.Name); if(now.Same(c.After)) Write(c,c.Before);
     else if(c.Name == Integration.CompositeKey && now.Kind == RegistryValueKind.MultiString && now.Exists) { var filtered=((string[])now.Value()).Where(s=>!String.Equals(s,Integration.Clsid,StringComparison.OrdinalIgnoreCase)).ToArray(); Write(c,new ValueSnapshot{Exists=true,Kind=RegistryValueKind.MultiString,Strings=filtered}); }
     else if(now.Same(c.Before)) { } else if(c.Name == Integration.ModesKey) { } else throw new InvalidOperationException("Registry rollback conflict at " + path);
    } File.Delete(file);
   }
  }
  public void DetachJournaledEndpoints() { foreach(var path in Files().Select(f=>Load(f).Path).Where(p=>p.EndsWith("\\FxProperties",StringComparison.OrdinalIgnoreCase) || p.EndsWith("\\Properties",StringComparison.OrdinalIgnoreCase)).Distinct().ToArray()) Detach(path); }
  public void RestoreRegistration() { RestorePrefix(@"SOFTWARE\Classes\"); }
  public string[] Checkpoint() { return Files(); }
  public void RestoreSince(string[] checkpoint) { Recover(); foreach(var file in ReverseFiles().Except(checkpoint).ToArray()) { var c=Load(file); ValueSnapshot now; using(var key=root.OpenSubKey(c.Path)) now=ValueSnapshot.Read(key,c.Name); if(now.Same(c.After)) Write(c,c.Before); else if(!now.Same(c.Before)) throw new InvalidOperationException("Registry transaction conflict at " + c.Path); File.Delete(file); } }
  public void RestoreTransaction(string transactionId) {
   if(String.IsNullOrEmpty(transactionId)) throw new ArgumentException("Missing registry transaction identity");
   foreach(var file in ReverseFiles()) { var c=Load(file); if(!String.Equals(c.TransactionId,transactionId,StringComparison.Ordinal)) continue; ValueSnapshot now; using(var key=root.OpenSubKey(c.Path)) now=ValueSnapshot.Read(key,c.Name); if(now.Same(c.After)) Write(c,c.Before); else if(!now.Same(c.Before)) throw new InvalidOperationException("Registry transaction conflict at " + c.Path); File.Delete(file); }
  }
  public void RestorePrefix(string prefix) { foreach(var file in ReverseFiles()) { var c=Load(file); if(!c.Path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) continue; ValueSnapshot now; using(var key=root.OpenSubKey(c.Path)) now=ValueSnapshot.Read(key,c.Name); if(now.Same(c.After)) Write(c,c.Before); else if(!now.Same(c.Before)) throw new InvalidOperationException("Registration has been changed by another installation"); File.Delete(file); } }
 }
 internal interface IAudioServices {
  string[] Dependents(string name);
  ServiceControllerStatus Status(string name);
  void Stop(string name);
  void Start(string name);
  void Wait(string name,ServiceControllerStatus desired,TimeSpan timeout);
 }
 internal sealed class WindowsAudioServices : IAudioServices {
  [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint Type,State,Controls,Win32Exit,SpecificExit,CheckPoint,WaitHint; }
  sealed class NativeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid {
   public NativeServiceHandle() : base(true) { }
   protected override bool ReleaseHandle() { return CloseServiceHandle(handle); }
  }
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern NativeServiceHandle OpenSCManager(string machine,string database,uint access);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern NativeServiceHandle OpenService(NativeServiceHandle manager,string name,uint access);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool ControlService(NativeServiceHandle service,uint control,out ServiceStatus status);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool CloseServiceHandle(IntPtr handle);
  public string[] Dependents(string name) {
   using(var service=new ServiceController(name)) { var dependents=service.DependentServices; try { return dependents.Select(dependent=>dependent.ServiceName).ToArray(); } finally { foreach(var dependent in dependents) dependent.Dispose(); } }
  }
  public ServiceControllerStatus Status(string name) { using(var service=new ServiceController(name)) { service.Refresh(); return service.Status; } }
  public void Stop(string name) {
   using(var manager=OpenSCManager(null,null,1)) {
    if(manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot open service manager");
    using(var service=OpenService(manager,name,0x20)) {
     if(service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot open audio service for stop: "+name);
     ServiceStatus status; if(!ControlService(service,1,out status)) { int error=Marshal.GetLastWin32Error(); if(error!=1062) throw new Win32Exception(error,"Cannot stop "+name); }
    }
   }
  }
  public void Start(string name) { using(var service=new ServiceController(name)) service.Start(); }
  public void Wait(string name,ServiceControllerStatus desired,TimeSpan timeout) { using(var service=new ServiceController(name)) service.WaitForStatus(desired,timeout); }
 }
 internal static class AudioGraphRefresh {
  const long PhaseTimeoutMilliseconds=30000;
  static TimeSpan Remaining(Func<long> milliseconds,long deadline,string name) { long remaining=deadline-milliseconds(); if(remaining<=0) throw new System.TimeoutException("Audio service refresh timed out at "+name); return TimeSpan.FromMilliseconds(remaining); }
  static void ThrowFailures(List<Exception> failures) { if(failures.Count>0) throw new InvalidOperationException("Windows Audio graph maintenance failed: "+String.Join("; ",failures.Select(error=>error.Message)),new AggregateException(failures)); }
  internal static void Run(IAudioServices services,Func<long> milliseconds) { Run(services,milliseconds,delegate { },null,null); }
  internal static void Run(IAudioServices services,Func<long> milliseconds,Action whileStopped,Action<string[]> beforeStop,string[] savedRunning) {
   if(savedRunning==null && services.Status("Audiosrv")!=ServiceControllerStatus.Running) throw new InvalidOperationException("Windows Audio must be running before refreshing the audio graph");
   var all=new List<string>(); var running=new List<string>(); var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long snapshotDeadline=milliseconds()+PhaseTimeoutMilliseconds;
   Action<string> snapshot=null;
   snapshot=delegate(string name) {
    if(!visited.Add(name)) return;
    var status=services.Status(name);
    if(savedRunning!=null && (status==ServiceControllerStatus.StartPending || status==ServiceControllerStatus.StopPending)) { services.Wait(name,status==ServiceControllerStatus.StartPending ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped,Remaining(milliseconds,snapshotDeadline,name)); status=services.Status(name); }
    if(status!=ServiceControllerStatus.Running && status!=ServiceControllerStatus.Stopped) throw new InvalidOperationException("Audio service state is changing: "+name+" ("+status+")");
    foreach(var dependent in services.Dependents(name)) snapshot(dependent);
    all.Add(name); if(status==ServiceControllerStatus.Running) running.Add(name);
   };
   snapshot("Audiosrv");
   var required=new HashSet<string>(running,StringComparer.OrdinalIgnoreCase); if(savedRunning!=null) required.UnionWith(savedRunning);
   var restoreOrder=(savedRunning ?? new string[0]).Where(name=>!all.Contains(name,StringComparer.OrdinalIgnoreCase)).Concat(all.Where(required.Contains)).ToArray();
   if(beforeStop!=null) beforeStop(restoreOrder);
   var failures=new List<Exception>(); long stopDeadline=milliseconds()+PhaseTimeoutMilliseconds;
   try {
    foreach(var name in running) {
     var status=services.Status(name);
     if(status==ServiceControllerStatus.Stopped) continue;
     if(status==ServiceControllerStatus.StartPending) { services.Wait(name,ServiceControllerStatus.Running,Remaining(milliseconds,stopDeadline,name)); status=services.Status(name); }
     if(status==ServiceControllerStatus.Running) { Remaining(milliseconds,stopDeadline,name); services.Stop(name); }
     else if(status!=ServiceControllerStatus.StopPending) throw new InvalidOperationException("Cannot stop audio service "+name+" ("+status+")");
     services.Wait(name,ServiceControllerStatus.Stopped,Remaining(milliseconds,stopDeadline,name));
    }
    whileStopped();
   } catch(Exception error) { failures.Add(new InvalidOperationException("Could not complete maintenance while Windows Audio was stopped: "+error.Message,error)); }
   finally { failures.AddRange(RestoreServices(services,milliseconds,restoreOrder)); }
   ThrowFailures(failures);
  }
  internal static void Restore(IAudioServices services,Func<long> milliseconds,string[] savedRunning) { ThrowFailures(RestoreServices(services,milliseconds,savedRunning)); }
  static List<Exception> RestoreServices(IAudioServices services,Func<long> milliseconds,IEnumerable<string> running) {
   var failures=new List<Exception>(); long restoreDeadline=milliseconds()+PhaseTimeoutMilliseconds;
   foreach(var name in running.Reverse()) {
    try {
     var status=services.Status(name);
     if(status==ServiceControllerStatus.Running) continue;
     if(status==ServiceControllerStatus.StopPending) { services.Wait(name,ServiceControllerStatus.Stopped,Remaining(milliseconds,restoreDeadline,name)); status=services.Status(name); }
     if(status==ServiceControllerStatus.Stopped) services.Start(name);
     else if(status!=ServiceControllerStatus.StartPending) throw new InvalidOperationException("Cannot restore audio service "+name+" ("+status+")");
     services.Wait(name,ServiceControllerStatus.Running,Remaining(milliseconds,restoreDeadline,name));
    } catch(Exception error) { failures.Add(new InvalidOperationException("Could not restore originally running audio service "+name+": "+error.Message,error)); }
   }
   return failures;
  }
 }
 [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; public PropertyKey(Guid format,uint id){Format=format;Id=id;} }
 [StructLayout(LayoutKind.Explicit,Size=24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
 [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
 [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceEnumerator { [PreserveSig]int EnumAudioEndpoints(int flow,uint mask,out IMMDeviceCollection devices); [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device); [PreserveSig]int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IMMDevice device); }
 [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceCollection { [PreserveSig]int GetCount(out uint count); [PreserveSig]int Item(uint index,out IMMDevice device); }
 [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDevice { [PreserveSig]int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)]out object instance); [PreserveSig]int OpenPropertyStore(uint mode,out IPropertyStore store); [PreserveSig]int GetId([MarshalAs(UnmanagedType.LPWStr)]out string id); [PreserveSig]int GetState(out uint state); }
 [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPropertyStore { int GetCount(out uint count); int GetAt(uint index,out PropertyKey key); [PreserveSig]int GetValue(ref PropertyKey key,out PropVariant value); int SetValue(ref PropertyKey key,ref PropVariant value); int Commit(); }
}
