using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using MonoMod.RuntimeDetour.HookGen;

internal static class Program
{
    private static int Main()
    {
        try
        {
            VerifyPreventInlining();
            VerifyForwardedHooks();
            RejectFacade("reordered arguments", fixture =>
            {
                var instructions = fixture.Ordered.Body.Instructions;
                instructions[0].OpCode = OpCodes.Ldarg_1;
                instructions[1].OpCode = OpCodes.Ldarg_0;
            });
            RejectFacade("transformed result", fixture =>
            {
                var instructions = fixture.CurrentLanguage.Body.Instructions;
                instructions.Insert(instructions.Count - 1, Instruction.Create(OpCodes.Ldc_I4_1));
                instructions.Insert(instructions.Count - 1, Instruction.Create(OpCodes.Add));
            });
            RejectFacade("recursive alias", fixture =>
                fixture.CurrentLanguage.Body.Instructions[0].Operand = fixture.CurrentLanguage);
            RejectFacade("instance facade", fixture =>
            {
                fixture.CurrentLanguage.IsStatic = false;
                fixture.CurrentLanguage.HasThis = true;
            });
            RejectFacade("instance target", fixture =>
                ((MethodReference)fixture.CurrentLanguage.Body.Instructions[0].Operand).HasThis = true);
            Console.WriteLine("PASS: forwarded targets, legacy signatures, subscription pairs, ordinary hooks, and 5 invalid facades.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyForwardedHooks()
    {
        using var fixture = new Fixture();
        fixture.CurrentLanguage.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
        using var output = fixture.Generate();
        var language = output.GetType("On.Legacy.Language");

        foreach (var method in fixture.Language.Methods)
        {
            var target = fixture.TargetLanguage.Methods.Single(candidate => candidate.Name == method.Name);
            VerifyEndpoints(output, "Legacy.Language", method.Name, target);
        }

        foreach (string prefix in new[] { "orig_", "hook_" })
        {
            var current = language.NestedTypes.Single(type => type.Name == prefix + "CurrentLanguage")
                .Methods.Single(method => method.Name == "Invoke");
            Require(IsLegacyEnum(current.ReturnType), prefix + "CurrentLanguage must return the legacy enum");

            var change = language.NestedTypes.Single(type => type.Name == prefix + "SwitchLanguage")
                .Methods.Single(method => method.Name == "Invoke");
            Require(IsLegacyEnum(change.Parameters.Last().ParameterType), prefix + "SwitchLanguage must accept the legacy enum");
        }

        VerifyEndpoints(output, "Legacy.Ordinary", "Proxy", fixture.Ordinary);
        Require(output.GetType("On.Modding.ForwardedHooksAttribute") != null,
            "the marker attribute's ordinary constructor must not be treated as a forwarding facade");
    }

    private static bool IsLegacyEnum(TypeReference type) =>
        type.FullName == "Legacy.LanguageCode" && type.Scope.Name == "ForwardingFacade";

    private static void VerifyEndpoints(ModuleDefinition output, string typeName, string eventName, MethodDefinition target)
    {
        foreach (string hookNamespace in new[] { "On", "IL" })
        {
            var hookType = output.GetType(hookNamespace + "." + typeName);
            var hookEvent = hookType.Events.Single(candidate => candidate.Name == eventName);
            string delegateName = "On." + typeName + "/hook_" + eventName;
            VerifyEndpoint(hookEvent.AddMethod, target, hookNamespace == "On" ? "Add" : "Modify", delegateName);
            VerifyEndpoint(hookEvent.RemoveMethod, target, hookNamespace == "On" ? "Remove" : "Unmodify", delegateName);
            Require(hookEvent.AddMethod.Parameters.Single().ParameterType.FullName == hookEvent.EventType.FullName,
                hookType.FullName + ": add must use the event's delegate type");
            Require(hookEvent.RemoveMethod.Parameters.Single().ParameterType.FullName == hookEvent.EventType.FullName,
                hookType.FullName + ": remove must use the event's delegate type");
        }
    }

    private static void VerifyEndpoint(MethodDefinition endpoint, MethodDefinition target, string operation, string delegateName)
    {
        var token = (MethodReference)endpoint.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldtoken).Operand;
        Require(token.FullName == target.FullName && token.DeclaringType.Scope.Name == target.Module.Assembly.Name.Name,
            endpoint.FullName + ": must address the intended method in the intended assembly");
        var managerCall = endpoint.Body.Instructions
            .Select(instruction => instruction.Operand as MethodReference)
            .Single(method => method?.DeclaringType.FullName == "MonoMod.RuntimeDetour.HookGen.HookEndpointManager");
        Require(managerCall.Name == operation, endpoint.FullName + ": wrong subscription operation");
        var hookDelegate = ((GenericInstanceMethod)managerCall).GenericArguments.Single();
        Require(hookDelegate.FullName == delegateName, endpoint.FullName + ": subscription must retain the legacy delegate");
    }

    private static void RejectFacade(string description, Action<Fixture> invalidate)
    {
        using var fixture = new Fixture();
        invalidate(fixture);
        try
        {
            using var output = fixture.Generate();
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Forwarded hook method '", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("Accepted invalid facade: " + description);
    }

    private static void VerifyPreventInlining()
    {
        using var module = ModuleDefinition.CreateModule("InlineRegression", ModuleKind.Dll);
        var type = new TypeDefinition("Test", "Hooks", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("Target", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        method.ImplAttributes |= MethodImplAttributes.AggressiveInlining;
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        using var modder = new MonoModder { Module = module, PreventInline = true };
        modder.DefaultPostProcessType(type);
        Require(method.NoInlining, "Patched method must prevent inlining");
        Require((method.ImplAttributes & MethodImplAttributes.AggressiveInlining) == 0, "AggressiveInlining must be cleared");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly FixtureResolver resolver = new FixtureResolver();
        private readonly ModuleDefinition source;
        private readonly ModuleDefinition target;
        private MemoryStream generatedImage;

        public TypeDefinition Language { get; }
        public TypeDefinition TargetLanguage { get; }
        public MethodDefinition CurrentLanguage { get; }
        public MethodDefinition Ordered { get; }
        public MethodDefinition Ordinary { get; }

        public Fixture()
        {
            resolver.AddSearchDirectory(AppDomain.CurrentDomain.BaseDirectory);
            source = CreateModule("ForwardingFacade");
            target = CreateModule("ForwardingTarget");
            source.ImportReference(typeof(EditorBrowsableState));
            var legacyEnum = AddEnum(source, "Legacy");
            var currentEnum = AddEnum(target, "Current");
            Language = AddClass(source, "Legacy", "Language");
            TargetLanguage = AddClass(target, "Current", "Language");

            var marker = AddClass(source, "Modding", "ForwardedHooksAttribute");
            marker.Attributes = TypeAttributes.NotPublic | TypeAttributes.Sealed;
            marker.BaseType = source.ImportReference(typeof(Attribute));
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName |
                MethodAttributes.RTSpecialName, source.TypeSystem.Void) { HasThis = true };
            marker.Methods.Add(constructor);
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call,
                new MethodReference(".ctor", source.TypeSystem.Void, marker.BaseType) { HasThis = true }));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            Language.CustomAttributes.Add(new CustomAttribute(constructor));

            CurrentLanguage = Forward("CurrentLanguage", legacyEnum, currentEnum, Array.Empty<TypeReference>(), Array.Empty<TypeReference>());
            Forward("SwitchLanguage", source.TypeSystem.Void, target.TypeSystem.Void, new[] { legacyEnum }, new[] { currentEnum });
            Ordered = Forward("Ordered", source.TypeSystem.Int32, target.TypeSystem.Int32,
                Enumerable.Repeat<TypeReference>(source.TypeSystem.Int32, 6).ToArray(),
                Enumerable.Repeat<TypeReference>(target.TypeSystem.Int32, 6).ToArray());
            var ordinaryType = AddClass(source, "Legacy", "Ordinary");
            Ordinary = AddMethod(ordinaryType, "Proxy", source.TypeSystem.Int32, Array.Empty<TypeReference>());
            var ordinaryTarget = AddMethod(TargetLanguage, "Proxy", target.TypeSystem.Int32, Array.Empty<TypeReference>());
            ordinaryTarget.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            ordinaryTarget.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            Ordinary.Body.Instructions.Add(Instruction.Create(OpCodes.Call, source.ImportReference(ordinaryTarget)));
            Ordinary.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }

        public ModuleDefinition Generate()
        {
            using var modder = new MonoModder { Module = source, AssemblyResolver = resolver };
            modder.DependencyDirs.Add(AppDomain.CurrentDomain.BaseDirectory);
            modder.DependencyCache.Add(target.Assembly.Name.Name, target);
            var generator = new HookGenerator(modder, "MMHOOK_ForwardingFacade.dll") { HookPrivate = true };
            using var generated = generator.OutputModule;
            generator.Generate();
            generatedImage = new MemoryStream();
            generated.Write(generatedImage);
            generatedImage.Position = 0;
            return ModuleDefinition.ReadModule(generatedImage, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        }

        private ModuleDefinition CreateModule(string name)
        {
            var module = ModuleDefinition.CreateModule(name, new ModuleParameters
            {
                Kind = ModuleKind.Dll,
                AssemblyResolver = resolver
            });
            resolver.Register(module.Assembly);
            return module;
        }

        private MethodDefinition Forward(string name, TypeReference oldReturn, TypeReference newReturn,
            TypeReference[] oldArguments, TypeReference[] newArguments)
        {
            var original = AddMethod(TargetLanguage, name, newReturn, newArguments);
            if (newReturn.MetadataType != MetadataType.Void)
                original.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            original.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            var facade = AddMethod(Language, name, oldReturn, oldArguments);
            var shortLoads = new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 };
            for (int index = 0; index < oldArguments.Length; index++)
                facade.Body.Instructions.Add(index < shortLoads.Length
                    ? Instruction.Create(shortLoads[index])
                    : Instruction.Create(index == 4 ? OpCodes.Ldarg_S : OpCodes.Ldarg, facade.Parameters[index]));
            facade.Body.Instructions.Add(Instruction.Create(OpCodes.Call, source.ImportReference(original)));
            facade.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return facade;
        }

        private static TypeDefinition AddClass(ModuleDefinition module, string ns, string name)
        {
            var type = new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Abstract |
                TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);
            return type;
        }

        private static TypeDefinition AddEnum(ModuleDefinition module, string ns)
        {
            var type = new TypeDefinition(ns, "LanguageCode", TypeAttributes.Public | TypeAttributes.Sealed,
                module.ImportReference(typeof(Enum)));
            type.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName |
                FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
            module.Types.Add(type);
            return type;
        }

        private static MethodDefinition AddMethod(TypeDefinition type, string name, TypeReference result, TypeReference[] arguments)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, result);
            foreach (var argument in arguments) method.Parameters.Add(new ParameterDefinition(argument));
            type.Methods.Add(method);
            return method;
        }

        public void Dispose()
        {
            source.Dispose();
            target.Dispose();
            resolver.Dispose();
            generatedImage?.Dispose();
        }
    }

    private sealed class FixtureResolver : DefaultAssemblyResolver
    {
        public void Register(AssemblyDefinition assembly) => RegisterAssembly(assembly);
    }
}
