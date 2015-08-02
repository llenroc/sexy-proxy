﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace SexyProxy.Fody
{
    public static class CecilExtensions
    {
        public static ModuleDefinition ModuleDefinition { get; set; }

        // Will log an MessageImportance.High message to MSBuild. OPTIONAL
        public static Action<string> LogInfo { get; set; }

        // Will log an error message to MSBuild. OPTIONAL
        public static Action<string> LogError { get; set; }

        public static Action<string> LogWarning { get; set; }

        private static TypeReference typeType;
        private static TypeReference taskType;
        private static MethodReference getTypeFromRuntimeHandleMethod;
        private static MethodReference typeGetMethod;
        private static TypeReference taskTType;
        private static MethodReference taskFromResult;

        internal static void Initialize(ModuleDefinition moduleDefinition)
        {
            ModuleDefinition = moduleDefinition;
            typeType = ModuleDefinition.Import(typeof(Type));
            taskType = ModuleDefinition.Import(typeof(Task));
            getTypeFromRuntimeHandleMethod = ModuleDefinition.Import(typeType.Resolve().Methods.Single(x => x.Name == "GetTypeFromHandle"));
            typeGetMethod = ModuleDefinition.Import(typeType.Resolve().Methods.Single(x => x.Name == "GetMethod" && x.Parameters.Count == 5));
            taskTType = ModuleDefinition.Import(typeof(Task<>));
            taskFromResult = ModuleDefinition.Import(taskType.Resolve().Methods.Single(x => x.Name == "FromResult"));
        }

        public static AssemblyNameReference FindAssembly(this ModuleDefinition module, string name)
        {
            return module.AssemblyReferences
                .Where(x => x.Name == name)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
        }

        public static void Emit(this MethodBody body, Action<ILProcessor> il)
        {
            il(body.GetILProcessor());
        }

        public static GenericInstanceMethod MakeGenericMethod(this MethodReference method, params TypeReference[] genericArguments)
        {
            var result = new GenericInstanceMethod(method);
            foreach (var argument in genericArguments)
                result.GenericArguments.Add(argument);
            return result;
        }

        public static bool IsAssignableFrom(this TypeReference baseType, TypeReference type, Action<string> logger = null)
        {
            return baseType.Resolve().IsAssignableFrom(type.Resolve(), logger);
        }

        public static bool IsAssignableFrom(this TypeDefinition baseType, TypeDefinition type, Action<string> logger = null)
        {
            logger = logger ?? (x => {});

            Queue<TypeDefinition> queue = new Queue<TypeDefinition>();
            queue.Enqueue(type);

            while (queue.Any())
            {
                var current = queue.Dequeue();
                logger(current.FullName);

                if (baseType.FullName == current.FullName)
                    return true;

                if (current.BaseType != null)
                    queue.Enqueue(current.BaseType.Resolve());

                foreach (var @interface in current.Interfaces)
                {
                    queue.Enqueue(@interface.Resolve());
                }
            }

            return false;
        }

        public static TypeDefinition GetEarliestAncestorThatDeclares(this TypeDefinition type, TypeReference attributeType)
        {
            var current = type;
            TypeDefinition result = null;
            while (current != null)
            {
                if (current.IsDefined(attributeType))
                {
                    result = current;
                }
                current = current.BaseType?.Resolve();
            }
            return result;
        }

        public static bool IsDefined(this IMemberDefinition member, TypeReference attributeType, bool inherit = false)
        {
            var typeIsDefined = member.HasCustomAttributes && member.CustomAttributes.Any(x => x.AttributeType.FullName == attributeType.FullName);
            if (inherit && member.DeclaringType?.BaseType != null)
            {
                typeIsDefined = member.DeclaringType.BaseType.Resolve().IsDefined(attributeType, true);
            }
            return typeIsDefined;
        }

        public static MethodReference Bind(this MethodReference method, GenericInstanceType genericType)
        {
            var reference = new MethodReference(method.Name, method.ReturnType, genericType);
            reference.HasThis = method.HasThis;
            reference.ExplicitThis = method.ExplicitThis;
            reference.CallingConvention = method.CallingConvention;

            foreach (var parameter in method.Parameters)
                reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));

            return reference;
        }
        /*
        public static MethodReference BindDefinition(this MethodReference method, TypeReference genericTypeDefinition)
        {
            if (!genericTypeDefinition.HasGenericParameters)
                return method;

            var genericDeclaration = new GenericInstanceType(genericTypeDefinition);
            foreach (var parameter in genericTypeDefinition.GenericParameters)
            {
                genericDeclaration.GenericArguments.Add(parameter);
            }
            var reference = new MethodReference(method.Name, method.ReturnType, genericDeclaration);
            reference.HasThis = method.HasThis;
            reference.ExplicitThis = method.ExplicitThis;
            reference.CallingConvention = method.CallingConvention;

            foreach (var parameter in method.Parameters)
                reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));

            return reference;
        }
        */
        public static FieldReference BindDefinition(this FieldReference field, TypeReference genericTypeDefinition)
        {
            if (!genericTypeDefinition.HasGenericParameters)
                return field;

            var genericDeclaration = new GenericInstanceType(genericTypeDefinition);
            foreach (var parameter in genericTypeDefinition.GenericParameters)
            {
                genericDeclaration.GenericArguments.Add(parameter);
            }
            var reference = new FieldReference(field.Name, field.FieldType, genericDeclaration);
            return reference;
        }

        public static TypeReference FindType(this ModuleDefinition currentModule, string @namespace, string typeName, IMetadataScope scope = null, params string[] typeParameters)
        {
            var result = new TypeReference(@namespace, typeName, currentModule, scope);
            foreach (var typeParameter in typeParameters)
            {
                result.GenericParameters.Add(new GenericParameter(typeParameter, result));
            }
            return result;
        }

        public static void EmitDefaultBaseConstructorCall(this ILProcessor il, TypeDefinition baseType)
        {
            TypeReference constructorType = baseType;
            MethodReference conObj = null;
            while (conObj == null)
            {
                constructorType = (constructorType == null ? baseType : constructorType.Resolve().BaseType) ?? ModuleDefinition.TypeSystem.Object;
                conObj = ModuleDefinition.Import(constructorType.Resolve().GetConstructors().Single(x => x.Parameters.Count == 0));
            }

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, conObj);
        }

        public static void LoadType(this ILProcessor il, TypeReference type)
        {
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Call, getTypeFromRuntimeHandleMethod);
        }

        public static void StoreMethodInfo(this ILProcessor il, FieldDefinition staticField, MethodDefinition method)
        {
            var parameterTypes = method.Parameters.Select(info => info.ParameterType).ToArray();

            // The type we want to invoke GetMethod upon
            il.LoadType(method.DeclaringType);

            // Arg1: methodName
            il.Emit(OpCodes.Ldstr, method.Name);

            // Arg2: bindingFlags
            il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));

            // Arg3: binder
            il.Emit(OpCodes.Ldnull);

            // Arg4: parameterTypes
            il.Emit(OpCodes.Ldc_I4, parameterTypes.Length);
            il.Emit(OpCodes.Newarr, typeType);
            // Copy array for each element we are going to set
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                il.Emit(OpCodes.Dup);
            }
            // Set each element 
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                il.Emit(OpCodes.Ldc_I4, i);
                il.LoadType(ModuleDefinition.Import(parameterTypes[i]));
                il.Emit(OpCodes.Stelem_Any, typeType);
            }

            // Arg5: parameterModifiers
            il.Emit(OpCodes.Ldnull);

            // Invoke method
            il.Emit(OpCodes.Call, typeGetMethod);

            // Store MethodInfo into the static field
            il.Emit(OpCodes.Stsfld, staticField);
        }

        public static bool IsTaskT(this TypeReference type)
        {
            var current = type;
            while (current != null)
            {
                if (current is GenericInstanceType && ((GenericInstanceType)current).Resolve().GetElementType().CompareTo(taskTType))
                    return true;
                current = current.Resolve().BaseType;
            }
            return false;
        }

        public static bool CompareTo(this TypeReference type, TypeReference compareTo)
        {
            return type.FullName == compareTo.FullName;
        }

        public static TypeReference GetTaskType(this TypeReference type)
        {
            var current = type;
            while (current != null)
            {
                if (current is GenericInstanceType && ((GenericInstanceType)current).Resolve().GetElementType().CompareTo(taskTType))
                    return ((GenericInstanceType)current).GenericArguments.Single();
                current = current.Resolve().BaseType;                
            }
            throw new Exception("Type " + type.FullName + " is not an instance of Task<T>");
        }

        public static void CreateDefaultMethodImplementation(MethodDefinition methodInfo, ILProcessor il)
        {
            if (taskType.IsAssignableFrom(methodInfo.ReturnType))
            {
                if (methodInfo.ReturnType.IsTaskT())
                {
                    var returnTaskType = methodInfo.ReturnType.GetTaskType();
                    il.EmitDefaultValue(returnTaskType.Resolve());
                    var fromResult = taskFromResult.MakeGenericMethod(returnTaskType);
                    il.Emit(OpCodes.Call, fromResult);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                    var fromResult = taskFromResult.MakeGenericMethod(ModuleDefinition.TypeSystem.Object);
                    il.Emit(OpCodes.Call, fromResult);
                }
            }
            else if (!methodInfo.ReturnType.CompareTo(ModuleDefinition.TypeSystem.Void))
            {
                il.EmitDefaultValue(methodInfo.ReturnType.Resolve());
            }

            // Return
            il.Emit(OpCodes.Ret);
        }

        public static void EmitDefaultValue(this ILProcessor il, TypeDefinition type)
        {
            if (type.CompareTo(ModuleDefinition.TypeSystem.Boolean) || type.CompareTo(ModuleDefinition.TypeSystem.Byte) ||
                type.CompareTo(ModuleDefinition.TypeSystem.Int16) || type.CompareTo(ModuleDefinition.TypeSystem.Int32))
            {
                il.Emit(OpCodes.Ldc_I4_0);
            }
            else if (type.CompareTo(ModuleDefinition.TypeSystem.Single))
            {
                il.Emit(OpCodes.Ldc_R4, (float)0);
            }
            else if (type.CompareTo(ModuleDefinition.TypeSystem.Int64))
            {
                il.Emit(OpCodes.Ldc_I8);
            }
            else if (type.CompareTo(ModuleDefinition.TypeSystem.Double))
            {
                il.Emit(OpCodes.Conv_R8);
            }
            else if (type.IsValueType)
            {
                var local = new VariableDefinition(type);
                il.Body.Variables.Add(local);
                il.Emit(OpCodes.Ldloca_S, local);
                il.Emit(OpCodes.Initobj, type);
                il.Emit(OpCodes.Ldloc, local);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }
        }


/*

        public static IEnumerable<TypeDefinition> GetAllTypes(this ModuleDefinition module)
        {
            var stack = new Stack<TypeDefinition>();
            foreach (var type in module.Types)
            {
                stack.Push(type);
            }
            while (stack.Any())
            {
                var current = stack.Pop();
                yield return current;

                foreach (var nestedType in current.NestedTypes)
                {
                    stack.Push(nestedType);
                }
            }
        }
*/
    }
}