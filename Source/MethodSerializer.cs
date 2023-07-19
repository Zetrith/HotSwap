using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace HotSwap
{
    public static class MethodSerializer
    {
        private class TokenProvider : ITokenProvider
        {
            public void Error(string message)
            {
            }

            public MDToken GetToken(object o)
            {
                if (o is string)
                    return new MDToken((Table)0x70, 1);
                else if (o is IMDTokenProvider token)
                    return token.MDToken;
                else if (o is StandAloneSig sig)
                    return sig.MDToken;

                return new MDToken();
            }

            public MDToken GetToken(IList<TypeSig> locals, uint origToken)
            {
                return new MDToken(origToken);
            }
        }

        static FieldInfo codeSizeField = AccessTools.Field(typeof(MethodBodyWriter), "codeSize");

        public static byte[] SerializeInstructions(CilBody body)
        {
            var writer = new MethodBodyWriter(new TokenProvider(), body);
            writer.Write();
            int codeSize = (int)(uint)codeSizeField.GetValue(writer);
            byte[] newCode = new byte[codeSize];
            Array.Copy(writer.Code, writer.Code.Length - codeSize, newCode, 0, codeSize);
            return newCode;
        }
    }
}
