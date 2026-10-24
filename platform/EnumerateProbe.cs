using System;
using NoiseReduction.Platform;
class Probe { [STAThread] static int Main() { foreach(var d in Integration.EnumerateMicrophones()) Console.WriteLine(d.Id + " | " + d.Name + " | " + d.Guid + " | active=" + d.Active + " | attached=" + d.Attached + " | enhancements=" + d.EnhancementsEnabled); return 0; } }
