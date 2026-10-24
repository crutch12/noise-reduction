using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace NoiseReduction.Platform {
 public sealed class CaptureSessionInfo {
  public uint ProcessId;
  public string ApplicationName,SessionIdentifier;
  public bool IsSystemSession,IsMultiProcess;
 }
 public sealed class CaptureSessionSnapshot {
  public string EndpointId,Error;
  public bool Available;
  public readonly List<CaptureSessionInfo> Sessions=new List<CaptureSessionInfo>();
 }
 public static class CaptureSessions {
  const int NoSingleProcess=0x0889000d;
  public static List<string> GetActiveApplications(string endpointId) {
   var snapshot=TryGetSnapshot(endpointId);
   return snapshot.Available ? snapshot.Sessions.Select(session=>session.ApplicationName).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;
  }
  public static CaptureSessionSnapshot TryGetSnapshot(string endpointId) {
   var snapshot=new CaptureSessionSnapshot { EndpointId=endpointId };
   string stage="validate request";
   IDeviceEnumerator enumerator=null; IDevice device=null; object managerObject=null; ISessionEnumerator sessions=null;
   try {
    if(String.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("A capture endpoint ID is required");
    if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.MTA) throw new InvalidOperationException("Capture sessions must be queried from an MTA worker thread");
    stage="create device enumerator"; enumerator=(IDeviceEnumerator)new DeviceEnumerator();
    stage="resolve capture endpoint"; Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId,out device));
    stage="validate capture data flow"; int flow; Marshal.ThrowExceptionForHR(((IEndpoint)device).GetDataFlow(out flow));
    if(flow!=1) throw new ArgumentException("The requested endpoint is not a capture device");
    var managerId=typeof(ISessionManager2).GUID;
    stage="activate capture session manager"; Marshal.ThrowExceptionForHR(device.Activate(ref managerId,23,IntPtr.Zero,out managerObject));
    var manager=(ISessionManager2)managerObject;
    stage="enumerate capture sessions"; Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
    stage="count capture sessions"; int count; Marshal.ThrowExceptionForHR(sessions.GetCount(out count));
    for(int index=0;index<count;index++) {
     ISessionControl control=null;
     try {
      stage="read capture session "+index; Marshal.ThrowExceptionForHR(sessions.GetSession(index,out control));
      int state; Marshal.ThrowExceptionForHR(control.GetState(out state));
      if(state!=1) continue;
      var details=(ISessionControl2)control;
      uint processId; int processResult=details.GetProcessId(out processId); Marshal.ThrowExceptionForHR(processResult);
      int systemResult=details.IsSystemSoundsSession(); Marshal.ThrowExceptionForHR(systemResult);
      string identifier; Marshal.ThrowExceptionForHR(details.GetSessionInstanceIdentifier(out identifier));
      bool system=systemResult==0 || processId==0;
      snapshot.Sessions.Add(new CaptureSessionInfo { ProcessId=processId,ApplicationName=system ? "System" : ProcessName(processId),SessionIdentifier=identifier,IsSystemSession=system,IsMultiProcess=processResult==NoSingleProcess });
     } finally { Release(control); }
    }
    snapshot.Available=true;
   } catch(Exception error) { snapshot.Error=stage+": "+error.Message; }
   finally { Release(sessions); Release(managerObject); Release(device); Release(enumerator); }
   return snapshot;
  }
  static string ProcessName(uint processId) {
   if(processId<=Int32.MaxValue) try { using(var process=Process.GetProcessById((int)processId)) { var name=process.ProcessName; if(!String.IsNullOrWhiteSpace(name)) return name; } } catch(ArgumentException) { } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { } catch(System.Security.SecurityException) { }
   return "PID "+processId;
  }
  static void Release(object instance) { if(instance!=null && Marshal.IsComObject(instance)) try { Marshal.ReleaseComObject(instance); } catch(InvalidComObjectException) { } }
  [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator { }
  [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDeviceEnumerator {
   [PreserveSig] int EnumAudioEndpoints(int flow,uint states,out IntPtr devices);
   [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IDevice device);
   [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IDevice device);
  }
  [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevice {
   [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)]out object result);
  }
  [ComImport,Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IEndpoint {
   [PreserveSig] int GetDataFlow(out int flow);
  }
  [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessionManager2 {
   [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid,uint flags,out IntPtr control);
   [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid,uint flags,out IntPtr volume);
   [PreserveSig] int GetSessionEnumerator(out ISessionEnumerator sessions);
  }
  [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessionEnumerator {
   [PreserveSig] int GetCount(out int count);
   [PreserveSig] int GetSession(int index,out ISessionControl control);
  }
  [ComImport,Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessionControl {
   [PreserveSig] int GetState(out int state);
  }
  [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessionControl2 {
   [PreserveSig] int GetState(out int state);
   [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)]out string name);
   [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr context);
   [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)]out string path);
   [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)]string path,IntPtr context);
   [PreserveSig] int GetGroupingParam(out Guid grouping);
   [PreserveSig] int SetGroupingParam(ref Guid grouping,IntPtr context);
   [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
   [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
   [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string identifier);
   [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string identifier);
   [PreserveSig] int GetProcessId(out uint processId);
   [PreserveSig] int IsSystemSoundsSession();
   [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)]bool optOut);
  }
 }
}
