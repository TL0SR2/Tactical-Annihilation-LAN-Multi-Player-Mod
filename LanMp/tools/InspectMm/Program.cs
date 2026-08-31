using System;
using System.Reflection;
using System.Linq;
class Program {
  static void Main() {
    var a = Assembly.LoadFrom(@"E:\SteamLibrary\steamapps\common\Tactical Annihilation\AnnW_Data\Managed\Assembly-CSharp.dll");
    foreach (var tname in new[]{"ANNW.UI_MENU_POP_SkirmishSelect","UI_MENU_POP_SkirmishSelect","ANNW.UI_MENU_LevelSelect_InfoSkm","UI_Stackable"}) {
      var t = a.GetType(tname) ?? a.GetTypes().FirstOrDefault(x => x.Name == tname || x.FullName == tname);
      Console.WriteLine("==== "+tname+" => "+(t?.FullName??"null"));
      if (t==null) continue;
      foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
        if (f.Name.IndexOf("rect", StringComparison.OrdinalIgnoreCase)>=0 || f.Name.IndexOf("panel", StringComparison.OrdinalIgnoreCase)>=0 || f.Name.IndexOf("size", StringComparison.OrdinalIgnoreCase)>=0)
          Console.WriteLine("  F "+f.FieldType.Name+" "+f.Name);
    }
  }
}
