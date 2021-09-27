using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using dnlib.DotNet;

namespace HotSwap
{
    public static class MethodTranslator
    {
        public static void TranslateLocals(CilBody methodBody, ILGenerator ilGen)
        {
            foreach (var local in methodBody.Variables)
            {
                ilGen.DeclareLocal((Type)Translator.TranslateRef(local.Type));
            }
        }

        public static void TranslateRefs(CilBody methodBody, byte[] newCode, DynamicMethod replacement, Stopwatch[] watches)
        {
            int pos = 0;

            foreach (var inst in methodBody.Instructions)
            {
                switch (inst.OpCode.OperandType)
                {
                    case dnlib.DotNet.Emit.OperandType.InlineString:
                    case dnlib.DotNet.Emit.OperandType.InlineType:
                    case dnlib.DotNet.Emit.OperandType.InlineMethod:
                    case dnlib.DotNet.Emit.OperandType.InlineField:
                    case dnlib.DotNet.Emit.OperandType.InlineSig:
                    case dnlib.DotNet.Emit.OperandType.InlineTok:
                        pos += inst.OpCode.Size;

                        var watch = inst.Operand switch
                        {
                            IType => 6,
                            IMethod => 7,
                            _ => 8
                        };

                        watches[watch].Start();
                        object @ref = Translator.TranslateRef(inst.Operand);
                        watches[watch].Stop();

                        if (@ref == null)
                            throw new NullReferenceException($"Null translation {inst.Operand} {inst.Operand.GetType()}");

                        int token = replacement.AddRef(@ref);
                        newCode[pos++] = (byte)(token & 255);
                        newCode[pos++] = (byte)(token >> 8 & 255);
                        newCode[pos++] = (byte)(token >> 16 & 255);
                        newCode[pos++] = (byte)(token >> 24 & 255);

                        break;
                    default:
                        pos += inst.GetSize();
                        break;
                }
            }
        }

        public static void TranslateExceptions(CilBody methodBody, ILGenerator ilGen)
        {
            var dnhandlers = methodBody.ExceptionHandlers.Reverse().ToArray();
            var exinfos = ilGen.ex_handlers = new ILExceptionInfo[dnhandlers.Length];

            for (int i = 0; i < dnhandlers.Length; i++)
            {
                var ex = dnhandlers[i];
                int start = (int)ex.TryStart.Offset;
                int end = (int)ex.TryEnd.Offset;
                int len = end - start;

                exinfos[i].start = start;
                exinfos[i].len = len;

                int handlerStart = (int)ex.HandlerStart.Offset;
                int handlerEnd = (int)ex.HandlerEnd.Offset;
                int handlerLen = handlerEnd - handlerStart;

                Type catchType = null;
                int filterOffset = 0;

                if (ex.CatchType != null)
                    catchType = (Type)Translator.TranslateRef(ex.CatchType);
                else if (ex.FilterStart != null)
                    filterOffset = (int)ex.FilterStart.Offset;

                exinfos[i].handlers = new ILExceptionBlock[]
                {
                    new ILExceptionBlock()
                    {
                        extype = catchType,
                        type = (int)ex.HandlerType,
                        start = handlerStart,
                        len = handlerLen,
                        filter_offset = filterOffset
                    }
                };
            }
        }
    }
}
