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
    /// Adapter: wraps an inner ICaster&lt;T, string&gt; and converts the result to UTF-8 bytes.
    /// <code>
    /// class BytesCaster`1&lt;T&gt; : ICaster&lt;T, byte[]&gt; {
    ///     private ICaster&lt;T, string&gt; _inner;
    ///     .ctor(ICaster&lt;T, string&gt; caster) { _inner = caster; }
    ///     byte[] Cast(T obj) => Encoding.UTF8.GetBytes(_inner.Cast(obj));
    /// }
    /// </code>
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
        var icasterOutRef = new GenericInstanceType(icasterTd) { GenericArguments = { genT, byteArrayType } };
        td.Interfaces.Add(new InterfaceImplementation(icasterOutRef));

        // Field: private ICaster<T, string> _inner
        var icasterInnerRef = new GenericInstanceType(icasterTd) { GenericArguments = { genT, module.TypeSystem.String } };
        var innerField = new FieldDefinition("_inner", FieldAttributes.Private, icasterInnerRef);
        td.Fields.Add(innerField);

        // .ctor(ICaster<T, string> caster) { base(); _inner = caster; }
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition("caster", ParameterAttributes.None, icasterInnerRef));
        td.Methods.Add(ctor);
        {
            var il = ctor.Body.GetILProcessor();
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, objCtor));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, innerField));
            il.Append(il.Create(OpCodes.Ret));
        }

        // byte[] Cast(T obj) => Encoding.UTF8.GetBytes(_inner.Cast(obj))
        var cast = new MethodDefinition("Cast",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            byteArrayType);
        cast.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, genT));
        td.Methods.Add(cast);
        {
            var il = cast.Body.GetILProcessor();

            // Resolve ICaster<T, string>.Cast method reference
            var innerCastRef = new MethodReference("Cast", module.TypeSystem.String, icasterInnerRef) { HasThis = true };
            innerCastRef.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, genT));

            // string str = _inner.Cast(obj)
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, innerField));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Callvirt, innerCastRef));

            // return Encoding.UTF8.GetBytes(str)
            var getUtf8 = module.ImportReference(typeof(System.Text.Encoding).GetProperty("UTF8")!.GetGetMethod()!);
            var getBytes = module.ImportReference(typeof(System.Text.Encoding).GetMethod("GetBytes", new[] { typeof(string) })!);
            il.Append(il.Create(OpCodes.Call, getUtf8));
            // stack: str, Encoding  — need to swap. Use a local.
            // Actually: GetBytes is instance method on Encoding. We need Encoding on stack first.
            // Let me restructure:
            il.Body.Instructions.Clear();

            cast.Body.InitLocals = true;
            cast.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String)); // loc0: str

            // string str = _inner.Cast(obj)
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, innerField));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Callvirt, innerCastRef));
            il.Append(il.Create(OpCodes.Stloc_0));

            // return Encoding.UTF8.GetBytes(str)
            il.Append(il.Create(OpCodes.Call, getUtf8));     // Encoding.UTF8
            il.Append(il.Create(OpCodes.Ldloc_0));           // str
            il.Append(il.Create(OpCodes.Callvirt, getBytes)); // .GetBytes(str)
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["BytesCaster"] = td;
    }

    // ── BytesParser<T> : ICaster<byte[], T> ────────────────────────────────

    /// <summary>
    /// Emits BytesParser&lt;T&gt; : ICaster&lt;byte[], T&gt;.
    /// Adapter: converts byte[] to string via UTF-8, then delegates to inner ICaster&lt;string, T&gt;.
    /// <code>
    /// class BytesParser`1&lt;T&gt; : ICaster&lt;byte[], T&gt; {
    ///     private ICaster&lt;string, T&gt; _inner;
    ///     .ctor(ICaster&lt;string, T&gt; caster) { _inner = caster; }
    ///     T Cast(byte[] obj) => _inner.Cast(Encoding.UTF8.GetString(obj));
    /// }
    /// </code>
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
        var icasterOutRef = new GenericInstanceType(icasterTd) { GenericArguments = { byteArrayType, genT } };
        td.Interfaces.Add(new InterfaceImplementation(icasterOutRef));

        // Field: private ICaster<string, T> _inner
        var icasterInnerRef = new GenericInstanceType(icasterTd) { GenericArguments = { module.TypeSystem.String, genT } };
        var innerField = new FieldDefinition("_inner", FieldAttributes.Private, icasterInnerRef);
        td.Fields.Add(innerField);

        // .ctor(ICaster<string, T> caster) { base(); _inner = caster; }
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition("caster", ParameterAttributes.None, icasterInnerRef));
        td.Methods.Add(ctor);
        {
            var il = ctor.Body.GetILProcessor();
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, objCtor));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, innerField));
            il.Append(il.Create(OpCodes.Ret));
        }

        // T Cast(byte[] obj) => _inner.Cast(Encoding.UTF8.GetString(obj))
        var cast = new MethodDefinition("Cast",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            genT);
        cast.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, byteArrayType));
        td.Methods.Add(cast);
        {
            var il = cast.Body.GetILProcessor();
            cast.Body.InitLocals = true;
            cast.Body.Variables.Add(new VariableDefinition(module.TypeSystem.String)); // loc0: str

            // string str = Encoding.UTF8.GetString(obj)
            var getUtf8 = module.ImportReference(typeof(System.Text.Encoding).GetProperty("UTF8")!.GetGetMethod()!);
            var getString = module.ImportReference(typeof(System.Text.Encoding).GetMethod("GetString", new[] { typeof(byte[]) })!);

            il.Append(il.Create(OpCodes.Call, getUtf8));     // Encoding.UTF8
            il.Append(il.Create(OpCodes.Ldarg_1));            // obj (byte[])
            il.Append(il.Create(OpCodes.Callvirt, getString)); // .GetString(byte[])
            il.Append(il.Create(OpCodes.Stloc_0));

            // return _inner.Cast(str)
            var innerCastRef = new MethodReference("Cast", genT, icasterInnerRef) { HasThis = true };
            innerCastRef.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, module.TypeSystem.String));

            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, innerField));
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Callvirt, innerCastRef));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["BytesParser"] = td;
    }

    // ── Caster Arg Builders ────────────────────────────────────────────────

    /// <summary>
    /// Emits XmlCasterArgs : CLRConstructorArgBuilder (empty — no args needed).
    /// </summary>
    public static void EmitXmlCasterArgs(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("XmlCasterArgs")) return;
        if (!userTypes.TryGetValue("CLRConstructorArgBuilder", out var baseTd)) return;
        EmitEmptyArgBuilder(module, auraModule, userTypes, "XmlCasterArgs", baseTd);
    }

    /// <summary>
    /// Emits XmlParserArgs : CLRConstructorArgBuilder (empty — no args needed).
    /// </summary>
    public static void EmitXmlParserArgs(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("XmlParserArgs")) return;
        if (!userTypes.TryGetValue("CLRConstructorArgBuilder", out var baseTd)) return;
        EmitEmptyArgBuilder(module, auraModule, userTypes, "XmlParserArgs", baseTd);
    }

    /// <summary>
    /// Emits BytesCasterArgs&lt;T&gt; : CLRConstructorArgBuilder.
    /// Has property Caster: ICaster&lt;T, string&gt;.
    /// </summary>
    public static void EmitBytesCasterArgs(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("BytesCasterArgs")) return;
        if (!userTypes.TryGetValue("CLRConstructorArgBuilder", out var baseTd)) return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd)) return;

        var td = new TypeDefinition("", "BytesCasterArgs`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            baseTd);
        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Property: ICaster<T, string> Caster { get; set; }
        var casterType = new GenericInstanceType(icasterTd) { GenericArguments = { genT, module.TypeSystem.String } };
        EmitAutoProperty(module, td, "Caster", casterType);

        // .ctor() calls base()
        EmitCtorCallingBase(module, td, baseTd);

        auraModule.NestedTypes.Add(td);
        userTypes["BytesCasterArgs"] = td;
    }

    /// <summary>
    /// Emits BytesParserArgs&lt;T&gt; : CLRConstructorArgBuilder.
    /// Has property Caster: ICaster&lt;string, T&gt;.
    /// </summary>
    public static void EmitBytesParserArgs(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("BytesParserArgs")) return;
        if (!userTypes.TryGetValue("CLRConstructorArgBuilder", out var baseTd)) return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd)) return;

        var td = new TypeDefinition("", "BytesParserArgs`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            baseTd);
        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Property: ICaster<string, T> Caster { get; set; }
        var casterType = new GenericInstanceType(icasterTd) { GenericArguments = { module.TypeSystem.String, genT } };
        EmitAutoProperty(module, td, "Caster", casterType);

        // .ctor() calls base()
        EmitCtorCallingBase(module, td, baseTd);

        auraModule.NestedTypes.Add(td);
        userTypes["BytesParserArgs"] = td;
    }

    // ── Caster Builders ─────────────────────────────────────────────────────

    /// <summary>
    /// Emits XmlCasterBuilder&lt;T&gt; : IBuilder&lt;XmlCaster&lt;T&gt;&gt;.
    /// Build() returns new XmlCaster&lt;T&gt;().
    /// </summary>
    public static void EmitXmlCasterBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("XmlCasterBuilder")) return;
        if (!userTypes.TryGetValue("IBuilder", out var ibuilderTd)) return;
        if (!userTypes.TryGetValue("XmlCaster", out var xmlCasterTd)) return;

        EmitSimpleCasterBuilder(module, auraModule, userTypes, "XmlCasterBuilder", ibuilderTd, xmlCasterTd);
    }

    /// <summary>
    /// Emits XmlParserBuilder&lt;T&gt; : IBuilder&lt;XmlParser&lt;T&gt;&gt;.
    /// Build() returns new XmlParser&lt;T&gt;().
    /// </summary>
    public static void EmitXmlParserBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("XmlParserBuilder")) return;
        if (!userTypes.TryGetValue("IBuilder", out var ibuilderTd)) return;
        if (!userTypes.TryGetValue("XmlParser", out var xmlParserTd)) return;

        EmitSimpleCasterBuilder(module, auraModule, userTypes, "XmlParserBuilder", ibuilderTd, xmlParserTd);
    }

    /// <summary>
    /// Emits BytesCasterBuilder&lt;T&gt; : IBuilder&lt;BytesCaster&lt;T&gt;&gt;.
    /// Build() extracts ICaster&lt;T,string&gt; from args and creates BytesCaster&lt;T&gt;(inner).
    /// </summary>
    public static void EmitBytesCasterBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("BytesCasterBuilder")) return;
        if (!userTypes.TryGetValue("IBuilder", out var ibuilderTd)) return;
        if (!userTypes.TryGetValue("BytesCaster", out var bytesCasterTd)) return;
        if (!userTypes.TryGetValue("BytesCasterArgs", out var argsTd)) return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd)) return;

        EmitAdapterCasterBuilder(module, auraModule, userTypes,
            "BytesCasterBuilder", ibuilderTd, bytesCasterTd, argsTd, icasterTd,
            innerTIn: null, innerTOut: module.TypeSystem.String, isStringFirst: false);
    }

    /// <summary>
    /// Emits BytesParserBuilder&lt;T&gt; : IBuilder&lt;BytesParser&lt;T&gt;&gt;.
    /// Build() extracts ICaster&lt;string,T&gt; from args and creates BytesParser&lt;T&gt;(inner).
    /// </summary>
    public static void EmitBytesParserBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("BytesParserBuilder")) return;
        if (!userTypes.TryGetValue("IBuilder", out var ibuilderTd)) return;
        if (!userTypes.TryGetValue("BytesParser", out var bytesParserTd)) return;
        if (!userTypes.TryGetValue("BytesParserArgs", out var argsTd)) return;
        if (!userTypes.TryGetValue("ICaster", out var icasterTd)) return;

        EmitAdapterCasterBuilder(module, auraModule, userTypes,
            "BytesParserBuilder", ibuilderTd, bytesParserTd, argsTd, icasterTd,
            innerTIn: module.TypeSystem.String, innerTOut: null, isStringFirst: true);
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

    private static void EmitCtorCallingBase(ModuleDefinition module, TypeDefinition td, TypeDefinition baseTd)
    {
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        td.Methods.Add(ctor);
        var il = ctor.Body.GetILProcessor();
        var baseCtor = baseTd.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 0);
        il.Append(il.Create(OpCodes.Ldarg_0));
        if (baseCtor is not null)
            il.Append(il.Create(OpCodes.Call, module.ImportReference(baseCtor)));
        else
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
        il.Append(il.Create(OpCodes.Ret));
    }

    private static void EmitAutoProperty(ModuleDefinition module, TypeDefinition td, string name, TypeReference propType)
    {
        var backing = new FieldDefinition($"<{name}>k__BackingField", FieldAttributes.Private, propType);
        td.Fields.Add(backing);

        var getter = new MethodDefinition($"get_{name}",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            propType);
        td.Methods.Add(getter);
        {
            var il = getter.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, backing));
            il.Append(il.Create(OpCodes.Ret));
        }

        var setter = new MethodDefinition($"set_{name}",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            module.TypeSystem.Void);
        setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, propType));
        td.Methods.Add(setter);
        {
            var il = setter.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, backing));
            il.Append(il.Create(OpCodes.Ret));
        }

        var prop = new PropertyDefinition(name, PropertyAttributes.None, propType)
        {
            GetMethod = getter,
            SetMethod = setter
        };
        td.Properties.Add(prop);
    }

    private static void EmitEmptyArgBuilder(ModuleDefinition module, TypeDefinition auraModule,
        Dictionary<string, TypeDefinition> userTypes, string name, TypeDefinition baseTd)
    {
        var td = new TypeDefinition("", name,
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            baseTd);
        EmitCtorCallingBase(module, td, baseTd);
        auraModule.NestedTypes.Add(td);
        userTypes[name] = td;
    }

    /// <summary>
    /// Emits a simple caster builder (no inner caster field) — e.g., XmlCasterBuilder, XmlParserBuilder.
    /// Build() just calls Newobj on the target caster's default ctor.
    /// </summary>
    private static void EmitSimpleCasterBuilder(ModuleDefinition module, TypeDefinition auraModule,
        Dictionary<string, TypeDefinition> userTypes, string name,
        TypeDefinition ibuilderTd, TypeDefinition casterTd)
    {
        var td = new TypeDefinition("", $"{name}`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Target caster type: e.g., XmlCaster<T>
        var casterInst = new GenericInstanceType(casterTd) { GenericArguments = { genT } };

        // Implement IBuilder<CasterType<T>>
        var ibuilderRef = new GenericInstanceType(ibuilderTd) { GenericArguments = { casterInst } };
        td.Interfaces.Add(new InterfaceImplementation(ibuilderRef));

        // Field: optional CLRConstructorArgBuilder _argBuilder
        var argBuilderRef = userTypes.TryGetValue("CLRConstructorArgBuilder", out var argBuilderTd)
            ? (TypeReference)argBuilderTd : module.TypeSystem.Object;

        var argField = new FieldDefinition("_argBuilder", FieldAttributes.Private, argBuilderRef);
        td.Fields.Add(argField);

        // .ctor()
        EmitDefaultCtor(module, td);

        // .ctor(CLRConstructorArgBuilder argBuilder)
        var ctor1 = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        ctor1.Parameters.Add(new ParameterDefinition("argBuilder", ParameterAttributes.None, argBuilderRef));
        td.Methods.Add(ctor1);
        {
            var il = ctor1.Body.GetILProcessor();
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, objCtor));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, argField));
            il.Append(il.Create(OpCodes.Ret));
        }

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);

        // GetConstructorDictionary()
        var getDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            dictType);
        td.Methods.Add(getDict);
        {
            var il = getDict.Body.GetILProcessor();
            // return _argBuilder?.GetConstructorDictionary() ?? new()
            il.Append(il.Create(OpCodes.Newobj, dictCtor));
            il.Append(il.Create(OpCodes.Ret));
        }

        // Build(args) => new CasterType<T>()
        var build = new MethodDefinition("Build",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            casterInst);
        build.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, dictType));
        td.Methods.Add(build);
        {
            var il = build.Body.GetILProcessor();
            // Find the default ctor of the caster type
            var casterCtor = casterTd.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 0);
            if (casterCtor is not null)
            {
                // new CasterType<T>() — need to resolve the ctor on the generic instance
                var ctorRef = new MethodReference(".ctor", module.TypeSystem.Void, casterInst)
                {
                    HasThis = true
                };
                il.Append(il.Create(OpCodes.Newobj, ctorRef));
            }
            else
            {
                il.Append(il.Create(OpCodes.Ldnull));
            }
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes[name] = td;
    }

    /// <summary>
    /// Emits an adapter caster builder — e.g., BytesCasterBuilder, BytesParserBuilder.
    /// Build() extracts the inner ICaster from the typed args, then calls the adapter's 1-arg ctor.
    /// </summary>
    private static void EmitAdapterCasterBuilder(ModuleDefinition module, TypeDefinition auraModule,
        Dictionary<string, TypeDefinition> userTypes, string name,
        TypeDefinition ibuilderTd, TypeDefinition adapterTd, TypeDefinition argsTd,
        TypeDefinition icasterTd,
        TypeReference? innerTIn, TypeReference? innerTOut, bool isStringFirst)
    {
        var td = new TypeDefinition("", $"{name}`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        var genT = new GenericParameter("T", td);
        td.GenericParameters.Add(genT);

        // Target adapter type: e.g., BytesCaster<T>
        var adapterInst = new GenericInstanceType(adapterTd) { GenericArguments = { genT } };

        // Implement IBuilder<AdapterType<T>>
        var ibuilderRef = new GenericInstanceType(ibuilderTd) { GenericArguments = { adapterInst } };
        td.Interfaces.Add(new InterfaceImplementation(ibuilderRef));

        // Field: typed args builder
        var argsInst = new GenericInstanceType(argsTd) { GenericArguments = { genT } };
        var argsField = new FieldDefinition("_argBuilder", FieldAttributes.Private, argsInst);
        td.Fields.Add(argsField);

        // .ctor()
        EmitDefaultCtor(module, td);

        // .ctor(ArgType<T> argBuilder) — accepts the typed args
        var ctor1 = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        ctor1.Parameters.Add(new ParameterDefinition("argBuilder", ParameterAttributes.None, argsInst));
        td.Methods.Add(ctor1);
        {
            var il = ctor1.Body.GetILProcessor();
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, objCtor));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, argsField));
            il.Append(il.Create(OpCodes.Ret));
        }

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);

        // GetConstructorDictionary()
        var getDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            dictType);
        td.Methods.Add(getDict);
        {
            var il = getDict.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Newobj, dictCtor));
            il.Append(il.Create(OpCodes.Ret));
        }

        // Build(args) => new AdapterType<T>(_argBuilder.Caster)
        var build = new MethodDefinition("Build",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            adapterInst);
        build.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, dictType));
        td.Methods.Add(build);
        {
            var il = build.Body.GetILProcessor();

            // Resolve inner ICaster type: ICaster<T, string> or ICaster<string, T>
            TypeReference tIn = isStringFirst ? module.TypeSystem.String : (TypeReference)genT;
            TypeReference tOut = isStringFirst ? (TypeReference)genT : module.TypeSystem.String;
            var innerCasterType = new GenericInstanceType(icasterTd) { GenericArguments = { tIn, tOut } };

            // Get the Caster property getter from the args type
            var getCaster = new MethodReference($"get_Caster", innerCasterType, argsInst) { HasThis = true };

            // Adapter ctor that takes ICaster
            var adapterCtorRef = new MethodReference(".ctor", module.TypeSystem.Void, adapterInst) { HasThis = true };
            adapterCtorRef.Parameters.Add(new ParameterDefinition("caster", ParameterAttributes.None, innerCasterType));

            // return new AdapterType<T>(_argBuilder.get_Caster())
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, argsField));
            il.Append(il.Create(OpCodes.Callvirt, getCaster));
            il.Append(il.Create(OpCodes.Newobj, adapterCtorRef));
            il.Append(il.Create(OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes[name] = td;
    }

    // ── Room ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the Room class: a publish/subscribe message bus.
    /// class Room {
    ///     private List&lt;IRoomReceiver&gt; _members;
    ///     void Join(IRoomReceiver receiver);
    ///     void Leave(IRoomReceiver receiver);
    ///     void Broadcast(string message, object args);
    ///     int MemberCount { get; }
    /// }
    /// </summary>
    public static void EmitRoom(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("Room")) return;

        // Ensure IRoomReceiver exists
        EmitIRoomReceiver(module, auraModule, userTypes);
        var iRoomReceiver = userTypes["IRoomReceiver"];

        var td = new TypeDefinition("", "Room",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        // _members field: List<object> (we use object because generic List<IRoomReceiver> is complex to emit)
        var listType = module.ImportReference(typeof(System.Collections.Generic.List<object>));
        var listCtor = module.ImportReference(typeof(System.Collections.Generic.List<object>).GetConstructor(Type.EmptyTypes)!);
        var listAdd = module.ImportReference(typeof(System.Collections.Generic.List<object>).GetMethod("Add")!);
        var listRemove = module.ImportReference(typeof(System.Collections.Generic.List<object>).GetMethod("Remove", new[] { typeof(object) })!);
        var listContains = module.ImportReference(typeof(System.Collections.Generic.List<object>).GetMethod("Contains")!);
        var listCount = module.ImportReference(typeof(System.Collections.Generic.List<object>).GetProperty("Count")!.GetGetMethod()!);

        var membersField = new FieldDefinition("_members", FieldAttributes.Private, listType);
        td.Fields.Add(membersField);

        // .ctor: _members = new List<object>()
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var cil = ctor.Body.GetILProcessor();
        cil.Append(cil.Create(OpCodes.Ldarg_0));
        cil.Append(cil.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
        cil.Append(cil.Create(OpCodes.Ldarg_0));
        cil.Append(cil.Create(OpCodes.Newobj, listCtor));
        cil.Append(cil.Create(OpCodes.Stfld, membersField));
        cil.Append(cil.Create(OpCodes.Ret));
        td.Methods.Add(ctor);

        // void Join(IRoomReceiver receiver)
        var join = new MethodDefinition("Join",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        join.Parameters.Add(new ParameterDefinition("receiver", ParameterAttributes.None, iRoomReceiver));
        var jil = join.Body.GetILProcessor();
        // if (!_members.Contains(receiver)) _members.Add(receiver)
        var jEnd = jil.Create(OpCodes.Ret);
        jil.Append(jil.Create(OpCodes.Ldarg_0));
        jil.Append(jil.Create(OpCodes.Ldfld, membersField));
        jil.Append(jil.Create(OpCodes.Ldarg_1));
        jil.Append(jil.Create(OpCodes.Callvirt, listContains));
        jil.Append(jil.Create(OpCodes.Brtrue, jEnd));
        jil.Append(jil.Create(OpCodes.Ldarg_0));
        jil.Append(jil.Create(OpCodes.Ldfld, membersField));
        jil.Append(jil.Create(OpCodes.Ldarg_1));
        jil.Append(jil.Create(OpCodes.Callvirt, listAdd));
        jil.Append(jEnd);
        td.Methods.Add(join);

        // void Leave(IRoomReceiver receiver)
        var leave = new MethodDefinition("Leave",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        leave.Parameters.Add(new ParameterDefinition("receiver", ParameterAttributes.None, iRoomReceiver));
        var lil = leave.Body.GetILProcessor();
        lil.Append(lil.Create(OpCodes.Ldarg_0));
        lil.Append(lil.Create(OpCodes.Ldfld, membersField));
        lil.Append(lil.Create(OpCodes.Ldarg_1));
        lil.Append(lil.Create(OpCodes.Callvirt, listRemove));
        lil.Append(lil.Create(OpCodes.Pop)); // Remove returns bool
        lil.Append(lil.Create(OpCodes.Ret));
        td.Methods.Add(leave);

        // void Broadcast(string message, object args)
        var broadcast = new MethodDefinition("Broadcast",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        broadcast.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, module.TypeSystem.String));
        broadcast.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, module.TypeSystem.Object));
        var bil = broadcast.Body.GetILProcessor();
        // for (int i = 0; i < _members.Count; i++) ((IRoomReceiver)_members[i]).OnMessage(message, args)
        var idxVar = new VariableDefinition(module.TypeSystem.Int32);
        broadcast.Body.Variables.Add(idxVar);
        var loopCheck = bil.Create(OpCodes.Nop);
        var loopBody = bil.Create(OpCodes.Nop);
        // i = 0
        bil.Append(bil.Create(OpCodes.Ldc_I4_0));
        bil.Append(bil.Create(OpCodes.Stloc, idxVar));
        bil.Append(bil.Create(OpCodes.Br, loopCheck));
        // body
        bil.Append(loopBody);
        bil.Append(bil.Create(OpCodes.Ldarg_0));
        bil.Append(bil.Create(OpCodes.Ldfld, membersField));
        bil.Append(bil.Create(OpCodes.Ldloc, idxVar));
        var listGetItem = module.ImportReference(typeof(System.Collections.Generic.List<object>).GetProperty("Item")!.GetGetMethod()!);
        bil.Append(bil.Create(OpCodes.Callvirt, listGetItem));
        bil.Append(bil.Create(OpCodes.Castclass, iRoomReceiver));
        bil.Append(bil.Create(OpCodes.Ldarg_1)); // message
        bil.Append(bil.Create(OpCodes.Ldarg_2)); // args
        var onMessage = iRoomReceiver.Methods.First(m => m.Name == "OnMessage");
        bil.Append(bil.Create(OpCodes.Callvirt, onMessage));
        // i++
        bil.Append(bil.Create(OpCodes.Ldloc, idxVar));
        bil.Append(bil.Create(OpCodes.Ldc_I4_1));
        bil.Append(bil.Create(OpCodes.Add));
        bil.Append(bil.Create(OpCodes.Stloc, idxVar));
        // check: i < _members.Count
        bil.Append(loopCheck);
        bil.Append(bil.Create(OpCodes.Ldloc, idxVar));
        bil.Append(bil.Create(OpCodes.Ldarg_0));
        bil.Append(bil.Create(OpCodes.Ldfld, membersField));
        bil.Append(bil.Create(OpCodes.Callvirt, listCount));
        bil.Append(bil.Create(OpCodes.Blt, loopBody));
        bil.Append(bil.Create(OpCodes.Ret));
        td.Methods.Add(broadcast);

        // int MemberCount { get; }
        var getMemberCount = new MethodDefinition("get_MemberCount",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            module.TypeSystem.Int32);
        var mcil = getMemberCount.Body.GetILProcessor();
        mcil.Append(mcil.Create(OpCodes.Ldarg_0));
        mcil.Append(mcil.Create(OpCodes.Ldfld, membersField));
        mcil.Append(mcil.Create(OpCodes.Callvirt, listCount));
        mcil.Append(mcil.Create(OpCodes.Ret));
        td.Methods.Add(getMemberCount);

        var memberCountProp = new PropertyDefinition("MemberCount", PropertyAttributes.None, module.TypeSystem.Int32)
        {
            GetMethod = getMemberCount
        };
        td.Properties.Add(memberCountProp);

        auraModule.NestedTypes.Add(td);
        userTypes["Room"] = td;
    }

    // ── RoomArgs + RoomBuilder ──────────────────────────────────────────

    /// <summary>
    /// Emits RoomArgs (empty arg builder) and RoomBuilder for Room.
    /// </summary>
    public static void EmitRoomArgs(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("RoomArgs")) return;

        // Ensure CLRConstructorArgBuilder exists
        if (!userTypes.TryGetValue("CLRConstructorArgBuilder", out var argBuilderBase)) return;

        var td = new TypeDefinition("", "RoomArgs",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            argBuilderBase);

        // .ctor
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var cil = ctor.Body.GetILProcessor();
        cil.Append(cil.Create(OpCodes.Ldarg_0));
        var baseCtor = argBuilderBase.Methods.First(m => m.IsConstructor && m.Parameters.Count == 0);
        cil.Append(cil.Create(OpCodes.Call, baseCtor));
        cil.Append(cil.Create(OpCodes.Ret));
        td.Methods.Add(ctor);

        auraModule.NestedTypes.Add(td);
        userTypes["RoomArgs"] = td;
    }

    public static void EmitRoomBuilder(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        if (userTypes.ContainsKey("RoomBuilder")) return;
        if (!userTypes.TryGetValue("Room", out var roomTd)) return;
        if (!userTypes.TryGetValue("IBuilder", out var ibuilder)) return;

        var td = new TypeDefinition("", "RoomBuilder",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        // Implement IBuilder (non-generic version for simplicity — Build returns object)
        td.Interfaces.Add(new InterfaceImplementation(ibuilder));

        // .ctor
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var cil = ctor.Body.GetILProcessor();
        cil.Append(cil.Create(OpCodes.Ldarg_0));
        cil.Append(cil.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
        cil.Append(cil.Create(OpCodes.Ret));
        td.Methods.Add(ctor);

        // GetConstructorDictionary
        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);
        var getDict = new MethodDefinition("GetConstructorDictionary",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final,
            dictType);
        var gdil = getDict.Body.GetILProcessor();
        gdil.Append(gdil.Create(OpCodes.Newobj, dictCtor));
        gdil.Append(gdil.Create(OpCodes.Ret));
        td.Methods.Add(getDict);

        // Build(Dictionary<string,object> args) -> object { return new Room(); }
        var build = new MethodDefinition("Build",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final,
            module.TypeSystem.Object);
        build.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, dictType));
        var bil = build.Body.GetILProcessor();
        var roomCtor = roomTd.Methods.First(m => m.IsConstructor && m.Parameters.Count == 0);
        bil.Append(bil.Create(OpCodes.Newobj, roomCtor));
        bil.Append(bil.Create(OpCodes.Ret));
        td.Methods.Add(build);

        auraModule.NestedTypes.Add(td);
        userTypes["RoomBuilder"] = td;
    }
}
