using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NoiseReduction.Platform;
class CaptureSessionsTests {
 static void Check(bool value,string message) { if(!value) throw new Exception(message); }
 [MTAThread] static int Main(string[] args) {
  Check(!CaptureSessions.TryGetSnapshot(null).Available,"missing endpoint must be unknown, not unused");
  Check(CaptureSessions.GetActiveApplications("not-an-endpoint")==null,"failed lookup must not claim no active apps");
  var type=typeof(CaptureSessions); Type control=type.GetNestedType("ISessionControl2",BindingFlags.NonPublic);
  Check(control.GUID==new Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),"control2 SDK IID");
  string[] slots={"GetState","GetDisplayName","SetDisplayName","GetIconPath","SetIconPath","GetGroupingParam","SetGroupingParam","RegisterAudioSessionNotification","UnregisterAudioSessionNotification","GetSessionIdentifier","GetSessionInstanceIdentifier","GetProcessId","IsSystemSoundsSession","SetDuckingPreference"};
  MethodInfo[] methods=control.GetMethods(); Check(methods.Length==slots.Length,"complete inherited control2 vtable");
  for(int index=0;index<slots.Length;index++) Check(methods[index].Name==slots[index] && (methods[index].MethodImplementationFlags & MethodImplAttributes.PreserveSig)!=0,"HRESULT-preserving SDK slot "+index);
  CaptureSessionSnapshot sta=null; var thread=new Thread(delegate(){sta=CaptureSessions.TryGetSnapshot("not-an-endpoint");}); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
  Check(sta!=null && !sta.Available && sta.Error.Contains("MTA"),"UI apartment must return explicit unknown without COM activation");
  bool allAvailable=true;
  foreach(var endpoint in args) { var snapshot=CaptureSessions.TryGetSnapshot(endpoint); Console.WriteLine(endpoint+" available="+snapshot.Available+" active="+snapshot.Sessions.Count+" error="+snapshot.Error); foreach(var session in snapshot.Sessions) Console.WriteLine("  pid="+session.ProcessId+" app="+session.ApplicationName+" system="+session.IsSystemSession+" multi="+session.IsMultiProcess+" session="+session.SessionIdentifier); allAvailable &= snapshot.Available; }
  Check(allAvailable,"one or more real capture snapshots are unavailable");
  Console.WriteLine("PASS capture session ABI, unknown-state behavior and read-only snapshots"); return 0;
 }
}
