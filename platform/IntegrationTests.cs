using System;
using System.IO;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using NoiseReduction.Platform;
class IntegrationTests {
 static void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
 static int Main(string[] args) {
  AudioGraphTests.Run(); if(Array.IndexOf(args,"--audio-graph-only")>=0) return 0;
  string path = "Software\\NoiseReductionTests\\" + Guid.NewGuid().ToString("N");
  string journal = Path.Combine(Path.GetTempPath(), "nr-tests-" + Guid.NewGuid().ToString("N"));
  try {
   using (var root = Registry.CurrentUser.CreateSubKey(path)) {
    var engine = new RegistryEngine(root, journal);
    string fx = "Capture\\{11111111-1111-1111-1111-111111111111}\\FxProperties";
    string oem = "{22222222-2222-2222-2222-222222222222}";
    using (var k = root.CreateSubKey(fx)) k.SetValue(Integration.CompositeKey, new string[]{oem}, RegistryValueKind.MultiString);
    engine.Attach(fx); engine.Attach(fx);
    using (var k = root.OpenSubKey(fx, true)) {
     Assert(((string[])k.GetValue(Integration.CompositeKey)).Length == 2, "idempotent append");
     k.SetValue(Integration.CompositeKey, new string[]{oem, Integration.Clsid, "{33333333-3333-3333-3333-333333333333}"}, RegistryValueKind.MultiString);
    }
    engine.Detach(fx);
    using (var k = root.OpenSubKey(fx)) Assert(((string[])k.GetValue(Integration.CompositeKey)).Length == 2, "preserve later OEM change");
    string pack = "Capture\\{55555555-5555-5555-5555-555555555555}\\FxProperties";
    using (var k = root.CreateSubKey(pack)) { k.SetValue(Integration.EffectPackKey, "SWD\\DRIVERENUM\\Pack"); k.SetValue(Integration.ModeKey, new string[]{oem}, RegistryValueKind.MultiString); }
    engine.Attach(pack); engine.Attach(pack);
    using (var k = root.OpenSubKey(pack)) Assert(((string[])k.GetValue(Integration.ModeKey)).Length == 2 && k.GetValue(Integration.CompositeKey) == null && Integration.Hooked(k), "effect pack endpoint uses mode chain once");
    engine.Detach(pack);
    using (var k = root.OpenSubKey(pack)) Assert(((string[])k.GetValue(Integration.ModeKey)).Length == 1 && !Integration.Hooked(k), "effect pack detach restores mode chain");
    string empty = "Capture\\{44444444-4444-4444-4444-444444444444}\\FxProperties";
    root.CreateSubKey(empty).Dispose(); engine.Attach(empty); engine.Detach(empty);
    using (var k = root.OpenSubKey(empty)) Assert(k.GetValue(Integration.CompositeKey) == null, "restore absent value");
    using (var k = root.OpenSubKey(empty, true)) k.SetValue(Integration.CompositeKey, 42, RegistryValueKind.DWord);
    bool conflict = false; try { engine.Attach(empty); } catch (InvalidOperationException) { conflict = true; }
    Assert(conflict, "reject incompatible registry type");
    using (var k = root.OpenSubKey(empty)) Assert(k.GetValueKind(Integration.CompositeKey) == RegistryValueKind.DWord, "preserve conflicting type");
    string crash = "Capture\\{55555555-5555-5555-5555-555555555555}\\FxProperties";
    root.CreateSubKey(crash).Dispose();
    engine.BeforeWrite = delegate { throw new IOException("injected crash"); };
    try { engine.Attach(crash); } catch(IOException) { }
    engine = new RegistryEngine(root, journal); engine.Recover();
    using(var k = root.OpenSubKey(crash)) Assert(k.GetValue(Integration.CompositeKey) == null, "recover pre-write failure");
    engine.Attach(crash); engine.Detach(crash);
    string legacy = "Capture\\{66666666-6666-6666-6666-666666666666}\\FxProperties";
    using(var k=root.CreateSubKey(legacy)) k.SetValue(Integration.LegacyKey,oem,RegistryValueKind.String);
    conflict=false; try { engine.Attach(legacy); } catch(InvalidOperationException) { conflict=true; }
    Assert(conflict,"refuse unsafe legacy OEM migration");
    using(var k=root.OpenSubKey(legacy)) Assert((string)k.GetValue(Integration.LegacyKey)==oem && k.GetValue(Integration.CompositeKey)==null,"legacy remains exact");
    string types="Registration";
    using(var k=root.CreateSubKey(types)) { k.SetValue("expand","%TEMP%\\test",RegistryValueKind.ExpandString); k.SetValue("binary",new byte[]{0,1,255},RegistryValueKind.Binary); }
    var checkpoint=engine.Checkpoint(); engine.Set(types,"expand","replace",RegistryValueKind.String); engine.Set(types,"binary",13,RegistryValueKind.DWord); engine.Set(types,"absent",42,RegistryValueKind.DWord);
    engine.RestoreSince(checkpoint);
    using(var k=root.OpenSubKey(types)) { Assert(k.GetValueKind("expand")==RegistryValueKind.ExpandString && (string)k.GetValue("expand",null,RegistryValueOptions.DoNotExpandEnvironmentNames)=="%TEMP%\\test","restore unexpanded type"); Assert(k.GetValueKind("binary")==RegistryValueKind.Binary && ((byte[])k.GetValue("binary"))[2]==255,"restore binary bytes"); Assert(k.GetValue("absent")==null,"restore missing registration"); }
    string unplug="Capture\\{77777777-7777-7777-7777-777777777777}\\FxProperties";
    root.CreateSubKey(unplug).Dispose(); engine.Attach(unplug); root.DeleteSubKeyTree(unplug); engine.Detach(unplug);
    using(var k=root.CreateSubKey(unplug)) k.SetValue(Integration.CompositeKey,new[]{Integration.Clsid},RegistryValueKind.MultiString);
    engine.DetachJournaledEndpoints(); using(var k=root.OpenSubKey(unplug)) Assert(k.GetValue(Integration.CompositeKey)==null,"detach replugged endpoint");
    for(int repeat=0;repeat<16;repeat++) {
     string repeated="Repeated"+repeat;
     using(var k=root.CreateSubKey(repeated)) k.SetValue("value","A",RegistryValueKind.String);
     checkpoint=engine.Checkpoint(); engine.Set(repeated,"value","B",RegistryValueKind.String); engine.Set(repeated,"value","C",RegistryValueKind.String);
     engine=new RegistryEngine(root,journal); engine.RestoreSince(checkpoint);
     using(var k=root.OpenSubKey(repeated)) Assert((string)k.GetValue("value")=="A","reverse checkpoint rollback across restart");
     engine.Set(repeated,"value","B",RegistryValueKind.String); engine.Set(repeated,"value","C",RegistryValueKind.String);
     engine=new RegistryEngine(root,journal); engine.RestorePrefix(repeated);
     using(var k=root.OpenSubKey(repeated)) Assert((string)k.GetValue("value")=="A","reverse prefix rollback across restart");
    }
    string restricted="RestrictedFx";
    using(var k=root.CreateSubKey(restricted)) { var acl=k.GetAccessControl(); acl.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User,RegistryRights.CreateSubKey,AccessControlType.Deny)); k.SetAccessControl(acl); }
    engine.Set(restricted,"value","set without subkey creation rights",RegistryValueKind.String);
    using(var k=root.OpenSubKey(restricted)) Assert((string)k.GetValue("value")=="set without subkey creation rights","write existing value without CreateSubKey rights");
    engine.RestorePrefix(restricted);
    string setupTransaction=Guid.NewGuid().ToString("N"); RegistryEngine.CurrentTransactionId=setupTransaction;
    try { engine.Set("SetupOwned","value","setup",RegistryValueKind.String); } finally { RegistryEngine.CurrentTransactionId=null; }
    engine.Set("LaterUserAttach","value","keep",RegistryValueKind.String);
    engine=new RegistryEngine(root,journal); engine.RestoreTransaction(setupTransaction);
    using(var k=root.OpenSubKey("SetupOwned")) Assert(k.GetValue("value")==null,"restore only setup transaction ID");
    using(var k=root.OpenSubKey("LaterUserAttach")) Assert((string)k.GetValue("value")=="keep","preserve later unrelated user attachment");
    engine.RestorePrefix("LaterUserAttach");
    string verificationPath="Capture\\{88888888-8888-8888-8888-888888888888}\\FxProperties";
    using(var k=root.CreateSubKey(verificationPath)) k.SetValue(Integration.CompositeKey,new[]{oem},RegistryValueKind.MultiString);
    engine.BeginVerifiedAttachment(verificationPath); engine.Attach(verificationPath);
    RegistryEngine.CurrentTransactionId=null;
    engine=new RegistryEngine(root,journal); engine.Recover();
    using(var k=root.OpenSubKey(verificationPath)) Assert(((string[])k.GetValue(Integration.CompositeKey)).Length==1,"process loss rolls back unverified committed FX record");
    Assert(Directory.GetFiles(journal,"*.verify").Length==0,"process loss cleans verification marker");
    using(var scope=engine.BeginVerifiedAttachment(verificationPath)) { engine.Attach(verificationPath); engine.Recover(); using(var k=root.OpenSubKey(verificationPath)) Assert(RegistryEngine.Contains(k.GetValue(Integration.CompositeKey),Integration.Clsid),"active scope survives internal recovery"); scope.Commit(); }
    engine=new RegistryEngine(root,journal); engine.Recover();
    using(var k=root.OpenSubKey(verificationPath)) Assert(RegistryEngine.Contains(k.GetValue(Integration.CompositeKey),Integration.Clsid),"successful verification commit survives restart");
    using(var scope=engine.BeginVerifiedAttachment(verificationPath)) { engine.Attach(verificationPath); }
    using(var k=root.OpenSubKey(verificationPath)) Assert(RegistryEngine.Contains(k.GetValue(Integration.CompositeKey),Integration.Clsid),"failed verification never undoes preexisting attachment");
    engine.Detach(verificationPath);
    using(var scope=engine.BeginVerifiedAttachment(verificationPath)) { engine.Attach(verificationPath); using(var k=root.OpenSubKey(verificationPath,true)) k.SetValue(Integration.CompositeKey,new[]{oem,Integration.Clsid,"{99999999-9999-9999-9999-999999999999}"},RegistryValueKind.MultiString); }
    using(var k=root.OpenSubKey(verificationPath)) Assert(((string[])k.GetValue(Integration.CompositeKey)).Length==2 && !RegistryEngine.Contains(k.GetValue(Integration.CompositeKey),Integration.Clsid),"verification rollback preserves later OEM chain changes");
    engine.BeginVerifiedAttachment(verificationPath);
    Assert(Directory.GetFiles(journal,"*.verify").Length==1,"verification marker is durable before Attach");
    RegistryEngine.CurrentTransactionId=null; engine=new RegistryEngine(root,journal); engine.Recover();
    Assert(Directory.GetFiles(journal,"*.verify").Length==0,"empty verification scope recovers without FX mutation");
    engine.BeginVerifiedAttachment(verificationPath); engine.Attach(verificationPath);
    using(var k=root.OpenSubKey(verificationPath,true)) k.SetValue(Integration.CompositeKey,new[]{oem,"{99999999-9999-9999-9999-999999999999}",Integration.Clsid,"{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"},RegistryValueKind.MultiString);
    RegistryEngine.CurrentTransactionId=null; engine=new RegistryEngine(root,journal); engine.Recover();
    using(var k=root.OpenSubKey(verificationPath)) Assert(((string[])k.GetValue(Integration.CompositeKey)).Length==3 && !RegistryEngine.Contains(k.GetValue(Integration.CompositeKey),Integration.Clsid),"crash recovery preserves later OEM additions");
    File.WriteAllText(Path.Combine(journal,"interrupted.tmp"),"<RegistryChange><truncated");
    engine.Recover(); Assert(!File.Exists(Path.Combine(journal,"interrupted.tmp")),"clean incomplete unpublished record"); engine.Set("AfterInterruptedPublication","value",1,RegistryValueKind.DWord); engine.RestorePrefix("AfterInterruptedPublication");
    Console.WriteLine("PASS integration preservation, types, idempotence, conflicts, crash recovery, unplug/replug, ordered repeated rollback, transaction isolation, limited registry rights, durable verified attachment, interrupted publication");
   }
   return 0;
  } finally {
   using(var restrictedKey=Registry.CurrentUser.OpenSubKey(path+"\\RestrictedFx",RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryRights.ReadPermissions|RegistryRights.ChangePermissions)) if(restrictedKey!=null) { var acl=restrictedKey.GetAccessControl(); acl.RemoveAccessRuleSpecific(new RegistryAccessRule(WindowsIdentity.GetCurrent().User,RegistryRights.CreateSubKey,AccessControlType.Deny)); restrictedKey.SetAccessControl(acl); }
   Registry.CurrentUser.DeleteSubKeyTree(path, false); if(Directory.Exists(journal)) Directory.Delete(journal, true); }
 }
}

class AudioGraphTests {
 sealed class FakeServices : IAudioServices {
  internal readonly Dictionary<string,ServiceControllerStatus> States=new Dictionary<string,ServiceControllerStatus> { {"Audiosrv",ServiceControllerStatus.Running},{"Rtk",ServiceControllerStatus.Running},{"Leaf",ServiceControllerStatus.Running},{"Other",ServiceControllerStatus.Running},{"Idle",ServiceControllerStatus.Stopped} };
  internal readonly List<string> Calls=new List<string>(); internal readonly List<double> Waits=new List<double>();
  internal long Now; internal string FailStop,FailWait,FailStart; internal int WaitCost;
  public string[] Dependents(string name) { return name=="Audiosrv" ? new[]{"Rtk","Idle","Other"} : name=="Rtk" ? new[]{"Leaf"} : new string[0]; }
  public ServiceControllerStatus Status(string name) { return States[name]; }
  public void Stop(string name) { Calls.Add("stop:"+name); if(FailStop==name) throw new InvalidOperationException("stop refused"); States[name]=ServiceControllerStatus.StopPending; }
  public void Start(string name) { Calls.Add("start:"+name); if(FailStart==name) throw new InvalidOperationException("start refused"); States[name]=ServiceControllerStatus.StartPending; }
  public void Wait(string name,ServiceControllerStatus desired,TimeSpan timeout) { Calls.Add("wait:"+name+":"+desired); Waits.Add(timeout.TotalMilliseconds); if(FailWait==name && desired==ServiceControllerStatus.Stopped) { FailWait=null; Now+=(long)timeout.TotalMilliseconds; throw new System.TimeoutException("injected stop timeout"); } if(WaitCost>timeout.TotalMilliseconds) { Now+=(long)timeout.TotalMilliseconds; throw new System.TimeoutException("phase budget exhausted"); } Now+=WaitCost; States[name]=desired; }
 }
 static void Check(bool value,string message) { if(!value) throw new Exception(message); }
 internal static void Run() {
  var f=new FakeServices(); AudioGraphRefresh.Run(f,delegate{return f.Now;});
  Check(f.Calls.IndexOf("stop:Leaf")<f.Calls.IndexOf("stop:Rtk") && f.Calls.IndexOf("stop:Rtk")<f.Calls.IndexOf("stop:Audiosrv"),"dependents stop before Windows Audio");
  Check(f.Calls.IndexOf("start:Audiosrv")<f.Calls.IndexOf("start:Rtk") && f.Calls.IndexOf("start:Rtk")<f.Calls.IndexOf("start:Leaf"),"Windows Audio and parents restore before dependents");
  Check(!f.Calls.Contains("stop:Idle") && !f.Calls.Contains("start:Idle"),"initially stopped services stay stopped");
  foreach(var state in f.States) Check(state.Value==(state.Key=="Idle" ? ServiceControllerStatus.Stopped : ServiceControllerStatus.Running),"initial service states restored");
  f=new FakeServices{FailStop="Audiosrv"}; bool failed=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;}); } catch(InvalidOperationException) { failed=true; }
  Check(failed && f.States["Leaf"]==ServiceControllerStatus.Running && f.States["Rtk"]==ServiceControllerStatus.Running && f.States["Other"]==ServiceControllerStatus.Running,"restore stopped dependents if audio stop fails");
  f=new FakeServices{FailWait="Rtk"}; failed=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;}); } catch(InvalidOperationException) { failed=true; }
  Check(failed && f.States["Rtk"]==ServiceControllerStatus.Running && f.States["Leaf"]==ServiceControllerStatus.Running,"bounded stop timeout still restores pending service and dependents");
  foreach(var wait in f.Waits) Check(wait>0 && wait<=30000,"all service waits explicitly bounded");
  f=new FakeServices{FailStart="Rtk"}; failed=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;}); } catch(InvalidOperationException error) { failed=error.Message.Contains("Rtk"); }
  Check(failed && f.Calls.Contains("start:Leaf") && f.States["Other"]==ServiceControllerStatus.Running,"failed dependent restore is reported and other restoration attempts continue");
  f=new FakeServices{WaitCost=11000}; failed=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;}); } catch(InvalidOperationException) { failed=true; }
  Check(failed,"expired restoration waits are reported even when remaining services receive Start attempts");
  Check(f.Now<=60000,"separate global stop and restoration budgets bound total waits");
  foreach(var name in new[]{"Leaf","Rtk","Other"}) Check(f.Calls.Contains("start:"+name),"every stopped originally running service gets a restoration attempt even after wait budget expires: "+name);
  Check(!f.Calls.Contains("start:Idle") && !f.Calls.Contains("start:Audiosrv"),"exhausted wait budget does not start initially stopped or already running services");
  Check(f.States["Leaf"]!=ServiceControllerStatus.Stopped,"leaf is not left stopped just because previous restoration used the wait budget");
  f=new FakeServices(); f.States["Rtk"]=ServiceControllerStatus.StartPending; failed=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;}); } catch(InvalidOperationException) { failed=true; }
  Check(failed && f.Calls.Count==0,"unstable initial service snapshot rejects refresh before changes");
  f=new FakeServices(); f.States["Audiosrv"]=ServiceControllerStatus.Stopped; failed=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;}); } catch(InvalidOperationException) { failed=true; }
  Check(failed && f.Calls.Count==0,"initially stopped Windows Audio is not unexpectedly started");
  f=new FakeServices(); string[] saved=null; bool callback=false;
  AudioGraphRefresh.Run(f,delegate{return f.Now;},delegate { callback=true; foreach(var state in f.States) Check(state.Value==ServiceControllerStatus.Stopped,"maintenance callback executes only with graph stopped"); },delegate(string[] names) { saved=names; Check(f.Calls.Count==0,"durable snapshot callback precedes any stop"); },null);
  Check(callback && saved.Length==4,"service snapshot includes every originally running service");
  f=new FakeServices{FailStop="Audiosrv"}; callback=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;},delegate { callback=true; },null,null); } catch(InvalidOperationException) { }
  Check(!callback && f.States["Leaf"]==ServiceControllerStatus.Running,"failed graph stop never runs replacement callback and restores dependents");
  f=new FakeServices(); failed=false; try { AudioGraphRefresh.Run(f,delegate{return f.Now;},delegate { throw new IOException("replacement failed"); },null,null); } catch(InvalidOperationException) { failed=true; }
  Check(failed && f.States["Audiosrv"]==ServiceControllerStatus.Running && f.States["Leaf"]==ServiceControllerStatus.Running,"maintenance failure still restores graph and dependents");
  f=new FakeServices(); foreach(var name in new[]{"Audiosrv","Leaf","Rtk","Other"}) f.States[name]=ServiceControllerStatus.Stopped;
  AudioGraphRefresh.Run(f,delegate{return f.Now;},delegate { Check(f.States["Audiosrv"]==ServiceControllerStatus.Stopped,"crash recovery action keeps stopped graph stopped until replacement rollback finishes"); },null,saved);
  Check(f.States["Audiosrv"]==ServiceControllerStatus.Running && f.States["Leaf"]==ServiceControllerStatus.Running && !f.Calls.Contains("start:Idle"),"persisted original services restore after process loss");
  Console.WriteLine("PASS audio graph service ordering, original states, bounded waits, stopped callbacks and crash restoration");
 }
}
