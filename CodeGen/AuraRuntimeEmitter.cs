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

    // ── VoidBuilder ────────────────────────────────────────────────────────

    /// <summary>
    /// Emits VoidBuilder — the ONLY type that can be <c>new VoidBuilder()</c>.
    /// Acts as bootstrap for the builder chain. Implements IBuilder&lt;object&gt;.
    /// GetConstructorDictionary() → empty dict, Build() → null (sentinel).
    /// </summary>
    public static void EmitVoidBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("VoidBuilder"))
            return;

        if (!userTypes.TryGetValue("IBuilder", out var ibuilderTd))
            return;

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);

        var td = new TypeDefinition("", "VoidBuilder",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        // Implement IBuilder<object>
        var ibuilderObj = new GenericInstanceType(ibuilderTd) { GenericArguments = { module.TypeSystem.Object } };
        td.Interfaces.Add(new InterfaceImplementation(ibuilderObj));

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

        // Dictionary<string, object> GetConstructorDictionary() => new()
        var getDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            dictType);
        td.Methods.Add(getDict);
        {
            var il = getDict.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Newobj, dictCtor));
            il.Append(il.Create(OpCodes.Ret));
        }

        // object Build(Dictionary<string, object> args) => null (sentinel)
        var build = new MethodDefinition("Build",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            module.TypeSystem.Object);
        build.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, dictType));
        td.Methods.Add(build);
        {
            var il = build.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["VoidBuilder"] = td;
    }

    // ── CLRConstructorArgBuilder (abstract base) ────────────────────────────

    /// <summary>
    /// Emits CLRConstructorArgBuilder — abstract base class for building CLR constructor args.
    /// Designed for inheritance: subclasses define properties that map to constructor parameters.
    /// Has an Args: Dictionary&lt;string, object&gt; field.
    /// GetConstructorDictionary() is virtual (subclass overrides generated by compiler).
    /// </summary>
    public static void EmitCLRConstructorArgBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("CLRConstructorArgBuilder"))
            return;

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);

        var td = new TypeDefinition("", "CLRConstructorArgBuilder",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        // Field: private Dictionary<string, object> _args
        var argsField = new FieldDefinition("_args",
            FieldAttributes.Private,
            dictType);
        td.Fields.Add(argsField);

        // .ctor() — initializes _args = new Dictionary<string, object>()
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        td.Methods.Add(ctor);
        {
            var il = ctor.Body.GetILProcessor();
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, objCtor));
            // this._args = new Dictionary<string, object>()
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Newobj, dictCtor));
            il.Append(il.Create(OpCodes.Stfld, argsField));
            il.Append(il.Create(OpCodes.Ret));
        }

        // Property: public Dictionary<string, object> Args { get => _args; }
        var argsProp = new PropertyDefinition("Args", PropertyAttributes.None, dictType);
        var argsGetter = new MethodDefinition("get_Args",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            dictType);
        td.Methods.Add(argsGetter);
        {
            var il = argsGetter.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, argsField));
            il.Append(il.Create(OpCodes.Ret));
        }
        argsProp.GetMethod = argsGetter;
        td.Properties.Add(argsProp);

        // Method: public void AddArg(string key, object value)
        // Helper to populate Args from property setters
        var addArg = new MethodDefinition("AddArg",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        addArg.Parameters.Add(new ParameterDefinition("key", ParameterAttributes.None, module.TypeSystem.String));
        addArg.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Object));
        td.Methods.Add(addArg);
        {
            var il = addArg.Body.GetILProcessor();
            // _args[key] = value
            var dictSetItem = module.ImportReference(
                typeof(Dictionary<string, object>).GetMethod("set_Item", new[] { typeof(string), typeof(object) })!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, argsField));
            il.Append(il.Create(OpCodes.Ldarg_1)); // key
            il.Append(il.Create(OpCodes.Ldarg_2)); // value
            il.Append(il.Create(OpCodes.Callvirt, dictSetItem));
            il.Append(il.Create(OpCodes.Ret));
        }

        // Method: public virtual Dictionary<string, object> GetConstructorDictionary()
        // Base impl returns _args as-is. Subclasses can override.
        var getDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            dictType);
        td.Methods.Add(getDict);
        {
            var il = getDict.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, argsField));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["CLRConstructorArgBuilder"] = td;
    }

    // ── CLRExternalTypeBuilder<T> class ─────────────────────────────────────

    /// <summary>
    /// Emits CLRExternalTypeBuilder&lt;T&gt; : IBuilder&lt;T&gt; into the output module.
    /// Accepts optional CLRConstructorArgBuilder via _argBuilder field.
    /// Uses Activator.CreateInstance to construct CLR objects.
    /// </summary>
    public static void EmitCLRExternalTypeBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("CLRExternalTypeBuilder"))
            return;

        if (!userTypes.TryGetValue("IBuilder", out var ibuilderTd))
            return;

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);

        var td = new TypeDefinition("", "CLRExternalTypeBuilder`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Implement IBuilder<T>
        var ibuilderRef = new GenericInstanceType(ibuilderTd) { GenericArguments = { genT } };
        td.Interfaces.Add(new InterfaceImplementation(ibuilderRef));

        // Field: private CLRConstructorArgBuilder _argBuilder
        TypeReference? argBuilderTypeRef = null;
        if (userTypes.TryGetValue("CLRConstructorArgBuilder", out var argBuilderTd))
            argBuilderTypeRef = argBuilderTd;

        FieldDefinition? argBuilderField = null;
        if (argBuilderTypeRef is not null)
        {
            argBuilderField = new FieldDefinition("_argBuilder",
                FieldAttributes.Private,
                argBuilderTypeRef);
            td.Fields.Add(argBuilderField);
        }

        var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);

        // .ctor() — default, no arg builder
        var ctor0 = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        td.Methods.Add(ctor0);
        {
            var il = ctor0.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, objCtor));
            il.Append(il.Create(OpCodes.Ret));
        }

        // .ctor(CLRConstructorArgBuilder argBuilder)
        if (argBuilderField is not null && argBuilderTypeRef is not null)
        {
            var ctor1 = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            ctor1.Parameters.Add(new ParameterDefinition("argBuilder", ParameterAttributes.None, argBuilderTypeRef));
            td.Methods.Add(ctor1);
            {
                var il = ctor1.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, objCtor));
                // this._argBuilder = argBuilder
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_1));
                il.Append(il.Create(OpCodes.Stfld, argBuilderField));
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        // GetConstructorDictionary() — delegates to _argBuilder if present, else empty dict
        var getDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            dictType);
        td.Methods.Add(getDict);
        {
            var il = getDict.Body.GetILProcessor();
            if (argBuilderField is not null && argBuilderTd is not null)
            {
                var getDictMethod = argBuilderTd.Methods.FirstOrDefault(m => m.Name == "GetConstructorDictionary");
                if (getDictMethod is not null)
                {
                    // if (_argBuilder != null) return _argBuilder.GetConstructorDictionary()
                    var emptyPath = il.Create(OpCodes.Newobj, dictCtor);
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, argBuilderField));
                    il.Append(il.Create(OpCodes.Brfalse, emptyPath));
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, argBuilderField));
                    il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getDictMethod)));
                    il.Append(il.Create(OpCodes.Ret));
                    // else: return new Dictionary()
                    il.Append(emptyPath);
                    il.Append(il.Create(OpCodes.Ret));
                }
                else
                {
                    il.Append(il.Create(OpCodes.Newobj, dictCtor));
                    il.Append(il.Create(OpCodes.Ret));
                }
            }
            else
            {
                il.Append(il.Create(OpCodes.Newobj, dictCtor));
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        // T Build(Dictionary<string, object> args) — Activator.CreateInstance(typeof(T))
        var build = new MethodDefinition("Build",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            genT);
        build.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, dictType));
        td.Methods.Add(build);
        {
            var il = build.Body.GetILProcessor();
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

    // ── ICaster<TIn, TOut> interface ────────────────────────────────────────

    /// <summary>
    /// Emits the ICaster&lt;TIn, TOut&gt; interface into the output module.
    /// <code>
    /// interface ICaster`2&lt;TIn, TOut&gt; {
    ///     TOut Cast(TIn obj);
    /// }
    /// </code>
    /// </summary>
    public static void EmitICaster(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("ICaster"))
            return;

        var iface = new TypeDefinition("", "ICaster`2",
            TypeAttributes.NestedPublic | TypeAttributes.Interface | TypeAttributes.Abstract,
            module.TypeSystem.Object);

        var genTIn = new GenericParameter("TIn", iface);
        var genTOut = new GenericParameter("TOut", iface);
        iface.GenericParameters.Add(genTIn);
        iface.GenericParameters.Add(genTOut);

        // TOut Cast(TIn obj)
        var cast = new MethodDefinition("Cast",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.Virtual,
            genTOut);
        cast.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, genTIn));
        iface.Methods.Add(cast);

        auraModule.NestedTypes.Add(iface);
        userTypes["ICaster"] = iface;
    }

    // ── Morph extension method ──────────────────────────────────────────────

    /// <summary>
    /// Emits a static helper class with the Morph extension method:
    /// <code>
    /// static class CasterExtensions {
    ///     static TOut Morph&lt;TIn, TOut&gt;(TIn obj, ICaster&lt;TIn, TOut&gt; caster)
    ///         => caster.Cast(obj);
    /// }
    /// </code>
    /// In Aura this becomes: obj.morph(caster)
    /// </summary>
    public static void EmitMorphExtension(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("CasterExtensions"))
            return;

        if (!userTypes.TryGetValue("ICaster", out var icasterTd))
            return;

        var td = new TypeDefinition("", "CasterExtensions",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        // static TOut Morph<TIn, TOut>(TIn obj, ICaster<TIn, TOut> caster)
        var morph = new MethodDefinition("Morph",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Object); // return type replaced below

        var mTIn = new GenericParameter("TIn", morph);
        var mTOut = new GenericParameter("TOut", morph);
        morph.GenericParameters.Add(mTIn);
        morph.GenericParameters.Add(mTOut);
        morph.ReturnType = mTOut;

        // Parameter 1: TIn obj
        morph.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, mTIn));

        // Parameter 2: ICaster<TIn, TOut> caster
        var icasterInst = new GenericInstanceType(icasterTd)
        {
            GenericArguments = { mTIn, mTOut }
        };
        morph.Parameters.Add(new ParameterDefinition("caster", ParameterAttributes.None, icasterInst));

        td.Methods.Add(morph);
        {
            var il = morph.Body.GetILProcessor();

            // Resolve ICaster<TIn, TOut>.Cast method
            var castMethodRef = new MethodReference("Cast", mTOut, icasterInst)
            {
                HasThis = true
            };
            castMethodRef.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, mTIn));

            // return caster.Cast(obj)
            il.Append(il.Create(OpCodes.Ldarg_1)); // caster
            il.Append(il.Create(OpCodes.Ldarg_0)); // obj
            il.Append(il.Create(OpCodes.Callvirt, castMethodRef));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["CasterExtensions"] = td;
    }

    // ── XmlCaster<T> : ICaster<T, string> ───────────────────────────────────

    /// <summary>
    /// Emits XmlCaster&lt;T&gt; : ICaster&lt;T, string&gt;.
    /// Cast(obj) serializes T to XML string via XmlSerializer.
    /// </summary>
    public static void EmitXmlCaster(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("XmlCaster"))
            return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd))
            return;

        var td = new TypeDefinition("", "XmlCaster`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Implement ICaster<T, string>
        var icasterRef = new GenericInstanceType(icasterTd) { GenericArguments = { genT, module.TypeSystem.String } };
        td.Interfaces.Add(new InterfaceImplementation(icasterRef));

        // .ctor()
        EmitDefaultCtor(module, td);

        // string Cast(T obj)
        var cast = new MethodDefinition("Cast",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            module.TypeSystem.String);
        cast.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, genT));
        td.Methods.Add(cast);
        {
            var il = cast.Body.GetILProcessor();
            cast.Body.InitLocals = true;

            // var serializer = new XmlSerializer(typeof(T))
            var xmlSerializerType = typeof(System.Xml.Serialization.XmlSerializer);
            var xmlSerCtor = module.ImportReference(xmlSerializerType.GetConstructor(new[] { typeof(Type) })!);
            var getTypeFromHandle = module.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle")!);

            cast.Body.Variables.Add(new VariableDefinition(module.ImportReference(xmlSerializerType)));  // loc0: serializer
            cast.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(System.IO.StringWriter)))); // loc1: sw
            cast.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String)); // loc2: result

            il.Append(il.Create(OpCodes.Ldtoken, genT));
            il.Append(il.Create(OpCodes.Call, getTypeFromHandle));
            il.Append(il.Create(OpCodes.Newobj, xmlSerCtor));
            il.Append(il.Create(OpCodes.Stloc_0));

            // var sw = new StringWriter()
            var swCtor = module.ImportReference(typeof(System.IO.StringWriter).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Newobj, swCtor));
            il.Append(il.Create(OpCodes.Stloc_1));

            // serializer.Serialize(sw, obj)
            var serializeMethod = module.ImportReference(
                xmlSerializerType.GetMethod("Serialize", new[] { typeof(System.IO.TextWriter), typeof(object) })!);
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldarg_1));
            if (genT.IsValueType)
                il.Append(il.Create(OpCodes.Box, genT));
            else
                il.Append(il.Create(OpCodes.Box, genT)); // box for object param
            il.Append(il.Create(OpCodes.Callvirt, serializeMethod));

            // return sw.ToString()
            var toStringMethod = module.ImportReference(typeof(object).GetMethod("ToString")!);
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Callvirt, toStringMethod));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["XmlCaster"] = td;
    }

    // ── XmlParser<T> : ICaster<string, T> ───────────────────────────────────

    /// <summary>
    /// Emits XmlParser&lt;T&gt; : ICaster&lt;string, T&gt;.
    /// Cast(xml) deserializes XML string to T via XmlSerializer.
    /// </summary>
    public static void EmitXmlParser(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("XmlParser"))
            return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd))
            return;

        var td = new TypeDefinition("", "XmlParser`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Implement ICaster<string, T>
        var icasterRef = new GenericInstanceType(icasterTd) { GenericArguments = { module.TypeSystem.String, genT } };
        td.Interfaces.Add(new InterfaceImplementation(icasterRef));

        EmitDefaultCtor(module, td);

        // T Cast(string obj)
        var cast = new MethodDefinition("Cast",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            genT);
        cast.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, module.TypeSystem.String));
        td.Methods.Add(cast);
        {
            var il = cast.Body.GetILProcessor();
            cast.Body.InitLocals = true;

            var xmlSerializerType = typeof(System.Xml.Serialization.XmlSerializer);
            var xmlSerCtor = module.ImportReference(xmlSerializerType.GetConstructor(new[] { typeof(Type) })!);
            var getTypeFromHandle = module.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle")!);

            cast.Body.Variables.Add(new VariableDefinition(module.ImportReference(xmlSerializerType)));  // loc0
            cast.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(System.IO.StringReader)))); // loc1

            // var serializer = new XmlSerializer(typeof(T))
            il.Append(il.Create(OpCodes.Ldtoken, genT));
            il.Append(il.Create(OpCodes.Call, getTypeFromHandle));
            il.Append(il.Create(OpCodes.Newobj, xmlSerCtor));
            il.Append(il.Create(OpCodes.Stloc_0));

            // var sr = new StringReader(obj)
            var srCtor = module.ImportReference(typeof(System.IO.StringReader).GetConstructor(new[] { typeof(string) })!);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Newobj, srCtor));
            il.Append(il.Create(OpCodes.Stloc_1));

            // return (T)serializer.Deserialize(sr)
            var deserializeMethod = module.ImportReference(
                xmlSerializerType.GetMethod("Deserialize", new[] { typeof(System.IO.TextReader) })!);
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Callvirt, deserializeMethod));
            il.Append(il.Create(OpCodes.Unbox_Any, genT));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["XmlParser"] = td;
    }

    // ── BytesCaster<T> : ICaster<T, byte[]> ────────────────────────────────

    /// <summary>
    /// Emits BytesCaster&lt;T&gt; : ICaster&lt;T, byte[]&gt;.
    /// Cast(obj) serializes T to UTF-8 JSON bytes via JsonSerializer.SerializeToUtf8Bytes.
    /// </summary>
    public static void EmitBytesCaster(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("BytesCaster"))
            return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd))
            return;

        var byteArrayType = module.ImportReference(typeof(byte[]));

        var td = new TypeDefinition("", "BytesCaster`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Implement ICaster<T, byte[]>
        var icasterRef = new GenericInstanceType(icasterTd) { GenericArguments = { genT, byteArrayType } };
        td.Interfaces.Add(new InterfaceImplementation(icasterRef));

        EmitDefaultCtor(module, td);

        // byte[] Cast(T obj)
        var cast = new MethodDefinition("Cast",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            byteArrayType);
        cast.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, genT));
        td.Methods.Add(cast);
        {
            var il = cast.Body.GetILProcessor();

            // return JsonSerializer.SerializeToUtf8Bytes(obj)
            var jsonSerializerType = typeof(System.Text.Json.JsonSerializer);
            var serializeMethod = module.ImportReference(
                jsonSerializerType.GetMethod("SerializeToUtf8Bytes",
                    new[] { typeof(object), typeof(Type), typeof(System.Text.Json.JsonSerializerOptions) })
                ?? jsonSerializerType.GetMethod("SerializeToUtf8Bytes",
                    new[] { typeof(object), typeof(System.Text.Json.JsonSerializerOptions) })!);

            // Use the simple overload: SerializeToUtf8Bytes(object, Type)
            var simpleSerialize = module.ImportReference(
                jsonSerializerType.GetMethod("SerializeToUtf8Bytes",
                    new[] { typeof(object), typeof(Type), typeof(System.Text.Json.JsonSerializerOptions) })!);
            var getTypeFromHandle = module.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle")!);

            il.Append(il.Create(OpCodes.Ldarg_1));         // obj
            il.Append(il.Create(OpCodes.Box, genT));        // box to object
            il.Append(il.Create(OpCodes.Ldtoken, genT));    // typeof(T)
            il.Append(il.Create(OpCodes.Call, getTypeFromHandle));
            il.Append(il.Create(OpCodes.Ldnull));           // options = null
            il.Append(il.Create(OpCodes.Call, simpleSerialize));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["BytesCaster"] = td;
    }

    // ── BytesParser<T> : ICaster<byte[], T> ────────────────────────────────

    /// <summary>
    /// Emits BytesParser&lt;T&gt; : ICaster&lt;byte[], T&gt;.
    /// Cast(bytes) deserializes UTF-8 JSON bytes to T via JsonSerializer.Deserialize.
    /// </summary>
    public static void EmitBytesParser(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("BytesParser"))
            return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd))
            return;

        var byteArrayType = module.ImportReference(typeof(byte[]));

        var td = new TypeDefinition("", "BytesParser`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Implement ICaster<byte[], T>
        var icasterRef = new GenericInstanceType(icasterTd) { GenericArguments = { byteArrayType, genT } };
        td.Interfaces.Add(new InterfaceImplementation(icasterRef));

        EmitDefaultCtor(module, td);

        // T Cast(byte[] obj)
        var cast = new MethodDefinition("Cast",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            genT);
        cast.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, byteArrayType));
        td.Methods.Add(cast);
        {
            var il = cast.Body.GetILProcessor();

            // JsonSerializer.Deserialize(ReadOnlySpan<byte>, Type, options)
            // Use: JsonSerializer.Deserialize(utf8Json, returnType, options)
            var jsonSerializerType = typeof(System.Text.Json.JsonSerializer);
            var getTypeFromHandle = module.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle")!);

            // We'll use the overload: Deserialize(ReadOnlySpan<byte>, Type, JsonSerializerOptions?)
            // But Span is tricky in IL. Instead, use Deserialize(string, Type, options) after converting bytes to string.
            // Simpler approach: Encoding.UTF8.GetString(bytes) then JsonSerializer.Deserialize(string, type, options)

            cast.Body.InitLocals = true;
            cast.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String)); // loc0: json string

            // string json = Encoding.UTF8.GetString(obj)
            var encodingType = typeof(System.Text.Encoding);
            var getUtf8 = module.ImportReference(encodingType.GetProperty("UTF8")!.GetGetMethod()!);
            var getString = module.ImportReference(
                typeof(System.Text.Encoding).GetMethod("GetString", new[] { typeof(byte[]) })!);

            il.Append(il.Create(OpCodes.Call, getUtf8));     // Encoding.UTF8
            il.Append(il.Create(OpCodes.Ldarg_1));            // obj (byte[])
            il.Append(il.Create(OpCodes.Callvirt, getString)); // .GetString(byte[])
            il.Append(il.Create(OpCodes.Stloc_0));

            // return (T)JsonSerializer.Deserialize(json, typeof(T), null)
            var deserializeMethod = module.ImportReference(
                jsonSerializerType.GetMethod("Deserialize",
                    new[] { typeof(string), typeof(Type), typeof(System.Text.Json.JsonSerializerOptions) })!);

            il.Append(il.Create(OpCodes.Ldloc_0));            // json
            il.Append(il.Create(OpCodes.Ldtoken, genT));       // typeof(T)
            il.Append(il.Create(OpCodes.Call, getTypeFromHandle));
            il.Append(il.Create(OpCodes.Ldnull));              // options = null
            il.Append(il.Create(OpCodes.Call, deserializeMethod));
            il.Append(il.Create(OpCodes.Unbox_Any, genT));     // cast to T
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["BytesParser"] = td;
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private static void EmitDefaultCtor(ModuleDefinition module, TypeDefinition td)
    {
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        td.Methods.Add(ctor);
        var il = ctor.Body.GetILProcessor();
        var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, objCtor));
        il.Append(il.Create(OpCodes.Ret));
    }
}
