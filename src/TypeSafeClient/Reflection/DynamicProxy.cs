﻿namespace TypeSafeClient.Reflection
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;

    /// <summary>
    /// Creates run-time backing types for abstract classes and interfaces
    /// </summary>
    public static class DynamicProxy
    {
        private static readonly AssemblyBuilder DynamicAssembly;
        private static readonly ModuleBuilder ModuleBuilder;

        static DynamicProxy()
        {
            var assemblyName = new AssemblyName("DynImpl");
            DynamicAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                assemblyName, AssemblyBuilderAccess.RunAndSave);
            ModuleBuilder = DynamicAssembly.DefineDynamicModule("DynImplModule");
        }

        /// <summary>
        /// Get an empty instance of a dynamic proxy for type T.
        /// All public fields are writable and all properties have both getters and setters.
        /// </summary>
        public static T GetInstanceFor<T>()
        {
            return (T)GetInstanceFor(typeof(T));
        }

        /// <summary>
        /// Get an empty instance of a dynamic proxy for the given type.
        /// All public fields are writable and all properties have both getters and setters.
        /// </summary>
        private static object GetInstanceFor(Type targetType)
        {
            lock (DynamicAssembly)
            {
                var constructedType = DynamicAssembly.GetType(ProxyName(targetType)) ?? GetConstructedType(targetType);
                var instance = Activator.CreateInstance(constructedType);
                return instance;
            }
        }

        private static string ProxyName(Type targetType)
        {
            return targetType.Name + "Proxy";
        }

        private static Type GetConstructedType(Type targetType)
        {
            var typeBuilder = ModuleBuilder.DefineType(targetType.Name + "Proxy", TypeAttributes.Public);

            var ctorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard, new Type[] { });
            var ilGenerator = ctorBuilder.GetILGenerator();
            ilGenerator.Emit(OpCodes.Ret);

            IncludeType(targetType, typeBuilder);

            foreach (var face in targetType.GetInterfaces())
            {
                IncludeType(face, typeBuilder);
            }

            return typeBuilder.CreateType();
        }

        private static void IncludeType(Type typeOfT, TypeBuilder typeBuilder)
        {
            var methodInfos = typeOfT.GetMethods();
            foreach (var methodInfo in methodInfos)
            {
                if (methodInfo.Name.StartsWith("set_"))
                {
                    continue; // we always add a set for a get. Set-only properties not supported.
                }

                if (methodInfo.Name.StartsWith("get_"))
                {
                    BindProperty(typeBuilder, methodInfo);
                }
                else
                {
                    BindMethod(typeBuilder, methodInfo);
                }
            }

            typeBuilder.AddInterfaceImplementation(typeOfT);
        }

        private static void BindMethod(TypeBuilder typeBuilder, MethodInfo methodInfo)
        {
            var methodBuilder = typeBuilder.DefineMethod(
                methodInfo.Name,
                MethodAttributes.Public | MethodAttributes.Virtual,
                methodInfo.ReturnType,
                methodInfo.GetParameters().Select(p => p.GetType()).ToArray());
            var methodILGen = methodBuilder.GetILGenerator();
            if (methodInfo.ReturnType == typeof(void))
            {
                methodILGen.Emit(OpCodes.Ret);
            }
            else
            {
                if (methodInfo.ReturnType.IsValueType || methodInfo.ReturnType.IsEnum)
                {
                    var getMethod = typeof(Activator).GetMethod("CreateInstance", new[] { typeof(Type) });
                    var lb = methodILGen.DeclareLocal(methodInfo.ReturnType);
                    methodILGen.Emit(OpCodes.Ldtoken, lb.LocalType);
                    methodILGen.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
                    methodILGen.Emit(OpCodes.Callvirt, getMethod);
                    methodILGen.Emit(OpCodes.Unbox_Any, lb.LocalType);
                }
                else
                {
                    methodILGen.Emit(OpCodes.Ldnull);
                }
                methodILGen.Emit(OpCodes.Ret);
            }
            typeBuilder.DefineMethodOverride(methodBuilder, methodInfo);
        }

        /// <summary>
        /// Bind a new property into a type builder with getters and setters.
        /// </summary>
        public static void BindProperty(TypeBuilder typeBuilder, MethodInfo methodInfo)
        {
            // Backing Field
            var propertyName = methodInfo.Name.Replace("get_", "");
            var propertyType = methodInfo.ReturnType;
            var backingField = typeBuilder.DefineField(
                "_" + propertyName, propertyType, FieldAttributes.Private);

            //Getter
            var backingGet = typeBuilder.DefineMethod(
                "get_" + propertyName,
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.Virtual
                | MethodAttributes.HideBySig,
                propertyType,
                Type.EmptyTypes);
            var getIl = backingGet.GetILGenerator();

            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, backingField);
            getIl.Emit(OpCodes.Ret);

            //Setter
            var backingSet = typeBuilder.DefineMethod(
                "set_" + propertyName,
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.Virtual
                | MethodAttributes.HideBySig,
                null,
                new[] { propertyType });

            var setIl = backingSet.GetILGenerator();

            setIl.Emit(OpCodes.Ldarg_0);
            setIl.Emit(OpCodes.Ldarg_1);
            setIl.Emit(OpCodes.Stfld, backingField);
            setIl.Emit(OpCodes.Ret);

            // Property
            var propertyBuilder = typeBuilder.DefineProperty(
                propertyName, PropertyAttributes.None, propertyType, null);
            propertyBuilder.SetGetMethod(backingGet);
            propertyBuilder.SetSetMethod(backingSet);
        }
    }
}