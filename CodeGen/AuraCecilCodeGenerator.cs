using System.Collections;
using System.Linq;
using AuraLang.Ast;
using AuraLang.I18n;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AuraLang.CodeGen;

/// <summary>
/// Aura AST -> CIL (.dll) generator based on Mono.Cecil.
///
/// v3 focus:
/// 1) Member emission: class/struct methods are emitted into their owning TypeDefinition.
/// 2) Instance context: emitter understands "this" and resolves implicit member access.
/// 3) Window metadata: window interfaces are attached to their target classes (best-effort).
///
/// Notes:
/// - This codegen expects you to run lowering first (pipe/using/switch/pattern/guard lowering).
/// - This is still a "frontier" backend; user-defined members (fields/properties) are emitted best-effort via reflection.
/// </summary>
public sealed class AuraCecilCodeGenerator
{
    private readonly List<CodeGenDiagnostic> _diags = new();

    public CodeGenResult Generate(CompilationUnitNode ast, string outputPath,
        string? assemblyName = null, string? sourceFilePath = null)
    {
        _diags.Clear();

        assemblyName ??= Path.GetFileNameWithoutExtension(outputPath);
        var asmName = new AssemblyNameDefinition(assemblyName, new Version(1, 0, 0, 0));
        var asm = AssemblyDefinition.CreateAssembly(asmName, assemblyName, ModuleKind.Dll);

        var module = asm.MainModule;

        // PDB debug document (portable PDB)
        Mono.Cecil.Cil.Document? debugDoc = null;
        if (sourceFilePath is not null)
        {
            debugDoc = new Mono.Cecil.Cil.Document(Path.GetFullPath(sourceFilePath))
            {
                Type = DocumentType.Text,
                Language = DocumentLanguage.Other,
                HashAlgorithm = DocumentHashAlgorithm.SHA256,
            };
        }

        // Import basic runtime types
        _ = module.ImportReference(typeof(object));
        _ = module.ImportReference(typeof(string));
        _ = module.ImportReference(typeof(int));
        _ = module.ImportReference(typeof(bool));
        _ = module.ImportReference(typeof(Exception));
        _ = module.ImportReference(typeof(Console));
        _ = module.ImportReference(typeof(ValueType));
        _ = module.ImportReference(typeof(Attribute));

        // Keep AuraModule for top-level functions & as a container for nested types
        var auraModule = new TypeDefinition(
            @namespace: "",
            name: "AuraModule",
            attributes: TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            baseType: module.TypeSystem.Object
        );
        module.Types.Add(auraModule);

        var importedNamespaces = CollectImportedNamespaces(ast);

        // v3: Pass 1 - create type stubs (class/struct/trait/window)
        var userTypes = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        var astTypeNodes = new Dictionary<object, TypeDefinition>(ReferenceEqualityComparer.Instance);
        var windowNodes = new List<object>();

        // v4: Runtime support types (built-ins)
        CreateRuntimeRoomType(module, auraModule, userTypes);

        // v4: Emit AuraGlobal runtime type (handles, builder registration)
        AuraRuntimeEmitter.Emit(module, auraModule, userTypes);

        // v5: Emit Aura builder types
        AuraRuntimeEmitter.EmitIBuilder(module, auraModule, userTypes);
        AuraRuntimeEmitter.EmitVoidBuilder(module, auraModule, userTypes);
        AuraRuntimeEmitter.EmitCLRConstructorArgBuilder(module, auraModule, userTypes);
        AuraRuntimeEmitter.EmitCLRExternalTypeBuilder(module, auraModule, userTypes);

        CreateTypeStubs(module, auraModule, ast, userTypes, astTypeNodes, windowNodes);

        // v3: Pass 1b-extra - wire inheritance and add struct default ctors
        WireInheritanceAndStructCtors(module, ast, userTypes);

        // v3: Pass 1b - emit best-effort fields/properties for classes/structs (optional but helps this/window).
        EmitBestEffortFieldsAndProperties(module, astTypeNodes, importedNamespaces, userTypes);

        // v3: Pass 1c - attach window interfaces to their target classes (metadata).
        AttachWindowsToTargets(module, windowNodes, userTypes);

        // v3: Pass 1d - create method stubs (top-level and member methods)
        var topLevelMethodsByName = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        var fnNodeToMethod = new Dictionary<object, MethodDefinition>(ReferenceEqualityComparer.Instance);

        // Top-level
        foreach (var item in ast.Items)
        {
            if (item is FunctionDeclNode fn)
            {
                var m = CreateMethodStub(module, owner: auraModule, fn, importedNamespaces, userTypes, isInstance: false);
                if (m is null) continue;

                fnNodeToMethod[fn] = m;
                if (!topLevelMethodsByName.TryAdd(m.Name, m))
                {
                    _diags.Add(new CodeGenDiagnostic(fn.Span, "CG1002", CodeGenSeverity.Warning,
                        Msg.Diag("CG1002", m.Name)));
                }
            }
        }

        // Member methods: scan each type AST node members for FunctionDeclNode (best-effort)
        foreach (var kv in astTypeNodes)
        {
            var typeNode = kv.Key;
            var ownerType = kv.Value;

            var typeNodeName = typeNode.GetType().Name;
            if (typeNodeName is not "ClassDeclNode" and not "StructDeclNode")
                continue; // v3 emits method bodies only for class/struct

            var members = AstReflection.TryGetMembers(typeNode);
            if (members is null) continue;

            foreach (var mem in members)
            {
                if (mem is null) continue;

                // Most likely: method member is FunctionDeclNode.
                if (mem is FunctionDeclNode mfn)
                {
                    var mm = CreateMethodStub(module, owner: ownerType, mfn, importedNamespaces, userTypes, isInstance: true);
                    if (mm is null) continue;
                    fnNodeToMethod[mfn] = mm;
                }
                else if (mem is OperatorDeclNode opDecl)
                {
                    var mm = CreateOperatorMethodStub(module, owner: ownerType, opDecl, importedNamespaces, userTypes);
                    if (mm is null) continue;
                    fnNodeToMethod[opDecl] = mm;
                    continue;
                }
                else
                {
                    // Some ASTs may wrap function nodes as MethodDeclNode containing a FunctionDeclNode-like property.
                    var inner = AstReflection.TryGetPropertyValue(mem, "Function", "Decl", "Method", "Signature");
                    if (inner is FunctionDeclNode innerFn)
                    {
                        var mm = CreateMethodStub(module, owner: ownerType, innerFn, importedNamespaces, userTypes, isInstance: true);
                        if (mm is null) continue;
                        fnNodeToMethod[innerFn] = mm;
                    }
                }
            }
        }

        // v3: Pass 2 - emit bodies
        // Top-level
        foreach (var item in ast.Items)
        {
            if (item is FunctionDeclNode fn && fnNodeToMethod.TryGetValue(fn, out var method))
            {
                var emitter = new CecilEmitter(module, auraModule, method, importedNamespaces, topLevelMethodsByName, userTypes, _diags, debugDocument: debugDoc);
                emitter.EmitFunctionBody(fn);
            }
        }

        // Member methods
        foreach (var kv in fnNodeToMethod)
        {
            if (kv.Key is OperatorDeclNode opD)
            {
                var method = kv.Value;
                if (method.DeclaringType == auraModule) continue;

                // Create a synthetic FunctionDeclNode wrapper for the emitter
                var synthFn = new FunctionDeclNode(
                    opD.Span, opD.Attributes, opD.Visibility,
                    Array.Empty<FunctionModifier>(),
                    new NameNode(opD.Span, method.Name),
                    Array.Empty<TypeParameterNode>(),
                    opD.Parameters, opD.ReturnSpec,
                    Array.Empty<WhereClauseNode>(), opD.Body);

                var emitter = new CecilEmitter(module, auraModule, method, importedNamespaces, topLevelMethodsByName, userTypes, _diags, debugDocument: debugDoc);
                emitter.EmitFunctionBody(synthFn);
                continue;
            }

            if (kv.Key is not FunctionDeclNode fn) continue;
            var method2 = kv.Value;

            if (method2.DeclaringType == auraModule)
                continue; // already emitted as top-level

            var emitter2 = new CecilEmitter(module, auraModule, method2, importedNamespaces, topLevelMethodsByName, userTypes, _diags, debugDocument: debugDoc);
            emitter2.EmitFunctionBody(fn);
        }

        // v4: Process [BuildMe] attributes — emit module initializer registrations
        ProcessBuildMeAttributes(module, ast, auraModule, userTypes);

        var outputDirName = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (outputDirName is not null)
            Directory.CreateDirectory(outputDirName);

        // Write assembly with or without PDB debug symbols
        if (debugDoc is not null)
        {
            asm.Write(outputPath, new WriterParameters
            {
                WriteSymbols = true,
                SymbolWriterProvider = new PortablePdbWriterProvider(),
            });
        }
        else
        {
            asm.Write(outputPath);
        }

        return new CodeGenResult(outputPath, _diags.ToArray());
    }

    private static HashSet<string> CollectImportedNamespaces(CompilationUnitNode ast)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ast.Items)
        {
            if (item is ImportDeclNode imp)
                set.Add(imp.Name.ToString());
        }
        set.Add("System");
        return set;
    }

    // ------------------------------
    // v4 runtime built-ins
    // ------------------------------

    private void CreateRuntimeRoomType(ModuleDefinition module, TypeDefinition auraModule, Dictionary<string, TypeDefinition> userTypes)
    {
        // If user defines a Room type, don't override it.
        if (userTypes.ContainsKey("Room"))
        {
            _diags.Add(new CodeGenDiagnostic(default, "CG7001", CodeGenSeverity.Warning,
                Msg.Diag("CG7001")));
            return;
        }

        // Emit IRoomReceiver interface first (Room depends on it)
        AuraRuntimeEmitter.EmitIRoomReceiver(module, auraModule, userTypes);
        var iRoomReceiver = userTypes["IRoomReceiver"];
        var onMessageMethod = iRoomReceiver.Methods.First(m => m.Name == "OnMessage");

        var td = new TypeDefinition("", "Room",
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        // private string _name;
        var nameField = new FieldDefinition("_name", FieldAttributes.Private, module.TypeSystem.String);
        td.Fields.Add(nameField);

        // private List<object> _objects;
        var listObjType = module.ImportReference(typeof(List<object>));
        var objectsField = new FieldDefinition("_objects", FieldAttributes.Private, listObjType);
        td.Fields.Add(objectsField);

        // private static Dictionary<string, Room> _rooms (global room registry)
        var dictStrRoomType = module.ImportReference(typeof(Dictionary<string, object>));
        var roomsRegistryField = new FieldDefinition("_rooms",
            FieldAttributes.Private | FieldAttributes.Static,
            dictStrRoomType);
        td.Fields.Add(roomsRegistryField);

        // static .cctor: _rooms = new Dictionary<string, object>()
        var roomCctor = new MethodDefinition(".cctor",
            MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Static,
            module.TypeSystem.Void);
        td.Methods.Add(roomCctor);
        {
            var il = roomCctor.Body.GetILProcessor();
            var dictCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Newobj, dictCtor));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stsfld, roomsRegistryField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
        }

        // public .ctor(string name)
        var listObjCtor = module.ImportReference(typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
        td.Methods.Add(ctor);

        {
            var il = ctor.Body.GetILProcessor();
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Call, objCtor));
            // this._name = name
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_1));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stfld, nameField));
            // this._objects = new List<object>()
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Newobj, listObjCtor));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stfld, objectsField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
        }

        // public static Room getRoom(string name)
        // if (_rooms.TryGetValue(name, out var r)) return (Room)r; else { var room = new Room(name); _rooms[name] = room; return room; }
        var getRoom = new MethodDefinition("getRoom",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            td);
        getRoom.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
        td.Methods.Add(getRoom);
        {
            var il = getRoom.Body.GetILProcessor();
            getRoom.Body.InitLocals = true;
            getRoom.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Object)); // loc0: outVal
            getRoom.Body.Variables.Add(new VariableDefinition(td));                        // loc1: newRoom

            var dictTryGet = module.ImportReference(typeof(Dictionary<string, object>).GetMethod("TryGetValue", new[] { typeof(string), typeof(object).MakeByRefType() })!);
            var dictSetItem = module.ImportReference(typeof(Dictionary<string, object>).GetMethod("set_Item", new[] { typeof(string), typeof(object) })!);

            // if (_rooms.TryGetValue(name, out loc0)) return (Room)loc0;
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldsfld, roomsRegistryField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloca_S, (byte)0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, dictTryGet));
            var createNewLabel = il.Create(Mono.Cecil.Cil.OpCodes.Nop);
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Brfalse, createNewLabel));
            // found: return (Room)loc0
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Castclass, td));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
            // not found: create, store, return
            il.Append(createNewLabel);
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Newobj, ctor));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stloc_1));
            // _rooms[name] = newRoom
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldsfld, roomsRegistryField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_1));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, dictSetItem));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_1));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
        }

        // public static Room createRoom(string name) — creates and registers, returns the room
        var createRoom = new MethodDefinition("createRoom",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            td);
        createRoom.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
        td.Methods.Add(createRoom);
        {
            var il = createRoom.Body.GetILProcessor();
            createRoom.Body.InitLocals = true;
            createRoom.Body.Variables.Add(new VariableDefinition(td)); // loc0: newRoom
            var dictSetItem = module.ImportReference(typeof(Dictionary<string, object>).GetMethod("set_Item", new[] { typeof(string), typeof(object) })!);
            // var room = new Room(name)
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Newobj, ctor));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stloc_0));
            // _rooms[name] = room
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldsfld, roomsRegistryField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, dictSetItem));
            // return room
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
        }

        // public void addObject(object obj) — adds to _objects list
        var listAddMethod = module.ImportReference(typeof(List<object>).GetMethod("Add", new[] { typeof(object) })!);
        var addObject = new MethodDefinition("addObject",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        addObject.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, module.TypeSystem.Object));
        td.Methods.Add(addObject);
        {
            var il = addObject.Body.GetILProcessor();
            // this._objects.Add(obj)
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldfld, objectsField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_1));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, listAddMethod));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
        }

        // public void sendMessage(string message, object args)
        // Iterates _objects, casts each to IRoomReceiver, calls OnMessage
        var sendMessage = new MethodDefinition("sendMessage",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        sendMessage.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, module.TypeSystem.String));
        sendMessage.Parameters.Add(new ParameterDefinition("args", ParameterAttributes.None, module.TypeSystem.Object));
        td.Methods.Add(sendMessage);
        {
            // for (int i = 0; i < _objects.Count; i++) {
            //   var obj = _objects[i];
            //   if (obj is IRoomReceiver r) r.OnMessage(message, args);
            // }
            var il = sendMessage.Body.GetILProcessor();
            sendMessage.Body.InitLocals = true;
            sendMessage.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));    // loc0: i
            sendMessage.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Object));    // loc1: obj
            sendMessage.Body.Variables.Add(new VariableDefinition(iRoomReceiver));               // loc2: receiver

            var listGetCount = module.ImportReference(typeof(List<object>).GetProperty("Count")!.GetGetMethod()!);
            var listGetItem = module.ImportReference(typeof(List<object>).GetMethod("get_Item", new[] { typeof(int) })!);
            var onMsgRef = module.ImportReference(onMessageMethod);

            // i = 0
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stloc_0));

            // goto condCheck
            var condCheck = il.Create(Mono.Cecil.Cil.OpCodes.Nop);
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Br, condCheck));

            // loopBody:
            var loopBody = il.Create(Mono.Cecil.Cil.OpCodes.Nop);
            il.Append(loopBody);

            // obj = _objects[i]
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldfld, objectsField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, listGetItem));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stloc_1));

            // receiver = obj as IRoomReceiver
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_1));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Isinst, iRoomReceiver));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stloc_2));

            // if (receiver != null) receiver.OnMessage(message, args)
            var skipCall = il.Create(Mono.Cecil.Cil.OpCodes.Nop);
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_2));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Brfalse, skipCall));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_2));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_1)); // message
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_2)); // args
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, onMsgRef));
            il.Append(skipCall);

            // i++
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_1));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Add));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Stloc_0));

            // condCheck: if (i < _objects.Count) goto loopBody
            il.Append(condCheck);
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldfld, objectsField));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, listGetCount));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Blt, loopBody));

            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
        }

        auraModule.NestedTypes.Add(td);
        userTypes["Room"] = td;
    }

    private void WireInheritanceAndStructCtors(
        ModuleDefinition module,
        CompilationUnitNode ast,
        Dictionary<string, TypeDefinition> userTypes)
    {
        foreach (var item in ast.Items)
        {
            var kind = item.GetType().Name;
            if (kind is not "ClassDeclNode" and not "StructDeclNode") continue;

            var typeName = AstReflection.TryGetNameText(item);
            if (typeName is null || !userTypes.TryGetValue(typeName, out var td)) continue;

            // Wire base class for class declarations
            if (kind == "ClassDeclNode" && item is ClassDeclNode classNode && classNode.BaseTypes.Count > 0)
            {
                foreach (var baseTypeNode in classNode.BaseTypes)
                {
                    var baseName = baseTypeNode switch
                    {
                        NamedTypeNode ntn => ntn.Name.ToString(),
                        _ => null
                    };
                    if (baseName is not null && userTypes.TryGetValue(baseName, out var baseTd))
                    {
                        // Set base type to the Aura user-defined base class
                        td.BaseType = module.ImportReference(baseTd);

                        // Update the auto-generated .ctor to call base class .ctor instead of object::.ctor
                        var subCtor = td.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
                        var baseCtor = baseTd.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
                        if (subCtor is not null && baseCtor is not null)
                        {
                            subCtor.Body.Instructions.Clear();
                            var il = subCtor.Body.GetILProcessor();
                            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
                            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Call, module.ImportReference(baseCtor)));
                            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
                        }

                        break; // single inheritance
                    }
                }
            }

            // Add default .ctor for structs (needed for newobj)
            if (kind == "StructDeclNode" && !td.Methods.Any(m => m.Name == ".ctor"))
            {
                var ctor = new MethodDefinition(".ctor",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    module.TypeSystem.Void);
                td.Methods.Add(ctor);
                var il = ctor.Body.GetILProcessor();
                il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
            }
        }
    }

    private void CreateTypeStubs(
        ModuleDefinition module,
        TypeDefinition auraModule,
        CompilationUnitNode ast,
        Dictionary<string, TypeDefinition> userTypes,
        Dictionary<object, TypeDefinition> astTypeNodes,
        List<object> windowNodes)
    {
        foreach (var item in ast.Items)
        {
            var kind = item.GetType().Name;

            if (kind is "ClassDeclNode" or "StructDeclNode" or "TraitDeclNode" or "InterfaceDeclNode" or "WindowDeclNode")
            {
                var name = AstReflection.TryGetNameText(item) ?? $"__AnonType{userTypes.Count}";
                var isPublic = AstReflection.TryIsPublicVisibility(item, out var pub) && pub;

                // Use Nested visibility because these types are added as auraModule.NestedTypes
                var attrs = isPublic ? TypeAttributes.NestedPublic : TypeAttributes.NestedAssembly;
                TypeDefinition td;

                if (kind is "TraitDeclNode" or "InterfaceDeclNode" or "WindowDeclNode")
                {
                    td = new TypeDefinition("", name, attrs | TypeAttributes.Interface | TypeAttributes.Abstract, module.TypeSystem.Object);
                }
                else if (kind == "StructDeclNode")
                {
                    td = new TypeDefinition("", name,
                        attrs | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit,
                        module.ImportReference(typeof(ValueType)));
                }
                else // class
                {
                    td = new TypeDefinition("", name,
                        attrs | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
                        module.TypeSystem.Object);

                    // Emit a default .ctor for runtime friendliness (Aura forbids user-defined constructors, not compiler-generated).
                    var ctor = new MethodDefinition(".ctor",
                        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                        module.TypeSystem.Void);
                    td.Methods.Add(ctor);
                    var il = ctor.Body.GetILProcessor();
                    il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
                    var objCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
                    il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Call, objCtor));
                    il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
                }

                auraModule.NestedTypes.Add(td);
                userTypes[name] = td;
                astTypeNodes[item] = td;

                if (kind == "WindowDeclNode")
                    windowNodes.Add(item);
            }
        }
    }

    private void EmitBestEffortFieldsAndProperties(
        ModuleDefinition module,
        Dictionary<object, TypeDefinition> astTypeNodes,
        HashSet<string> importedNamespaces,
        Dictionary<string, TypeDefinition> userTypes)
    {
        foreach (var kv in astTypeNodes)
        {
            var typeNode = kv.Key;
            var td = kv.Value;

            var kind = typeNode.GetType().Name;
            if (kind is not "ClassDeclNode" and not "StructDeclNode")
                continue;

            var members = AstReflection.TryGetMembers(typeNode);
            if (members is null) continue;

            foreach (var mem in members)
            {
                if (mem is null) continue;
                var mk = mem.GetType().Name;

                // Field heuristic
                if (mk.Contains("Field", StringComparison.OrdinalIgnoreCase) ||
                    mk is "VarFieldDeclNode" or "FieldDeclNode")
                {
                    var name = AstReflection.TryGetNameText(mem);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Determine type node
                    var typeVal = AstReflection.TryGetTypeNode(mem, "Type", "FieldType", "DeclType");
                    if (typeVal is TypeNode tn)
                    {
                        var ft = CecilEmitter.ResolveType(module, tn, importedNamespaces, userTypes, _diags, default);
                        // Aura forbids public fields; force private.
                        var fd = new FieldDefinition(name, FieldAttributes.Private, ft);
                        td.Fields.Add(fd);
                    }
                    continue;
                }

                // Property heuristic
                if (mk.Contains("Property", StringComparison.OrdinalIgnoreCase) ||
                    mk is "PropertyDeclNode")
                {
                    var name = AstReflection.TryGetNameText(mem);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var typeVal = AstReflection.TryGetTypeNode(mem, "Type", "PropertyType", "DeclType");
                    if (typeVal is not TypeNode tn) continue;

                    var pt = CecilEmitter.ResolveType(module, tn, importedNamespaces, userTypes, _diags, default);

                    // Create backing field
                    var backing = new FieldDefinition($"<{name}>k__BackingField", FieldAttributes.Private, pt);
                    td.Fields.Add(backing);

                    // Getter
                    var get = new MethodDefinition($"get_{name}",
                        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                        MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                        pt);

                    var gil = get.Body.GetILProcessor();
                    gil.Append(gil.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
                    gil.Append(gil.Create(Mono.Cecil.Cil.OpCodes.Ldfld, backing));
                    gil.Append(gil.Create(Mono.Cecil.Cil.OpCodes.Ret));
                    td.Methods.Add(get);

                    // Setter (best-effort: if AST indicates "set" exists; else omit)
                    bool hasSet = AstReflection.TryGetPropertyValue(mem, "Set", "Setter", "SetBody") is not null;
                    // If we cannot tell, default to having a setter for now (makes interop easier).
                    if (!hasSet)
                        hasSet = true;

                    MethodDefinition? set = null;
                    if (hasSet)
                    {
                        set = new MethodDefinition($"set_{name}",
                            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                            MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                            module.TypeSystem.Void);
                        set.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, pt));
                        var sil = set.Body.GetILProcessor();
                        sil.Append(sil.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
                        sil.Append(sil.Create(Mono.Cecil.Cil.OpCodes.Ldarg_1));
                        sil.Append(sil.Create(Mono.Cecil.Cil.OpCodes.Stfld, backing));
                        sil.Append(sil.Create(Mono.Cecil.Cil.OpCodes.Ret));
                        td.Methods.Add(set);
                    }

                    var prop = new PropertyDefinition(name, PropertyAttributes.None, pt)
                    {
                        GetMethod = get,
                        SetMethod = set
                    };
                    td.Properties.Add(prop);

                    continue;
                }
            }
        }
    }

    private void AttachWindowsToTargets(
        ModuleDefinition module,
        List<object> windowNodes,
        Dictionary<string, TypeDefinition> userTypes)
    {
        foreach (var wn in windowNodes)
        {
            var windowName = AstReflection.TryGetNameText(wn);
            if (string.IsNullOrWhiteSpace(windowName)) continue;
            if (!userTypes.TryGetValue(windowName, out var windowTd)) continue;

            // Try to read window target/base type:
            // common property names could be BaseType/Target/Of/From/TargetType.
            var targetVal = AstReflection.TryGetPropertyValue(wn, "Target", "TargetType", "BaseType", "Of", "From");
            if (targetVal is null) continue;

            // If it's a TypeNode: resolve its name (NamedTypeNode => name.ToString())
            string? targetName = null;
            if (targetVal is TypeNode tnode)
                targetName = tnode.ToString();
            else
                targetName = targetVal.ToString();

            if (string.IsNullOrWhiteSpace(targetName))
                continue;

            // Heuristic: if targetName contains "<" generics etc, strip.
            var simple = targetName.Split('<')[0].Trim();

            if (!userTypes.TryGetValue(simple, out var targetTd))
            {
                // Could be a CLR type; ignore for v3.
                continue;
            }

            // Attach interface
            if (!targetTd.Interfaces.Any(ii => ii.InterfaceType.FullName == windowTd.FullName))
                targetTd.Interfaces.Add(new InterfaceImplementation(windowTd));
        }
    }

    private MethodDefinition? CreateMethodStub(
        ModuleDefinition module,
        TypeDefinition owner,
        FunctionDeclNode fn,
        HashSet<string> importedNamespaces,
        IReadOnlyDictionary<string, TypeDefinition> userTypes,
        bool isInstance)
    {
        // v5: Support generic methods (method-level type parameters).

        // Visibility
        var isPublic = fn.Visibility.ToString().Equals("Public", StringComparison.OrdinalIgnoreCase);

        var attrs = (isPublic ? MethodAttributes.Public : MethodAttributes.Private) | MethodAttributes.HideBySig;

        if (!isInstance)
            attrs |= MethodAttributes.Static;
        else
            attrs |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;

        var method = new MethodDefinition(fn.Name.Text, attrs, module.TypeSystem.Void);



// Build generic context: type + method generic parameters.
var genericContext = new Dictionary<string, TypeReference>(StringComparer.Ordinal);
foreach (var gp in owner.GenericParameters)
    genericContext[gp.Name] = gp;

// Method generic parameters: fn<TypeParams...>
if (fn.TypeParams.Count > 0)
{
    var idx = 0;
    foreach (var tp in fn.TypeParams)
    {
        var name = TryGetTypeParamName(tp) ?? $"T{idx++}";
        // Avoid duplicates
        if (genericContext.ContainsKey(name))
            name = $"{name}_{idx++}";

        var gp = new GenericParameter(name, method);
        method.GenericParameters.Add(gp);
        genericContext[name] = gp;
    }

    // NOTE: v5 currently ignores 'where' constraints (best-effort backend).
    if (fn.WhereClauses.Count > 0)
    {
        _diags.Add(new CodeGenDiagnostic(fn.Span, "CG1103", CodeGenSeverity.Warning,
            Msg.Diag("CG1103", fn.Name.Text)));
    }
}

// Resolve return type (may reference method generic parameters).
method.ReturnType = ResolveReturnType(module, fn.ReturnSpec, importedNamespaces, userTypes, genericContext, fn.Span);

        // Async functions are exposed as Task / Task<T> at the CLR level.
        // The Aura source-level return type (if any) is treated as the *result* type.
        if (fn.Modifiers.Contains(FunctionModifier.Async))
        {
            method.ReturnType = WrapAsyncReturnType(module, method.ReturnType);

            // Add [AsyncStateMachine] attribute for CLR tooling/debugger compatibility.
            // We use a synthetic type reference since we don't actually generate the state machine class;
            // the lowering has already desugared async into ContinueWith chains.
            var asyncAttrType = module.ImportReference(
                typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute));
            var asyncAttrCtor = module.ImportReference(
                typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute)
                    .GetConstructor(new[] { typeof(Type) })!);
            var asyncAttr = new Mono.Cecil.CustomAttribute(asyncAttrCtor);
            // Point to the owning type as the "state machine" (best approximation without a real SM class)
            asyncAttr.ConstructorArguments.Add(
                new Mono.Cecil.CustomAttributeArgument(
                    module.ImportReference(typeof(Type)),
                    owner));
            method.CustomAttributes.Add(asyncAttr);
        }


        foreach (var p in fn.Parameters)
        {
            if (p.Type is null)
            {
                _diags.Add(new CodeGenDiagnostic(p.Span, "CG1101", CodeGenSeverity.Error,
                    Msg.Diag("CG1101", p.Name.Text)));
                method.Parameters.Add(new ParameterDefinition(p.Name.Text, ParameterAttributes.None, module.TypeSystem.Object));
            }
            else
            {
                var pt = CecilEmitter.ResolveType(module, p.Type, importedNamespaces, userTypes, genericContext, _diags, p.Span);
                method.Parameters.Add(new ParameterDefinition(p.Name.Text, ParameterAttributes.None, pt));
            }
        }

        owner.Methods.Add(method);
        return method;
    }

private static string? TryGetTypeParamName(TypeParameterNode tp) => tp.Name.Text;

private static TypeReference WrapAsyncReturnType(ModuleDefinition module, TypeReference userReturnType)
{
    // void => Task
    if (userReturnType.FullName == module.TypeSystem.Void.FullName)
        return module.ImportReference(typeof(System.Threading.Tasks.Task));

    // T => Task<T>
    var taskOpen = module.ImportReference(typeof(System.Threading.Tasks.Task<>));
    var gi = new GenericInstanceType(taskOpen);
    gi.GenericArguments.Add(userReturnType);
    return gi;
}

    private static readonly Dictionary<string, string> OperatorClrNames = new(StringComparer.Ordinal)
    {
        ["+"] = "op_Addition",
        ["-"] = "op_Subtraction",
        ["*"] = "op_Multiply",
        ["/"] = "op_Division",
        ["%"] = "op_Modulus",
        ["=="] = "op_Equality",
        ["!="] = "op_Inequality",
        ["<"] = "op_LessThan",
        [">"] = "op_GreaterThan",
        ["<="] = "op_LessThanOrEqual",
        [">="] = "op_GreaterThanOrEqual",
    };

    private MethodDefinition? CreateOperatorMethodStub(
        ModuleDefinition module,
        TypeDefinition owner,
        OperatorDeclNode opDecl,
        HashSet<string> importedNamespaces,
        IReadOnlyDictionary<string, TypeDefinition> userTypes)
    {
        if (!OperatorClrNames.TryGetValue(opDecl.Op, out var clrName))
        {
            _diags.Add(new CodeGenDiagnostic(opDecl.Span, "CG6001", CodeGenSeverity.Error,
                Msg.Diag("CG6001", opDecl.Op)));
            return null;
        }

        var attrs = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName;
        var method = new MethodDefinition(clrName, attrs, module.TypeSystem.Void);

        // Return type
        method.ReturnType = ResolveReturnType(module, opDecl.ReturnSpec, importedNamespaces, userTypes, null, opDecl.Span);

        // Parameters
        foreach (var p in opDecl.Parameters)
        {
            if (p.Type is null)
            {
                _diags.Add(new CodeGenDiagnostic(p.Span, "CG6002", CodeGenSeverity.Error,
                    Msg.Diag("CG6002", p.Name.Text)));
                method.Parameters.Add(new ParameterDefinition(p.Name.Text, ParameterAttributes.None, module.TypeSystem.Object));
            }
            else
            {
                var pt = CecilEmitter.ResolveType(module, p.Type, importedNamespaces, userTypes, null, _diags, p.Span);
                method.Parameters.Add(new ParameterDefinition(p.Name.Text, ParameterAttributes.None, pt));
            }
        }

        owner.Methods.Add(method);
        return method;
    }

    private TypeReference ResolveReturnType(
        ModuleDefinition module,
        ReturnSpecNode? ret,
        HashSet<string> importedNamespaces,
        IReadOnlyDictionary<string, TypeDefinition> userTypes,
        IReadOnlyDictionary<string, TypeReference>? genericContext,
        SourceSpan span)
    {
        if (ret is null)
            return module.TypeSystem.Void;

        if (ret is ReturnTypeSpecNode rts)
            return CecilEmitter.ResolveType(module, rts.ReturnType, importedNamespaces, userTypes, genericContext, _diags, span);

        if (ret is StateSpecNode)
            return module.TypeSystem.Void;

        _diags.Add(new CodeGenDiagnostic(span, "CG1102", CodeGenSeverity.Warning, Msg.Diag("CG1102")));
        return module.TypeSystem.Void;
    }

    private void ProcessBuildMeAttributes(
        ModuleDefinition module,
        CompilationUnitNode ast,
        TypeDefinition auraModule,
        Dictionary<string, TypeDefinition> userTypes)
    {
        // Collect all [BuildMe] registrations
        var registrations = new List<(TypeDefinition TargetType, string BuilderName, string Name)>();

        foreach (var item in ast.Items)
        {
            if (item is not ClassDeclNode cd) continue;

            foreach (var attr in cd.Attributes)
            {
                foreach (var a in attr.Attributes)
                {
                    if (a.Name.ToString() != "BuildMe") continue;

                    if (!userTypes.TryGetValue(cd.Name.Text, out var targetType))
                        continue;

                    // Extract builder and name from attribute args
                    string? builderName = null;
                    string? regName = null;

                    foreach (var arg in a.Args)
                    {
                        if (arg is AttributeNamedArgNode namedArg)
                        {
                            if (namedArg.Name.Text == "builder" && namedArg.Value is NameExprNode ne)
                                builderName = ne.Name.Text;
                            else if (namedArg.Name.Text == "name" && namedArg.Value is LiteralExprNode lit)
                                regName = lit.RawText.Trim('"');
                        }
                    }

                    if (builderName is not null)
                    {
                        registrations.Add((targetType, builderName, regName ?? cd.Name.Text));
                    }
                    else
                    {
                        _diags.Add(new CodeGenDiagnostic(cd.Span, "CG7101", CodeGenSeverity.Warning,
                            Msg.Diag("CG7101", cd.Name.Text)));
                    }
                }
            }
        }

        if (registrations.Count == 0) return;

        // Find or create module .cctor on auraModule
        var cctor = auraModule.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor is null)
        {
            cctor = new MethodDefinition(".cctor",
                MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Static,
                module.TypeSystem.Void);
            auraModule.Methods.Add(cctor);
            var il0 = cctor.Body.GetILProcessor();
            il0.Append(il0.Create(Mono.Cecil.Cil.OpCodes.Ret));
        }

        // Insert registration calls before the final Ret
        var il = cctor.Body.GetILProcessor();
        var retInstr = cctor.Body.Instructions.Last();

        // Get AuraGlobal.RegisterBuilder reference
        if (!userTypes.TryGetValue("Global", out var globalType)) return;
        var registerBuilderMethod = globalType.Methods.FirstOrDefault(m => m.Name == "RegisterBuilder");
        if (registerBuilderMethod is null) return;
        var registerBuilderRef = module.ImportReference(registerBuilderMethod);

        var getTypeFromHandle = module.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) })!);

        foreach (var (targetType, builderName, name) in registrations)
        {
            if (!userTypes.TryGetValue(builderName, out var builderType))
            {
                _diags.Add(new CodeGenDiagnostic(default, "CG7102", CodeGenSeverity.Warning,
                    Msg.Diag("CG7102", builderName)));
                continue;
            }

            // AuraGlobal.RegisterBuilder(typeof(Target), typeof(Builder), "name")
            // ldtoken Target
            il.InsertBefore(retInstr, il.Create(Mono.Cecil.Cil.OpCodes.Ldtoken, targetType));
            il.InsertBefore(retInstr, il.Create(Mono.Cecil.Cil.OpCodes.Call, getTypeFromHandle));
            // ldtoken Builder
            il.InsertBefore(retInstr, il.Create(Mono.Cecil.Cil.OpCodes.Ldtoken, builderType));
            il.InsertBefore(retInstr, il.Create(Mono.Cecil.Cil.OpCodes.Call, getTypeFromHandle));
            // ldstr "name"
            il.InsertBefore(retInstr, il.Create(Mono.Cecil.Cil.OpCodes.Ldstr, name));
            // call RegisterBuilder
            il.InsertBefore(retInstr, il.Create(Mono.Cecil.Cil.OpCodes.Call, registerBuilderRef));

            _diags.Add(new CodeGenDiagnostic(default, "CG7100", CodeGenSeverity.Info,
                Msg.Diag("CG7100", targetType.Name, builderName)));
        }
    }
}