using BepInEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace MrPrepperLoadingProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class DialogueStartIlInspector : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.dialoguestartilinspector";
    public const string PluginName = "Mr. Prepper Dialogue IL Inspector";
    public const string PluginVersion = "0.7.0";

    private sealed class TargetSpec
    {
        public string Name;
        public Type[] Parameters;
    }

    private static readonly TargetSpec[] TargetMethods =
    {
        new() { Name = "Start", Parameters = Type.EmptyTypes },
        new() { Name = "SetParagraphs", Parameters = Type.EmptyTypes },
        new() { Name = "SetComponents", Parameters = Type.EmptyTypes },
        new() { Name = "SetParagraphsFromLocalization", Parameters = new[] { typeof(string) } },
        new() { Name = "SetParagraphsFromText", Parameters = new[] { typeof(string).MakeByRefType() } }
    };

    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static DialogueStartIlInspector()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!(field.GetValue(null) is OpCode op)) continue;
            var value = unchecked((ushort)op.Value);
            if (value < 0x100) OneByteOpCodes[value] = op;
            else if ((value & 0xff00) == 0xfe00) TwoByteOpCodes[value & 0xff] = op;
        }
    }

    private void Awake()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal));
        var dialogueType = assembly?.GetType("Characters.Dialogue", false);
        if (dialogueType == null)
        {
            Logger.LogWarning("[DIALOGUE IL] Characters.Dialogue was not found.");
            return;
        }

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. targetType={dialogueType.FullName}");
        foreach (var spec in TargetMethods) InspectMethod(dialogueType, spec);

        var paragraphType = assembly?.GetType("Characters.DialogueParagraph", false);
        if (paragraphType == null)
        {
            Logger.LogWarning("[DIALOGUE IL] Characters.DialogueParagraph was not found.");
            return;
        }

        InspectExactConstructor(paragraphType, new[] { typeof(string) }, "DialogueParagraph..ctor");
        InspectExactMethod(paragraphType, "Set", new[] { typeof(string) }, "DialogueParagraph.Set");
        InspectExactMethod(paragraphType, "SetDuration", Type.EmptyTypes, "DialogueParagraph.SetDuration");

        var textTagType = assembly?.GetType("TextTag", false);
        if (textTagType != null)
        {
            var getTag = textTagType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, "GetTag", StringComparison.Ordinal) &&
                                     m.GetParameters().Length == 3 &&
                                     m.GetParameters()[0].ParameterType == typeof(string).MakeByRefType() &&
                                     m.GetParameters()[1].ParameterType == typeof(string) &&
                                     m.GetParameters()[2].ParameterType == typeof(bool));
            if (getTag != null) InspectMethodBody(getTag, "TextTag.GetTag");
            else Logger.LogWarning("[DIALOGUE IL] TextTag.GetTag(String&,String,Boolean) was not found.");
        }
        else
        {
            Logger.LogWarning("[DIALOGUE IL] TextTag was not found.");
        }

        var audioType = assembly?.GetType("TagAudioSettings", false);
        if (audioType != null)
        {
            var setFields = audioType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, "SetFields", StringComparison.Ordinal) && m.GetParameters().Length == 1);
            if (setFields != null) InspectMethodBody(setFields, "TagAudioSettings.SetFields");
            else Logger.LogWarning("[DIALOGUE IL] TagAudioSettings.SetFields(...) was not found.");
        }
        else
        {
            Logger.LogWarning("[DIALOGUE IL] TagAudioSettings was not found.");
        }
    }

    private void InspectExactConstructor(Type type, Type[] parameters, string label)
    {
        var ctor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            parameters,
            null);
        if (ctor == null)
        {
            Logger.LogWarning($"[DIALOGUE IL] {type.FullName}..ctor({string.Join(",", parameters.Select(p => p.Name))}) was not found.");
            return;
        }
        InspectMethodBody(ctor, label);
    }

    private void InspectExactMethod(Type type, string name, Type[] parameters, string label)
    {
        var method = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null,
            parameters,
            null);
        if (method == null)
        {
            Logger.LogWarning($"[DIALOGUE IL] {type.FullName}.{name}({string.Join(",", parameters.Select(p => p.Name))}) was not found.");
            return;
        }
        InspectMethodBody(method, label);
    }

    private void InspectMethod(Type type, TargetSpec spec)
    {
        var method = type.GetMethod(
            spec.Name,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null,
            spec.Parameters,
            null);

        if (method == null)
        {
            Logger.LogWarning($"[DIALOGUE IL] {type.FullName}.{spec.Name}({string.Join(",", spec.Parameters.Select(p => p.Name))}) was not found.");
            return;
        }

        InspectMethodBody(method, spec.Name);
    }

    private void InspectMethodBody(MethodBase method, string label)
    {
        MethodBody body;
        try { body = method.GetMethodBody(); }
        catch (Exception ex)
        {
            Logger.LogWarning($"[DIALOGUE IL] Could not read {label} body: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var il = body?.GetILAsByteArray();
        if (il == null || il.Length == 0)
        {
            Logger.LogWarning($"[DIALOGUE IL] {DescribeMethod(method)} has no managed IL body.");
            return;
        }

        var calls = new List<string>();
        var fields = new List<string>();
        var strings = new List<string>();
        var module = method.Module;
        var declaringType = method.DeclaringType;
        var typeArgs = declaringType != null && declaringType.IsGenericType ? declaringType.GetGenericArguments() : Type.EmptyTypes;
        var methodArgs = method is MethodInfo mi && mi.IsGenericMethod ? mi.GetGenericArguments() : Type.EmptyTypes;
        var position = 0;

        while (position < il.Length)
        {
            var offset = position;
            var op = ReadOpCode(il, ref position);
            if (op.Size == 0)
            {
                Logger.LogWarning($"[DIALOGUE IL] Unknown opcode in {label} at IL_{offset:X4}; stopping scan.");
                break;
            }

            try
            {
                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                    {
                        var token = ReadInt32(il, ref position);
                        MethodBase target = null;
                        try { target = module.ResolveMethod(token, typeArgs, methodArgs); } catch { }
                        calls.Add($"IL_{offset:X4} {op.Name} {(target != null ? DescribeMethod(target) : $"token=0x{token:X8}")}");
                        break;
                    }
                    case OperandType.InlineField:
                    {
                        var token = ReadInt32(il, ref position);
                        FieldInfo field = null;
                        try { field = module.ResolveField(token, typeArgs, methodArgs); } catch { }
                        fields.Add($"IL_{offset:X4} {op.Name} {(field != null ? DescribeField(field) : $"token=0x{token:X8}")}");
                        break;
                    }
                    case OperandType.InlineString:
                    {
                        var token = ReadInt32(il, ref position);
                        string value = null;
                        try { value = module.ResolveString(token); } catch { }
                        strings.Add($"IL_{offset:X4} ldstr '{Trim(value, 180)}'");
                        break;
                    }
                    default:
                        SkipOperand(op.OperandType, il, ref position);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DIALOGUE IL] Parse error in {label} at IL_{offset:X4} opcode={op.Name}: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        Logger.LogInfo($"[DIALOGUE IL SUMMARY] method='{DescribeMethod(method)}' static={method.IsStatic} ilBytes={il.Length} directCalls={calls.Count} fieldAccesses={fields.Count} strings={strings.Count}");
        foreach (var entry in calls) Logger.LogInfo($"[DIALOGUE IL CALL] method='{label}' {entry}");
        foreach (var entry in fields) Logger.LogInfo($"[DIALOGUE IL FIELD] method='{label}' {entry}");
        foreach (var entry in strings) Logger.LogInfo($"[DIALOGUE IL STRING] method='{label}' {entry}");
    }

    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        var first = il[position++];
        if (first != 0xfe) return OneByteOpCodes[first];
        if (position >= il.Length) return default;
        return TwoByteOpCodes[il[position++]];
    }

    private static void SkipOperand(OperandType operandType, byte[] il, ref int position)
    {
        switch (operandType)
        {
            case OperandType.InlineNone: return;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar: position += 1; return;
            case OperandType.InlineVar: position += 2; return;
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR: position += 4; return;
            case OperandType.InlineI8:
            case OperandType.InlineR: position += 8; return;
            case OperandType.InlineSwitch:
                var count = ReadInt32(il, ref position);
                position += count * 4;
                return;
            default: throw new NotSupportedException("Unsupported operand type: " + operandType);
        }
    }

    private static int ReadInt32(byte[] il, ref int position)
    {
        var value = BitConverter.ToInt32(il, position);
        position += 4;
        return value;
    }

    private static string DescribeMethod(MethodBase method)
    {
        var declaring = method.DeclaringType != null ? method.DeclaringType.FullName : "<no-type>";
        var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));
        return $"{declaring}.{method.Name}({parameters})";
    }

    private static string DescribeField(FieldInfo field)
    {
        var declaring = field.DeclaringType != null ? field.DeclaringType.FullName : "<no-type>";
        return $"{declaring}.{field.Name}:{field.FieldType.Name}";
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "<null>";
        value = value.Replace("\r", "\\r").Replace("\n", "\\n");
        return value.Length <= max ? value : value.Substring(0, max) + "...";
    }
}
