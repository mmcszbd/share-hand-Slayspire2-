using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Godot;

namespace SharedHandPrototype;

// Only used while discovering optional compatibility patches. Never wrap
// gameplay callbacks: a broken optional method must not hide a gameplay fault.
internal static class CompatibilityScan
{
    private static readonly HashSet<string> Failures = new();
    private static readonly List<object> Details = new();
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);
    internal static int Skipped => Failures.Count;
    internal static object Snapshot() => new { skipped = Skipped, details = Details.ToArray(), detailLimit = 50 };

    internal static void Attempt(string phase, string target, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Report(phase, target, ex);
        }
    }
    private static void Report(string phase, string target, Exception error)
    {
        var cause = error.GetBaseException();
        if (!Failures.Add(phase + ":" + target)) return;
        // Bounded persistent diagnostics survive the recent combat-event ring.
        // Do not include exception messages, which can contain filesystem paths.
        if (Details.Count < 50) Details.Add(new { phase, target, reason = cause.GetType().FullName });
        if (Skipped <= 20)
            GD.Print("[SharedHandPrototype] compatibility skip: " + phase + " " + target + " (" + cause.GetType().Name + ")");
        if (Skipped == 21) GD.Print("[SharedHandPrototype] further compatibility warnings suppressed; see diagnostic export.");
    }
    internal static string Name(MemberInfo member) => member.Module.Assembly.GetName().Name + ":" +
        (member is Type type ? type.FullName : member.DeclaringType?.FullName + "." + member.Name) + "#" + member.MetadataToken;
    internal static Type[] Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            Report("types", assembly.GetName().Name!, ex);
            return ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
    }

    // Decode actual instruction boundaries. Operand bytes (including switch
    // tables) can contain 0x28/0x6f without being call instructions.
    internal static bool CallsAny(MethodInfo method, ICollection<MethodBase> targets)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;
        for (int p = 0; p < il.Length;)
        {
            short value = il[p++];
            if (value == 0xfe)
            {
                if (p >= il.Length) throw new BadImageFormatException();
                value = unchecked((short)(0xfe00 | il[p++]));
            }
            if (!Opcodes.TryGetValue(value, out var opcode)) throw new BadImageFormatException();
            int size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => p + 4 <= il.Length ? checked(4 + 4 * BitConverter.ToInt32(il, p)) : -1,
                _ => 4
            };
            if (size < 0 || size > il.Length - p) throw new BadImageFormatException();
            if (opcode == OpCodes.Call || opcode == OpCodes.Callvirt)
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, p),
                    method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments());
                if (called != null && targets.Contains(called)) return true;
            }
            p += size;
        }
        return false;
    }
}

