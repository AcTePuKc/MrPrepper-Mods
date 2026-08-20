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
    public const string PluginName = "Mr. Prepper Dialogue.Start IL Inspector";
    public const string PluginVersion = "0.1.0";

    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static DialogueStartIlInspector()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!(field.GetValue(null) is OpCode op))
            {
                continue;
            }

            var value = unchecked((ushort)op.Value);
            if (value < 0x100)
            {
                OneByteOpCodes[value] = op;
            }
            else if ((value & 0xff00) == 0xfe00)
            {
                TwoByteOpCodes[value & 0xff] = op;
            }
        }
    }

    private void Awake()
    {
        InspectDialogueStart();
    }

    private void InspectDialogueStart()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal));
        if (assembly == null)
        {
            Logger.LogWarning("[DIALOGUE IL] Assembly-CSharp was not found.");
            return;
        }

        var type = assembly.GetType("Characters.Dialogue", false);
        if (type == null)
        {
            Logger.LogWarning("[DIALOGUE IL] Characters.Dialogue was not found.");
            return;
        }

        var method = type.GetMethod(
            "Start",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null,
            Type.EmptyTypes,
            null);

        if (method == null)
        {
            Logger.LogWarning("[DIALOGUE IL] Characters.Dialogue.Start() was not found.");
            return;
        }

        MethodBody body;
        try
        {
            body = method.GetMethodBody();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[DIALOGUE IL] Could not read method body: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var il = body?.GetILAsByteArray();
        if (il == null || il.Length == 0)
        {
            Logger.LogWarning("[DIALOGUE IL] Characters.Dialogue.Start() has no managed IL body.");
            return;
        }

        var calls = new List<string>();
        var fields = new List<string>();
        var strings = new List<string>();
        var module = method.Module;
        var typeArgs = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. target=Characters.Dialogue.Start() ilBytes={il.Length}");

        var position = 0;
        while (position < il.Length)
        {
            var offset = position;
            var op = ReadOpCode(il, ref position);
            if (op.Size == 0)
            {
                Logger.LogWarning($"[DIALOGUE IL] Unknown opcode at IL_{offset:X4}; stopping scan.");
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
                        var description = target != null ? DescribeMethod(target) : $"token=0x{token:X8}";
                        calls.Add($"IL_{offset:X4} {op.Name} {description}");
                        break;
                    }
                    case OperandType.InlineField:
                    {
                        var token = ReadInt32(il, ref position);
                        FieldInfo field = null;
                        try { field = module.ResolveField(token, typeArgs, methodArgs); } catch { }
                        var description = field != null ? DescribeField(field) : $"token=0x{token:X8}";
                        fields.Add($"IL_{offset:X4} {op.Name} {description}");
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
                Logger.LogWarning($"[DIALOGUE IL] Parse error at IL_{offset:X4} opcode={op.Name}: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        Logger.LogInfo($"[DIALOGUE IL SUMMARY] directCalls={calls.Count} fieldAccesses={fields.Count} strings={strings.Count}");
        foreach (var entry in calls)
        {
            Logger.LogInfo($"[DIALOGUE IL CALL] {entry}");
        }
        foreach (var entry in fields)
        {
            Logger.LogInfo($"[DIALOGUE IL FIELD] {entry}");
        }
        foreach (var entry in strings)
        {
            Logger.LogInfo($"[DIALOGUE IL STRING] {entry}");
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        var first = il[position++];
        if (first != 0xfe)
        {
            return OneByteOpCodes[first];
        }
        if (position >= il.Length)
        {
            return default;
        }
        return TwoByteOpCodes[il[position++]];
    }

    private static void SkipOperand(OperandType operandType, byte[] il, ref int position)
    {
        switch (operandType)
        {
            case OperandType.InlineNone:
                return;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                position += 1;
                return;
            case OperandType.InlineVar:
                position += 2;
                return;
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                position += 4;
                return;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                position += 8;
                return;
            case OperandType.InlineSwitch:
            {
                var count = ReadInt32(il, ref position);
                position += count * 4;
                return;
            }
            default:
                throw new NotSupportedException("Unsupported operand type: " + operandType);
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
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "<null>";
        }
        value = value.Replace("\r", "\\r").Replace("\n", "\\n");
        return value.Length <= max ? value : value.Substring(0, max) + "...";
    }
}
