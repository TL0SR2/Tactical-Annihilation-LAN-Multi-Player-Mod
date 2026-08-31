using System;
using System.Linq;
using System.Reflection;

class Program
{
    static void Main()
    {
        var dll = @"E:\SteamLibrary\steamapps\common\Tactical Annihilation\AnnW_Data\Managed\Assembly-CSharp.dll";
        var a = Assembly.LoadFrom(dll);
        Dump(a, "UndoMoveData");
        Dump(a, "UndoableMoveInfo");
        Dump(a, "WP_Builder");
        Dump(a, "Action_TrainUnit");
        Dump(a, "Action_SetTrainPos");
        Dump(a, "Action_SelfDestruct");
        Dump(a, "GameUI");
        Dump(a, "UI_COInfoManager");
        Dump(a, "COInfoManager");
        Dump(a, "UI_ElementManager");

        Console.WriteLine("==== ActionCate");
        var cate = a.GetType("ActionCate") ?? a.GetTypes().First(t => t.Name == "ActionCate");
        foreach (var n in Enum.GetNames(cate))
            Console.WriteLine("  " + n);

        Console.WriteLine("==== Types match Self/Train/Scrap/Destruct/Rally/Resource/Metal/COInfo/UI_Part");
        foreach (var t in a.GetTypes().Where(t =>
                     t.Name.IndexOf("Self", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("Train", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("Scrap", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("Destruct", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("Rally", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("Resource", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("COInfo", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("UI_Part", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("ResBar", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.Name.IndexOf("PlayerInfo", StringComparison.OrdinalIgnoreCase) >= 0)
                 .OrderBy(t => t.FullName))
            Console.WriteLine("  " + t.FullName);

        // Search methods/fields mentioning train_pos / OnResourceChanged consumers via IL not easy;
        // dump WP_Builder and any type with metal Text fields named like txt_metal
        Console.WriteLine("==== Types with field name containing metal/power/resource");
        foreach (var t in a.GetTypes())
        {
            FieldInfo[] fields;
            try { fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
            catch { continue; }
            var hits = fields.Where(f =>
                f.Name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0
                || f.Name.IndexOf("power", StringComparison.OrdinalIgnoreCase) >= 0
                || f.Name.IndexOf("resource", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (hits.Length == 0) continue;
            if (t.Name.StartsWith("<")) continue;
            Console.WriteLine("  " + t.FullName);
            foreach (var f in hits.Take(12))
                Console.WriteLine("    F " + f.FieldType.Name + " " + f.Name);
        }
    }

    static void Dump(Assembly a, string name)
    {
        var t = a.GetType(name) ?? a.GetTypes().FirstOrDefault(x => x.Name == name || x.FullName == name);
        Console.WriteLine("==== " + name + " => " + (t?.FullName ?? "null"));
        if (t == null) return;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
            Console.WriteLine("  M " + m.Name + " (" + ps + ")");
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            Console.WriteLine("  F " + f.FieldType.Name + " " + f.Name);
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            Console.WriteLine("  P " + p.PropertyType.Name + " " + p.Name);
    }
}
