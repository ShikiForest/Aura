using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AuraLang.CodeGen;

/// <summary>
/// Emits runtime support types (AuraGlobal) into the output assembly using Mono.Cecil.
/// These types provide handle management, object registry, and builder registration for the Aura runtime.
/// </summary>
public static class AuraRuntimeEmitter
{
    public static void Emit(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        // Don't override user-defined Global
        if (userTypes.ContainsKey("Global"))
            return;

        var td = new TypeDefinition("", "AuraGlobal",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        // --- Fields ---
        // private static Dictionary<long, object> _registry
        var dictType = module.ImportReference(typeof(Dictionary<long, object>));
        var registryField = new FieldDefinition("_registry",
            FieldAttributes.Private | FieldAttributes.Static,
            dictType);
        td.Fields.Add(registryField);

        // private static long _nextHandle
        var nextHandleField = new FieldDefinition("_nextHandle",
            FieldAttributes.Private | FieldAttributes.Static,
            module.TypeSystem.Int64);
        td.Fields.Add(nextHandleField);

        // private static Dictionary<string, object> _builders (for [BuildMe])
        var builderDictType = module.ImportReference(typeof(Dictionary<string, object>));
        var buildersField = new FieldDefinition("_builders",
            FieldAttributes.Private | FieldAttributes.Static,
            builderDictType);
        td.Fields.Add(buildersField);

        // --- Static constructor (.cctor) to initialize fields ---
        var cctor = new MethodDefinition(".cctor",
            MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Static,
            module.TypeSystem.Void);
        td.Methods.Add(cctor);
        {
            var il = cctor.Body.GetILProcessor();
            // _registry = new Dictionary<long, object>()
            var dictCtor = module.ImportReference(typeof(Dictionary<long, object>).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Newobj, dictCtor));
            il.Append(il.Create(OpCodes.Stsfld, registryField));
            // _nextHandle = 0
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Conv_I8));
            il.Append(il.Create(OpCodes.Stsfld, nextHandleField));
            // _builders = new Dictionary<string, object>()
            var builderDictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Newobj, builderDictCtor));
            il.Append(il.Create(OpCodes.Stsfld, buildersField));
            il.Append(il.Create(OpCodes.Ret));
        }

        // --- RegisterObject(object obj) -> long ---
        var registerObj = new MethodDefinition("RegisterObject",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Int64);
        registerObj.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, module.TypeSystem.Object));
        td.Methods.Add(registerObj);
        {
            var il = registerObj.Body.GetILProcessor();
            // long handle = _nextHandle; _nextHandle++; _registry[handle] = obj; return handle;
            registerObj.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int64));
            registerObj.Body.InitLocals = true;
            il.Append(il.Create(OpCodes.Ldsfld, nextHandleField));
            il.Append(il.Create(OpCodes.Stloc_0));
            // _nextHandle = _nextHandle + 1
            il.Append(il.Create(OpCodes.Ldsfld, nextHandleField));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Conv_I8));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stsfld, nextHandleField));
            // _registry[handle] = obj
            var dictSetItem = module.ImportReference(typeof(Dictionary<long, object>).GetMethod("set_Item", new[] { typeof(long), typeof(object) })!);
            il.Append(il.Create(OpCodes.Ldsfld, registryField));
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, dictSetItem));
            // return handle
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ret));
        }

        // --- FindObject(long handle) -> object ---
        var findObj = new MethodDefinition("FindObject",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Object);
        findObj.Parameters.Add(new ParameterDefinition("handle", ParameterAttributes.None, module.TypeSystem.Int64));
        td.Methods.Add(findObj);
        {
            var il = findObj.Body.GetILProcessor();
            // return _registry[handle]
            var dictGetItem = module.ImportReference(typeof(Dictionary<long, object>).GetMethod("get_Item", new[] { typeof(long) })!);
            il.Append(il.Create(OpCodes.Ldsfld, registryField));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, dictGetItem));
            il.Append(il.Create(OpCodes.Ret));
        }

        // --- FindObject<T>(long handle) -> T (generic version with cast) ---
        var findObjGeneric = new MethodDefinition("FindObject",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Object); // return type will be replaced by T
        var genParamT = new GenericParameter("T", findObjGeneric);
        findObjGeneric.GenericParameters.Add(genParamT);
        findObjGeneric.ReturnType = genParamT;
        findObjGeneric.Parameters.Add(new ParameterDefinition("handle", ParameterAttributes.None, module.TypeSystem.Int64));
        td.Methods.Add(findObjGeneric);
        {
            var il = findObjGeneric.Body.GetILProcessor();
            // return (T)_registry[handle]
            var dictGetItem = module.ImportReference(typeof(Dictionary<long, object>).GetMethod("get_Item", new[] { typeof(long) })!);
            il.Append(il.Create(OpCodes.Ldsfld, registryField));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, dictGetItem));
            il.Append(il.Create(OpCodes.Unbox_Any, genParamT));
            il.Append(il.Create(OpCodes.Ret));
        }

        // --- RemoveObject(long handle) -> void ---
        var removeObj = new MethodDefinition("RemoveObject",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        removeObj.Parameters.Add(new ParameterDefinition("handle", ParameterAttributes.None, module.TypeSystem.Int64));
        td.Methods.Add(removeObj);
        {
            var il = removeObj.Body.GetILProcessor();
            // _registry.Remove(handle)
            var dictRemove = module.ImportReference(typeof(Dictionary<long, object>).GetMethod("Remove", new[] { typeof(long) })!);
            il.Append(il.Create(OpCodes.Ldsfld, registryField));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, dictRemove));
            il.Append(il.Create(OpCodes.Pop)); // Remove returns bool; discard it
            il.Append(il.Create(OpCodes.Ret));
        }

        // --- RegisterBuilder(Type type, Type builderType, string name) -> void ---
        var registerBuilder = new MethodDefinition("RegisterBuilder",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        registerBuilder.Parameters.Add(new ParameterDefinition("type", ParameterAttributes.None, module.ImportReference(typeof(Type))));
        registerBuilder.Parameters.Add(new ParameterDefinition("builderType", ParameterAttributes.None, module.ImportReference(typeof(Type))));
        registerBuilder.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
        td.Methods.Add(registerBuilder);
        {
            var il = registerBuilder.Body.GetILProcessor();
            // _builders[name] = builderType (stores as object)
            var dictSetItem = module.ImportReference(typeof(Dictionary<string, object>).GetMethod("set_Item", new[] { typeof(string), typeof(object) })!);
            il.Append(il.Create(OpCodes.Ldsfld, buildersField));
            il.Append(il.Create(OpCodes.Ldarg_2)); // name
            il.Append(il.Create(OpCodes.Ldarg_1)); // builderType (as object)
            il.Append(il.Create(OpCodes.Callvirt, dictSetItem));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["Global"] = td;
    }

    /// <summary>
    /// Emits the IRoomReceiver interface that Room participants must implement.
    /// interface IRoomReceiver { void OnMessage(string message, object args); }
    /// </summary>
    public static void EmitIRoomReceiver(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("IRoomReceiver"))
            return;

        var iface = new TypeDefinition("", "IRoomReceiver",
            TypeAttributes.NestedPublic | TypeAttributes.Interface | TypeAttributes.Abstract,
            module.TypeSystem.Object);

        // void OnMessage(string message, object args)
        var onMessage = new MethodDefinition("OnMessage",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.Virtual,
            module.TypeSystem.Void);
        onMessage.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, module.TypeSystem.String));
        onMessage.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, module.TypeSystem.Object));
        iface.Methods.Add(onMessage);

        auraModule.NestedTypes.Add(iface);
        userTypes["IRoomReceiver"] = iface;
    }

    // ── IBuilder<T> interface ───────────────────────────────────────────────

    /// <summary>
    /// Emits the IBuilder&lt;T&gt; interface into the output module.
    /// <code>
    /// interface IBuilder`1&lt;T&gt; {
    ///     Dictionary&lt;string, object&gt; GetConstructorDictionary();
    ///     T Build(Dictionary&lt;string, object&gt; args);
    /// }
    /// </code>
    /// </summary>
    public static void EmitIBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("IBuilder"))
            return;

        var iface = new TypeDefinition("", "IBuilder`1",
            TypeAttributes.NestedPublic | TypeAttributes.Interface | TypeAttributes.Abstract,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", iface);
        iface.GenericParameters.Add(genT);

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));

        // Dictionary<string, object> GetConstructorDictionary()
        var getCtorDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.Virtual,
            dictType);
        iface.Methods.Add(getCtorDict);

        // T Build(Dictionary<string, object> args)
        var build = new MethodDefinition("Build",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.Virtual,
            genT);
        build.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, dictType));
        iface.Methods.Add(build);

        auraModule.NestedTypes.Add(iface);
        userTypes["IBuilder"] = iface;
    }

    // ── CLRExternalTypeBuilder<T> class ─────────────────────────────────────

    /// <summary>
    /// Emits CLRExternalTypeBuilder&lt;T&gt; : IBuilder&lt;T&gt; into the output module.
    /// Uses reflection (Activator.CreateInstance) to construct CLR objects.
    /// </summary>
    public static void EmitCLRExternalTypeBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("CLRExternalTypeBuilder"))
            return;

        // Ensure IBuilder exists
        if (!userTypes.TryGetValue("IBuilder", out var ibuilderTd))
            return;

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);
        var objectType = module.TypeSystem.Object;

        var td = new TypeDefinition("", "CLRExternalTypeBuilder`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            objectType);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Implement IBuilder<T>
        var ibuilderRef = new GenericInstanceType(ibuilderTd) { GenericArguments = { genT } };
        td.Interfaces.Add(new InterfaceImplementation(ibuilderRef));

        // .ctor()
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        td.Methods.Add(ctor);
        {
            var il = ctor.Body.GetILProcessor();
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, objCtor));
            il.Append(il.Create(OpCodes.Ret));
        }

        // Dictionary<string, object> GetConstructorDictionary() => new Dictionary<string, object>()
        var getDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            dictType);
        td.Methods.Add(getDict);
        {
            var il = getDict.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Newobj, dictCtor));
            il.Append(il.Create(OpCodes.Ret));
        }

        // T Build(Dictionary<string, object> args)
        var build = new MethodDefinition("Build",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            genT);
        build.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, dictType));
        td.Methods.Add(build);
        {
            // return Activator.CreateInstance<T>();
            var il = build.Body.GetILProcessor();
            var activatorCreateInstance = module.ImportReference(
                typeof(Activator).GetMethod("CreateInstance", Type.EmptyTypes)!
                    .MakeGenericMethod(typeof(object)));

            // Use the non-generic Activator.CreateInstance(typeof(T))
            // ldtoken T; call Type.GetTypeFromHandle; call Activator.CreateInstance(Type)
            var getTypeFromHandle = module.ImportReference(
                typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) })!);
            var createInstance = module.ImportReference(
                typeof(Activator).GetMethod("CreateInstance", new[] { typeof(Type) })!);

            il.Append(il.Create(OpCodes.Ldtoken, genT));
            il.Append(il.Create(OpCodes.Call, getTypeFromHandle));
            il.Append(il.Create(OpCodes.Call, createInstance));
            il.Append(il.Create(OpCodes.Unbox_Any, genT));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["CLRExternalTypeBuilder"] = td;
    }
}
