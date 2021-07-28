using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace HotSwap
{
    public static class OldHarmony
    {
        /* Methods below are copied from old Harmony */

        public static DynamicMethod CreateDynamicMethod(MethodBase original, string suffix)
        {
            if (original == null) throw new ArgumentNullException("original cannot be null");
            var patchName = original.Name + suffix;
            patchName = patchName.Replace("<>", "");

            var parameters = original.GetParameters();
            var result = parameters.Types().ToList();
            if (original.IsStatic == false)
                result.Insert(0, typeof(object));
            var paramTypes = result.ToArray();
            var returnType = AccessTools.GetReturnedType(original);

            // DynamicMethod does not support byref return types
            if (returnType == null || returnType.IsByRef)
                return null;

            DynamicMethod method;
            try
            {
                method = new DynamicMethod(
                patchName,
                MethodAttributes.Public | MethodAttributes.Static,
                CallingConventions.Standard,
                returnType,
                paramTypes,
                original.DeclaringType,
                true
            );
            }
            catch (Exception)
            {
                return null;
            }

            for (var i = 0; i < parameters.Length; i++)
                method.DefineParameter(i + 1, parameters[i].Attributes, parameters[i].Name);

            return method;
        }

        public static void PrepareDynamicMethod(DynamicMethod method)
        {
            var nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var nonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

            // on mono, just call 'CreateDynMethod'
            //
            var m_CreateDynMethod = typeof(DynamicMethod).GetMethod("CreateDynMethod", nonPublicInstance);
            if (m_CreateDynMethod != null)
            {
                m_CreateDynMethod.Invoke(method, new object[0]);
                return;
            }

            // on all .NET Core versions, call 'RuntimeHelpers._CompileMethod' but with a different parameter:
            //
            var m__CompileMethod = typeof(RuntimeHelpers).GetMethod("_CompileMethod", nonPublicStatic);

            var m_GetMethodDescriptor = typeof(DynamicMethod).GetMethod("GetMethodDescriptor", nonPublicInstance);
            var handle = (RuntimeMethodHandle)m_GetMethodDescriptor.Invoke(method, new object[0]);

            // 1) RuntimeHelpers._CompileMethod(handle.GetMethodInfo())
            //
            var m_GetMethodInfo = typeof(RuntimeMethodHandle).GetMethod("GetMethodInfo", nonPublicInstance);
            if (m_GetMethodInfo != null)
            {
                var runtimeMethodInfo = m_GetMethodInfo.Invoke(handle, new object[0]);
                try
                {
                    // this can throw BadImageFormatException "An attempt was made to load a program with an incorrect format"
                    m__CompileMethod.Invoke(null, new object[] { runtimeMethodInfo });
                    return;
                }
                catch (Exception)
                {
                }
            }

            // 2) RuntimeHelpers._CompileMethod(handle.Value)
            //
            if (m__CompileMethod.GetParameters()[0].ParameterType.IsAssignableFrom(handle.Value.GetType()))
            {
                m__CompileMethod.Invoke(null, new object[] { handle.Value });
                return;
            }

            // 3) RuntimeHelpers._CompileMethod(handle)
            //
            if (m__CompileMethod.GetParameters()[0].ParameterType.IsAssignableFrom(handle.GetType()))
            {
                m__CompileMethod.Invoke(null, new object[] { handle });
                return;
            }
        }
    }
}
