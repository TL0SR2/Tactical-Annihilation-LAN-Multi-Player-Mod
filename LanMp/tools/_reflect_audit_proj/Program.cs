using System;
using System.Linq;
using System.Reflection;
class Program {
  static Assembly A;
  static void Main() {
    A = Assembly.LoadFrom(@"E:\SteamLibrary\steamapps\common\Tactical Annihilation\AnnW_Data\Managed\Assembly-CSharp.dll");
    Dump("Action_SetTrainPos");
    Dump("Action_TrainUnit");
    Dump("ANNW.UI_EcoInfo");
    Dump("UI_PlayerInfoManager");
    Dump("ANNW.UnitRenderer_RallyPoint");
    Dump("ANNW.UI_COInfo_ResTipArea");
    // find types that implement/handle SELF_DESTRUCT
    foreach (var t in A.GetTypes().Where(t => t.Name.Contains("Destruct") || t.Name.Contains("Suicide") || t.Name.Contains("Delete") || t.Name.Contains("Scrap") || t.Name.Contains("Disband") || t.Name.Contains("SelfKill")))
      Console.WriteLine("HIT " + t.FullName);
    // ActionData subclasses
    var ad = A.GetType("ActionData") ?? A.GetTypes().First(t => t.Name=="ActionData");
    foreach (var t in A.GetTypes().Where(t => t.IsSubclassOf(ad)).OrderBy(t => t.Name))
      Console.WriteLine("ACT " + t.FullName);
    // Player Event_MetalPowerChange usage - find methods named like OnMetal
    foreach (var t in A.GetTypes()) {
      MethodInfo[] ms;
      try { ms = t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly); } catch { continue; }
      foreach (var m in ms.Where(m => m.Name.IndexOf("Metal", StringComparison.OrdinalIgnoreCase)>=0 || m.Name.IndexOf("Resource", StringComparison.OrdinalIgnoreCase)>=0 || m.Name.IndexOf("Eco", StringComparison.OrdinalIgnoreCase)>=0))
        if (!t.Name.StartsWith("<"))
          Console.WriteLine("MTH " + t.FullName + "." + m.Name);
    }
  }
  static void Dump(string name) {
    var t = A.GetType(name) ?? A.GetTypes().FirstOrDefault(x => x.Name==name || x.FullName==name);
    Console.WriteLine("==== " + name + " => " + (t?.FullName ?? "null"));
    if (t==null) return;
    foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
      Console.WriteLine("  M " + m.Name + " (" + string.Join(", ", m.GetParameters().Select(p=>p.ParameterType.Name)) + ")");
    foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
      Console.WriteLine("  F " + f.FieldType.Name + " " + f.Name);
  }
}
