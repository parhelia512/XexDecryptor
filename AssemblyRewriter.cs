using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace XexDecryptor {    
    public class AssemblyRewriter {
        public const string AssemblyNameMscorlib = "mscorlib, Version=3.5.0.0, Culture=neutral, PublicKeyToken=1c9e259686f921e0";
        public const string AssemblyNameXnaFramework = "Microsoft.Xna.Framework, Version=3.1.0.0, Culture=neutral, PublicKeyToken=51c3bfb2db46012c";

        // The XBox 360 versions of MS assemblies have different versions and public key tokens.
        // We need to find any references to them and fix them to point to the Win32 versions.
        public static readonly Dictionary<string, string> AssemblyNameReplacements = new Dictionary<string, string> {
            // XNA 3.1 references

            {AssemblyNameMscorlib,
             "mscorlib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"},
            {"System, Version=3.5.0.0, Culture=neutral, PublicKeyToken=1c9e259686f921e0",
             "System, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"},
            {"System.Core, Version=3.5.0.0, Culture=neutral, PublicKeyToken=1c9e259686f921e0",
             "System.Core, Version=3.5.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"},
            {"System.Xml.Linq, Version=3.5.0.0, Culture=neutral, PublicKeyToken=1c9e259686f921e0",
             "System.Xml.Linq, Version=3.5.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"},
            {AssemblyNameXnaFramework,
             "Microsoft.Xna.Framework, Version=3.1.0.0, Culture=neutral, PublicKeyToken=6d5c3888ef60e27d"},
            {"Microsoft.Xna.Framework.Game, Version=3.1.0.0, Culture=neutral, PublicKeyToken=51c3bfb2db46012c",
             "Microsoft.Xna.Framework.Game, Version=3.1.0.0, Culture=neutral, PublicKeyToken=6d5c3888ef60e27d"},

            // TODO: Add XNA 4.0 references
        };

        private static void KillCallInstruction (MethodBody body, MethodReference invokeTarget, ref int i, ref int c, bool replaceReturnValue) {
            // Patch out the call instruction.
            int stackEntriesToPop = invokeTarget.Parameters.Count + (invokeTarget.HasThis ? 1 : 0);
            body.Instructions.RemoveAt(i);

            c += stackEntriesToPop;

            int insertionPosition = i;
            while (stackEntriesToPop > 0) {
                body.Instructions.Insert(insertionPosition, Instruction.Create(OpCodes.Pop));
                stackEntriesToPop--;
                insertionPosition += 1;
            }

            if (!replaceReturnValue || (invokeTarget.ReturnType.FullName == "System.Void")) {
                c -= 1;
                i = insertionPosition;
            } else {
                body.Instructions.Insert(insertionPosition, Instruction.Create(OpCodes.Ldnull));
                i = insertionPosition + 1;
            }
        }

        public static void Rewrite (string executablePath) {
            var resolver = new RewriterAssemblyResolver();
            var readerParameters = new ReaderParameters {
                AssemblyResolver = resolver,
                ReadingMode = ReadingMode.Immediate,
                ReadSymbols = false
            };
            AssemblyDefinition asm;
            try {
                asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(
                    executablePath, readerParameters
                );
            } catch (Exception) {
                Console.WriteLine("{0} isn't a managed assembly. Not rewriting.", executablePath);
                return;
            }

            var asmCorlib = resolver.Resolve(AssemblyNameMscorlib, readerParameters);
            var asmFramework = resolver.Resolve(AssemblyNameXnaFramework, readerParameters);

            foreach (var module in asm.Modules) {
                // Force 32-bit x86
                module.Architecture = TargetArchitecture.I386;
                module.Attributes = ((module.Attributes & ~ModuleAttributes.Preferred32Bit) & ~ModuleAttributes.StrongNameSigned)
                    | ModuleAttributes.Required32Bit;

                // The main module will be console, switch it to GUI
                if (module.Kind == ModuleKind.Console)
                    module.Kind = ModuleKind.Windows;

                for (var i = 0; i < module.AssemblyReferences.Count; i++) {
                    var ar = module.AssemblyReferences[i];
                    Debug.WriteLine(ar.FullName);

                    string newFullName;
                    if (AssemblyNameReplacements.TryGetValue(ar.FullName, out newFullName)) {
                        var newReference = AssemblyNameReference.Parse(newFullName);
                        module.AssemblyReferences[i] = newReference;
                        Console.WriteLine("{0} -> {1}", ar.Name, newFullName);
                    } else {
                        Console.WriteLine("Ignoring {0}", ar.Name);
                    }
                }

                for (var i = 0; i < module.Resources.Count; i++) {
                    var rsrc = module.Resources[i];

                    switch (rsrc.Name) {
                        case "Microsoft.Xna.Framework.RuntimeProfile":
                            // FIXME: Detect version of executable and pick correct windows profile
                            module.Resources[i] = new EmbeddedResource(
                                rsrc.Name, rsrc.Attributes,
                                Encoding.ASCII.GetBytes("Windows.v3.1")
                            );

                            break;
                        default:
                            break;
                    }
                }

                foreach (var type in module.GetTypes()) {
                    string qualifiedName;

                    foreach (var method in type.Methods) {
                        if (!method.HasBody)
                            continue;

                        var body = method.Body;

                        // Patch out particular method calls.
                        for (int i = 0, c = body.Instructions.Count; i < c; i++) {
                            var instruction = body.Instructions[i];
                            MethodReference invokeTarget = null;

                            switch (instruction.OpCode.Code) {
                                case Code.Callvirt:
                                case Code.Call:
                                    invokeTarget = instruction.Operand as MethodReference;
                                    break;
                                default:
                                    continue;
                            }

                            if (invokeTarget == null)
                                continue;

                            qualifiedName = invokeTarget.DeclaringType.FullName + "::" + invokeTarget.Name;

                            switch (qualifiedName) {

                                // XBox 360 only.
                                case "System.Threading.Thread::SetProcessorAffinity":
                                case "Microsoft.Xna.Framework.GamerServices.GamerPresence::set_PresenceMode":
                                    KillCallInstruction(body, invokeTarget, ref i, ref c, true);
                                    break;

                                // Force windowed mode.
                                case "Microsoft.Xna.Framework.GraphicsDeviceManager::set_IsFullScreen":
                                    var ldc = body.Instructions[i - 1];
                                    if (ldc.OpCode.Code == Code.Ldc_I4_1) {
                                        body.Instructions[i - 1] = Instruction.Create(
                                            OpCodes.Ldc_I4_0
                                        );
                                    }

                                    break;
                                default:
                                    continue;
                            }
                        }

                    }
                }
            }

            var writerParameters = new WriterParameters {
                WriteSymbols = false
            };
            asm.Write(executablePath, writerParameters);
        }
    }

    public class RewriterAssemblyResolver : BaseAssemblyResolver {
        public readonly Dictionary<string, AssemblyDefinition> Cache = new Dictionary<string, AssemblyDefinition>();

        public override AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters) {
            var key = name.FullName;
            AssemblyDefinition result;

            string newKey;
            if (AssemblyRewriter.AssemblyNameReplacements.TryGetValue(key, out newKey))
                key = newKey;
            else
                Debugger.Break();

            if (Cache.TryGetValue(key, out result))
                return result;

            Cache[key] = result = base.Resolve(
                AssemblyNameReference.Parse(key), parameters
            );
            return result;
        }
    }
}
