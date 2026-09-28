using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;

namespace SingPlus.Admission;

internal readonly record struct IlInstruction(int Offset, OpCode OpCode, int? MetadataToken);

internal static class IlReader
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static f => f.FieldType == typeof(OpCode))
        .Select(static f => (OpCode)f.GetValue(null)!)
        .ToDictionary(static op => unchecked((ushort)op.Value));

    public static IEnumerable<IlInstruction> Read(byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var instructionOffset = offset;
            ushort value = il[offset++];
            if (value == 0xFE)
            {
                if (offset >= il.Length) yield break;
                value = (ushort)(0xFE00 | il[offset++]);
            }

            if (!OpCodesByValue.TryGetValue(value, out var opCode)) yield break;
            int? token = null;
            var operandSize = OperandSize(opCode.OperandType, il, offset);
            if (operandSize < 0 || offset + operandSize > il.Length) yield break;
            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineField or OperandType.InlineString or OperandType.InlineSig)
                token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
            offset += operandSize;
            yield return new IlInstruction(instructionOffset, opCode, token);
        }
    }

    private static int OperandSize(OperandType operandType, byte[] il, int offset) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => SwitchSize(il, offset),
        _ => -1
    };

    private static int SwitchSize(byte[] il, int offset)
    {
        if (offset + 4 > il.Length) return -1;
        var count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
        if (count < 0 || count > (il.Length - offset - 4) / 4) return -1;
        return 4 + (count * 4);
    }
}
