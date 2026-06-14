using System.Text;
using ClaudetRelay.Models;

namespace ClaudetRelay.Services;

public enum ExportLanguage { CSharp, Cpp, Java, TypeScript, Python, Kotlin, Swift, Php, Go, Rust }

/// <summary>
/// Generates code skeletons from CodeEntity definitions for several languages.
/// OOP languages map cleanly; Go and Rust have no class inheritance, so a base class
/// is approximated by composition/embedding (with a note).
/// </summary>
public static class CodeExportService
{
    public static string FileExtension(ExportLanguage lang) => lang switch
    {
        ExportLanguage.CSharp     => "cs",
        ExportLanguage.Cpp        => "h",
        ExportLanguage.Java       => "java",
        ExportLanguage.TypeScript => "ts",
        ExportLanguage.Python     => "py",
        ExportLanguage.Kotlin     => "kt",
        ExportLanguage.Swift      => "swift",
        ExportLanguage.Php        => "php",
        ExportLanguage.Go         => "go",
        ExportLanguage.Rust       => "rs",
        _                         => "txt"
    };

    public static string Generate(IEnumerable<CodeEntity> entities, ExportLanguage lang)
    {
        var all  = entities.ToList();
        var byId = all.ToDictionary(e => e.Id);
        string Name(string id) => byId.TryGetValue(id, out var e) ? e.Name : "";

        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated skeleton from ClaudetRelay code boards. Fill in the logic.");
        if (lang == ExportLanguage.Php) sb.AppendLine("<?php");
        if (lang == ExportLanguage.Python) sb.AppendLine(PythonImports(all));
        sb.AppendLine();

        var groups = all.GroupBy(e => e.Namespace?.Trim() ?? "").OrderBy(g => g.Key);

        foreach (var grp in groups)
        {
            bool hasNs = !string.IsNullOrWhiteSpace(grp.Key);
            string ind = "";
            bool braceNs = lang is ExportLanguage.CSharp or ExportLanguage.Cpp or ExportLanguage.TypeScript or ExportLanguage.Php or ExportLanguage.Rust;

            if (hasNs)
            {
                if (braceNs)
                {
                    string nsKw = lang == ExportLanguage.Rust ? $"mod {grp.Key.ToLowerInvariant()}" : $"namespace {grp.Key}";
                    sb.AppendLine($"{nsKw} {{");
                    ind = "    ";
                }
                else
                {
                    // package/module style can't repeat per group in one file → comment marker
                    sb.AppendLine($"// namespace / package: {grp.Key}");
                }
            }

            foreach (var e in grp.OrderBy(SortRank).ThenBy(x => x.Name))
            {
                EmitEntity(sb, e, lang, ind, Name);
                sb.AppendLine();
            }

            if (hasNs && braceNs) sb.AppendLine("}");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static int SortRank(CodeEntity e) => e.EntityType switch
    {
        CodeEntityType.Enum => 0, CodeEntityType.Interface => 1, CodeEntityType.Struct => 2,
        CodeEntityType.Class => 3, CodeEntityType.Function => 4, CodeEntityType.Object => 5, _ => 6
    };

    private static void EmitEntity(StringBuilder sb, CodeEntity e, ExportLanguage lang, string ind, Func<string, string> name)
    {
        Doc(sb, e, ind);
        switch (lang)
        {
            case ExportLanguage.CSharp:     EmitCSharp(sb, e, ind, name); break;
            case ExportLanguage.Cpp:        EmitCpp(sb, e, ind, name); break;
            case ExportLanguage.Java:       EmitJava(sb, e, ind, name); break;
            case ExportLanguage.TypeScript: EmitTypeScript(sb, e, ind, name); break;
            case ExportLanguage.Python:     EmitPython(sb, e, ind, name); break;
            case ExportLanguage.Kotlin:     EmitKotlin(sb, e, ind, name); break;
            case ExportLanguage.Swift:      EmitSwift(sb, e, ind, name); break;
            case ExportLanguage.Php:        EmitPhp(sb, e, ind, name); break;
            case ExportLanguage.Go:         EmitGo(sb, e, ind, name); break;
            case ExportLanguage.Rust:       EmitRust(sb, e, ind, name); break;
        }
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private static void Doc(StringBuilder sb, CodeEntity e, string ind, string marker = "//")
    {
        if (string.IsNullOrWhiteSpace(e.Description)) return;
        foreach (var line in e.Description.Split('\n'))
            sb.AppendLine($"{ind}{marker} {line.TrimEnd()}");
    }

    private static List<string> Bases(CodeEntity e, Func<string, string> name)
    {
        var b = new List<string>();
        if (!string.IsNullOrEmpty(e.BaseClassId)) b.Add(name(e.BaseClassId));
        b.AddRange(e.ImplementsIds.Select(name));
        return b.Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    private static (List<CodePort> ins, string ret) FuncSig(CodeEntity e)
    {
        var ins = e.Ports.Where(p => p.Direction == PortDirection.Input).ToList();
        var outp = e.Ports.FirstOrDefault(p => p.Direction == PortDirection.Output);
        var ret = outp?.DataType is { Length: > 0 } rt ? rt : "void";
        return (ins, ret);
    }

    // ── C# ──────────────────────────────────────────────────────────────────

    private static void EmitCSharp(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v.ToString().ToLowerInvariant();
        string Conv(PassingConvention c) => c is PassingConvention.Reference or PassingConvention.Pointer ? "ref " : "";
        var inner = ind + "    ";

        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}public enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{Conv(p.Convention)}{p.DataType} {p.Name}"));
                sb.AppendLine($"{ind}public static {ret} {e.Name}({ps})");
                sb.AppendLine($"{ind}{{");
                if (ret.Trim() is not ("void" or "")) sb.AppendLine($"{ind}    throw new System.NotImplementedException();");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {(string.IsNullOrEmpty(e.InstanceOfId) ? "var" : name(e.InstanceOfId))} {e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : e.EntityType == CodeEntityType.Struct ? "struct" : "class";
                var bases = Bases(e, name);
                var head = $"{ind}public {kw} {e.Name}" + (bases.Count > 0 ? " : " + string.Join(", ", bases) : "");
                sb.AppendLine(head);
                sb.AppendLine($"{ind}{{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)} {(f.IsStatic ? "static " : "")}{f.DataType} {f.Name}{(string.IsNullOrWhiteSpace(f.DefaultValue) ? "" : $" = {f.DefaultValue}")};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{Conv(p.Convention)}{p.DataType} {p.Name}"));
                    if (iface) sb.AppendLine($"{inner}{m.ReturnType} {m.Name}({ps});");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)} {(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({ps})");
                        sb.AppendLine($"{inner}{{");
                        if (m.ReturnType.Trim() is not ("void" or "")) sb.AppendLine($"{inner}    throw new System.NotImplementedException();");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── C++ ─────────────────────────────────────────────────────────────────

    private static void EmitCpp(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string Conv(PassingConvention c) => c switch { PassingConvention.Reference => "&", PassingConvention.Pointer => "*", _ => "" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum class {e.Name} {{ {string.Join(", ", e.EnumValues)} }};");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.DataType}{Conv(p.Convention)} {p.Name}"));
                sb.AppendLine($"{ind}{ret} {e.Name}({ps}) {{");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))} {e.Name};");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = e.EntityType == CodeEntityType.Struct ? "struct" : "class";
                var bases = Bases(e, name);
                var head = $"{ind}{kw} {e.Name}" + (bases.Count > 0 ? " : " + string.Join(", ", bases.Select(b => "public " + b)) : "");
                sb.AppendLine(head + " {");
                foreach (var vis in new[] { CodeVisibility.Public, CodeVisibility.Protected, CodeVisibility.Private })
                {
                    var fields  = iface ? new List<CodeField>() : e.Fields.Where(f => f.Visibility == vis).ToList();
                    var methods = e.Methods.Where(m => m.Visibility == vis).ToList();
                    if (fields.Count == 0 && methods.Count == 0) continue;
                    sb.AppendLine($"{ind}{vis.ToString().ToLowerInvariant()}:");
                    foreach (var f in fields)
                        sb.AppendLine($"{inner}{(f.IsStatic ? "static " : "")}{f.DataType} {f.Name};");
                    foreach (var m in methods)
                    {
                        var ps = string.Join(", ", m.Parameters.Select(p => $"{p.DataType}{Conv(p.Convention)} {p.Name}"));
                        sb.AppendLine($"{inner}{(iface ? "virtual " : "")}{(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({ps}){(iface ? " = 0;" : ";")}");
                    }
                }
                sb.AppendLine($"{ind}}};");
                break;
            }
        }
    }

    // ── Java ────────────────────────────────────────────────────────────────

    private static void EmitJava(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v == CodeVisibility.Internal ? "" : v.ToString().ToLowerInvariant() + " ";
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}public enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.DataType} {p.Name}"));
                sb.AppendLine($"{ind}public static {ret} {e.Name}({ps}) {{");
                if (ret.Trim() is not ("void" or "")) sb.AppendLine($"{ind}    throw new UnsupportedOperationException();");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {(string.IsNullOrEmpty(e.InstanceOfId) ? "var" : name(e.InstanceOfId))} {e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "class";
                var ext  = string.IsNullOrEmpty(e.BaseClassId) ? "" : " extends " + name(e.BaseClassId);
                var impl = e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var implS = impl.Count > 0 ? " implements " + string.Join(", ", impl) : "";
                sb.AppendLine($"{ind}public {kw} {e.Name}{ext}{implS} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}{(f.IsStatic ? "static " : "")}{f.DataType} {f.Name};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.DataType} {p.Name}"));
                    if (iface) sb.AppendLine($"{inner}{m.ReturnType} {m.Name}({ps});");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}{(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({ps}) {{");
                        if (m.ReturnType.Trim() is not ("void" or "")) sb.AppendLine($"{inner}    throw new UnsupportedOperationException();");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── TypeScript ────────────────────────────────────────────────────────────

    private static void EmitTypeScript(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private ", CodeVisibility.Protected => "protected ", _ => "" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}export enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}export function {e.Name}({ps}): {ret} {{");
                if (ret.Trim() is not ("void" or "")) sb.AppendLine($"{ind}    throw new Error(\"Not implemented\");");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: const {e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "class";
                var ext  = string.IsNullOrEmpty(e.BaseClassId) ? "" : " extends " + name(e.BaseClassId);
                var impl = e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var implS = impl.Count > 0 ? " implements " + string.Join(", ", impl) : "";
                sb.AppendLine($"{ind}export {kw} {e.Name}{ext}{implS} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}{(f.IsStatic ? "static " : "")}{f.Name}: {f.DataType};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                    if (iface) sb.AppendLine($"{inner}{m.Name}({ps}): {m.ReturnType};");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}{(m.IsStatic ? "static " : "")}{m.Name}({ps}): {m.ReturnType} {{");
                        if (m.ReturnType.Trim() is not ("void" or "")) sb.AppendLine($"{inner}    throw new Error(\"Not implemented\");");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── Python ────────────────────────────────────────────────────────────────

    private static string PythonImports(List<CodeEntity> all)
    {
        var lines = new List<string>();
        if (all.Any(e => e.EntityType == CodeEntityType.Enum)) lines.Add("from enum import Enum");
        if (all.Any(e => e.EntityType == CodeEntityType.Interface)) lines.Add("from abc import ABC, abstractmethod");
        if (all.Any(e => e.EntityType is CodeEntityType.Class or CodeEntityType.Struct && e.Fields.Count > 0))
            lines.Add("from dataclasses import dataclass");
        return string.Join("\n", lines);
    }

    private static void EmitPython(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string PyName(string n, CodeVisibility v) => v switch { CodeVisibility.Private => "__" + n, CodeVisibility.Protected => "_" + n, _ => n };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}class {e.Name}(Enum):");
                if (e.EnumValues.Count == 0) sb.AppendLine($"{inner}pass");
                else { int i = 1; foreach (var v in e.EnumValues) sb.AppendLine($"{inner}{v} = {i++}"); }
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}def {e.Name}({ps}) -> {(ret == "void" ? "None" : ret)}:");
                sb.AppendLine($"{inner}raise NotImplementedError");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}# instance: {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "...  # type" : name(e.InstanceOfId) + "()")}");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                var parents = new List<string>();
                if (!string.IsNullOrEmpty(e.BaseClassId)) parents.Add(name(e.BaseClassId));
                parents.AddRange(e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)));
                if (iface) parents.Insert(0, "ABC");
                var pStr = parents.Count > 0 ? $"({string.Join(", ", parents)})" : "";
                if (!iface && e.Fields.Count > 0) sb.AppendLine($"{ind}@dataclass");
                sb.AppendLine($"{ind}class {e.Name}{pStr}:");

                bool any = false;
                if (!iface)
                    foreach (var f in e.Fields)
                    {
                        sb.AppendLine($"{inner}{PyName(f.Name, f.Visibility)}: {f.DataType}{(string.IsNullOrWhiteSpace(f.DefaultValue) ? "" : $" = {f.DefaultValue}")}");
                        any = true;
                    }
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", new[] { "self" }.Concat(m.Parameters.Select(p => $"{p.Name}: {p.DataType}")));
                    if (iface) sb.AppendLine($"{inner}@abstractmethod");
                    sb.AppendLine($"{inner}def {PyName(m.Name, m.Visibility)}({ps}) -> {(m.ReturnType == "void" ? "None" : m.ReturnType)}:");
                    sb.AppendLine($"{inner}    {(iface ? "..." : "raise NotImplementedError")}");
                    any = true;
                }
                if (!any) sb.AppendLine($"{inner}pass");
                break;
            }
        }
    }

    // ── Kotlin ──────────────────────────────────────────────────────────────

    private static void EmitKotlin(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private ", CodeVisibility.Protected => "protected ", CodeVisibility.Internal => "internal ", _ => "" };
        string Ret(string r) => r.Trim() is "void" or "" ? "" : ": " + r;
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum class {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}fun {e.Name}({ps}){Ret(ret)} {{");
                sb.AppendLine($"{ind}    TODO(\"Not implemented\")");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: val {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */()" : name(e.InstanceOfId) + "()")}");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "open class";
                var bases = new List<string>();
                if (!string.IsNullOrEmpty(e.BaseClassId)) bases.Add(name(e.BaseClassId) + "()");
                bases.AddRange(e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)));
                var bStr = bases.Count > 0 ? " : " + string.Join(", ", bases) : "";
                sb.AppendLine($"{ind}{kw} {e.Name}{bStr} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}var {f.Name}: {f.DataType}{(string.IsNullOrWhiteSpace(f.DefaultValue) ? "" : $" = {f.DefaultValue}")}");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                    if (iface) sb.AppendLine($"{inner}fun {m.Name}({ps}){Ret(m.ReturnType)}");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}fun {m.Name}({ps}){Ret(m.ReturnType)} {{");
                        sb.AppendLine($"{inner}    TODO(\"Not implemented\")");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── Swift ───────────────────────────────────────────────────────────────

    private static void EmitSwift(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private ", CodeVisibility.Protected => "fileprivate ", CodeVisibility.Public => "public ", _ => "" };
        string Ret(string r) => r.Trim() is "void" or "" ? "" : " -> " + r;
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum {e.Name} {{");
                foreach (var v in e.EnumValues) sb.AppendLine($"{inner}case {v}");
                sb.AppendLine($"{ind}}}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}func {e.Name}({ps}){Ret(ret)} {{");
                sb.AppendLine($"{ind}    fatalError(\"Not implemented\")");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: let {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */()" : name(e.InstanceOfId) + "()")}");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "protocol" : e.EntityType == CodeEntityType.Struct ? "struct" : "class";
                var bases = new List<string>();
                if (!string.IsNullOrEmpty(e.BaseClassId)) bases.Add(name(e.BaseClassId));
                bases.AddRange(e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)));
                var bStr = bases.Count > 0 ? ": " + string.Join(", ", bases) : "";
                sb.AppendLine($"{ind}{kw} {e.Name}{bStr} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}{(f.IsStatic ? "static " : "")}var {f.Name}: {f.DataType}");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                    if (iface) sb.AppendLine($"{inner}func {m.Name}({ps}){Ret(m.ReturnType)}");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}{(m.IsStatic ? "static " : "")}func {m.Name}({ps}){Ret(m.ReturnType)} {{");
                        sb.AppendLine($"{inner}    fatalError(\"Not implemented\")");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── PHP ─────────────────────────────────────────────────────────────────

    private static void EmitPhp(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private", CodeVisibility.Protected => "protected", _ => "public" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum {e.Name} {{");
                foreach (var v in e.EnumValues) sb.AppendLine($"{inner}case {v};");
                sb.AppendLine($"{ind}}}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.DataType} ${p.Name}"));
                sb.AppendLine($"{ind}function {e.Name}({ps}): {ret} {{");
                if (ret.Trim() is not ("void" or "")) sb.AppendLine($"{ind}    throw new \\Exception(\"Not implemented\");");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: ${e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "class";
                var ext  = string.IsNullOrEmpty(e.BaseClassId) ? "" : " extends " + name(e.BaseClassId);
                var impl = e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var implS = impl.Count > 0 ? " implements " + string.Join(", ", impl) : "";
                sb.AppendLine($"{ind}{kw} {e.Name}{ext}{implS} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)} {(f.IsStatic ? "static " : "")}{f.DataType} ${f.Name};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.DataType} ${p.Name}"));
                    if (iface) sb.AppendLine($"{inner}public function {m.Name}({ps}): {m.ReturnType};");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)} {(m.IsStatic ? "static " : "")}function {m.Name}({ps}): {m.ReturnType} {{");
                        if (m.ReturnType.Trim() is not ("void" or "")) sb.AppendLine($"{inner}    throw new \\Exception(\"Not implemented\");");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── Go (no inheritance → embedding) ──────────────────────────────────────

    private static void EmitGo(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string Conv(PassingConvention c) => c switch { PassingConvention.Reference or PassingConvention.Pointer => "*", _ => "" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}type {e.Name} int");
                sb.AppendLine($"{ind}const (");
                bool first = true;
                foreach (var v in e.EnumValues) { sb.AppendLine($"{inner}{v}{(first ? $" {e.Name} = iota" : "")}"); first = false; }
                sb.AppendLine($"{ind})");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name} {Conv(p.Convention)}{p.DataType}"));
                var r  = ret.Trim() is "void" or "" ? "" : " " + ret;
                sb.AppendLine($"{ind}func {e.Name}({ps}){r} {{");
                sb.AppendLine($"{ind}    panic(\"not implemented\")");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {e.Name} := {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */{}" : name(e.InstanceOfId) + "{}")}");
                break;
            case CodeEntityType.Interface:
                sb.AppendLine($"{ind}type {e.Name} interface {{");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name} {p.DataType}"));
                    var r  = m.ReturnType.Trim() is "void" or "" ? "" : " " + m.ReturnType;
                    sb.AppendLine($"{inner}{m.Name}({ps}){r}");
                }
                sb.AppendLine($"{ind}}}");
                break;
            default: // Class / Struct → struct + methods; base class → embedded field
            {
                sb.AppendLine($"{ind}type {e.Name} struct {{");
                if (!string.IsNullOrEmpty(e.BaseClassId))
                    sb.AppendLine($"{inner}{name(e.BaseClassId)} // embedded (inheritance → composition)");
                foreach (var f in e.Fields)
                    sb.AppendLine($"{inner}{f.Name} {f.DataType}");
                sb.AppendLine($"{ind}}}");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name} {p.DataType}"));
                    var r  = m.ReturnType.Trim() is "void" or "" ? "" : " " + m.ReturnType;
                    sb.AppendLine($"{ind}func (recv *{e.Name}) {m.Name}({ps}){r} {{");
                    sb.AppendLine($"{ind}    panic(\"not implemented\")");
                    sb.AppendLine($"{ind}}}");
                }
                break;
            }
        }
    }

    // ── Rust (no inheritance → composition / traits) ─────────────────────────

    private static void EmitRust(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string Ret(string r) => r.Trim() is "void" or "" ? "" : " -> " + r;
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}pub enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}pub fn {e.Name}({ps}){Ret(ret)} {{");
                sb.AppendLine($"{ind}    unimplemented!()");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: let {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))} {{ }};");
                break;
            case CodeEntityType.Interface:
                sb.AppendLine($"{ind}pub trait {e.Name} {{");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", new[] { "&self" }.Concat(m.Parameters.Select(p => $"{p.Name}: {p.DataType}")));
                    sb.AppendLine($"{inner}fn {m.Name}({ps}){Ret(m.ReturnType)};");
                }
                sb.AppendLine($"{ind}}}");
                break;
            default: // Class / Struct → struct + impl; base → composition note
            {
                if (!string.IsNullOrEmpty(e.BaseClassId))
                    sb.AppendLine($"{ind}// note: Rust has no inheritance — base '{name(e.BaseClassId)}' modelled as composition");
                sb.AppendLine($"{ind}pub struct {e.Name} {{");
                if (!string.IsNullOrEmpty(e.BaseClassId))
                    sb.AppendLine($"{inner}base: {name(e.BaseClassId)},");
                foreach (var f in e.Fields)
                    sb.AppendLine($"{inner}{(f.Visibility == CodeVisibility.Public ? "pub " : "")}{f.Name}: {f.DataType},");
                sb.AppendLine($"{ind}}}");
                if (e.Methods.Count > 0)
                {
                    sb.AppendLine($"{ind}impl {e.Name} {{");
                    foreach (var m in e.Methods)
                    {
                        var ps = string.Join(", ", new[] { "&self" }.Concat(m.Parameters.Select(p => $"{p.Name}: {p.DataType}")));
                        sb.AppendLine($"{inner}{(m.Visibility == CodeVisibility.Public ? "pub " : "")}fn {m.Name}({ps}){Ret(m.ReturnType)} {{");
                        sb.AppendLine($"{inner}    unimplemented!()");
                        sb.AppendLine($"{inner}}}");
                    }
                    sb.AppendLine($"{ind}}}");
                }
                break;
            }
        }
    }
}
