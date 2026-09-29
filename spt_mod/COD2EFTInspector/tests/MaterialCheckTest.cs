// Unit test for MaterialCheck.cs (no Unity). Values mirror the user's real report (from_pc/20260929-071416, valeria).
using System;
using System.Collections.Generic;
using System.Linq;
using COD2EFTInspector;

static class MaterialCheckTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; }

    static MatInfo Good(string part, string shader, int stencil)
    {
        var m = new MatInfo { Part = part, Renderer = "r_" + part, Material = "m_" + part, Shader = shader, Skinned = true, Bones = 58 };
        m.Floats["_StencilType"] = stencil;
        foreach (var s in new[] { "_MainTex", "_BumpMap", "_SpecMap" }) m.Textures[s] = new TexInfo { Name = "t" + s, W = 2048, H = 2048 };
        return m;
    }

    public static int Run()
    {
        // what the real valeria report showed: all fine
        var ok = new List<MatInfo> { Good("Head", MaterialCheck.BodyShader, 1), Good("Top", MaterialCheck.BodyShader, 1), Good("Hands", MaterialCheck.HandsShader, 2) };
        var hair = new MatInfo { Part = "Head", Renderer = "lashes", Material = "Head_alpha", Shader = MaterialCheck.CutoutShader, Skinned = true, Bones = 58 };
        hair.Floats["_StencilType"] = 0;
        hair.Textures["_MainTex"] = new TexInfo { Name = "a_d", W = 1024, H = 1024 };
        hair.Textures["_BumpMap"] = new TexInfo { Name = "a_n", W = 1024, H = 1024 };
        ok.Add(hair);
        ok.Add(new MatInfo { Part = "", Renderer = "gear", Material = "x", Shader = "Standard", Skinned = false });   // gear isn't judged
        var r = MaterialCheck.Run(ok);
        Check(r.Count == 0, "real-report shapes pass: " + string.Join(" | ", r));

        var bad = Good("Top", "Standard", 1);
        Check(MaterialCheck.Run(new[] { bad }).Any(l => l.StartsWith("ERROR") && l.Contains("not an EFT shader")), "Standard shader flagged");
        var pink = Good("Top", "Hidden/InternalErrorShader", 1);
        Check(MaterialCheck.Run(new[] { pink }).Any(l => l.Contains("shader missing")), "missing shader flagged");
        var st = Good("Hands", MaterialCheck.HandsShader, 1);
        Check(MaterialCheck.Run(new[] { st }).Any(l => l.Contains("_StencilType 1, expected 2")), "hands stencil flagged");
        var wrong = Good("Hands", MaterialCheck.BodyShader, 2);
        Check(MaterialCheck.Run(new[] { wrong }).Any(l => l.Contains("expected 'p0/Reflective/Bumped Specular SMap'")), "hands on the body shader flagged");
        var empty = Good("Pants", MaterialCheck.BodyShader, 1); empty.Textures["_SpecMap"] = null;
        Check(MaterialCheck.Run(new[] { empty }).Any(l => l.Contains("_SpecMap is empty")), "empty texture slot flagged");
        var odd = Good("Pants", MaterialCheck.BodyShader, 1); odd.Textures["_MainTex"] = new TexInfo { Name = "x", W = 3000, H = 2048 };
        Check(MaterialCheck.Run(new[] { odd }).Any(l => l.Contains("not a power of two")), "non power of two flagged");
        var def = Good("Head", MaterialCheck.BodyShader, 1); def.Textures["_BumpMap"] = new TexInfo { Name = "bump", W = 4, H = 4 };
        Check(MaterialCheck.Run(new[] { def }).Any(l => l.Contains("Unity's default")), "default texture flagged");
        Check(MaterialCheck.Run(new[] { new MatInfo { Part = "Top", Renderer = "r", NoMaterial = true } }).Any(l => l.Contains("slot is empty")), "empty material slot flagged");
        Check(MaterialCheck.Run(new[] { new MatInfo { Part = "Top", Renderer = "r", Skinned = true, Bones = 0, Shader = MaterialCheck.BodyShader, Material = "m" } }).Any(l => l.Contains("0 bones")), "0 bones flagged");
        return fails;
    }
}
