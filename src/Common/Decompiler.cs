using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Metadata;

using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using Oxide.Patcher.Patching;

using MethodDefinition = Mono.Cecil.MethodDefinition;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;

namespace Oxide.Patcher.Common
{
    /// <summary>
    /// Contains code decompiling utility methods
    /// </summary>
    public static class Decompiler
    {
        private static readonly DecompilerSettings DecompilerSettings = new DecompilerSettings
        {
            UsingDeclarations = false
        };

        private static readonly Dictionary<string, (PEFile PeFile, CSharpDecompiler Decompiler)> DecompilerCache = new Dictionary<string, (PEFile, CSharpDecompiler)>();

        // Used by doc generation: one snapshot per throwaway AssemblyDefinition, populated lazily on first
        // decompile. The caller MUST invoke ClearInMemorySnapshotCache() once doc-gen is done so the snapshot
        // doesn't outlive the AssemblyDefinition it was built from.
        private static readonly Dictionary<AssemblyDefinition, (PEFile PeFile, CSharpDecompiler Decompiler)> InMemorySnapshotCache = new Dictionary<AssemblyDefinition, (PEFile, CSharpDecompiler)>();

        public static void ClearInMemorySnapshotCache()
        {
            foreach (var entry in InMemorySnapshotCache.Values)
            {
                entry.PeFile.Dispose();
            }
            InMemorySnapshotCache.Clear();
        }

        /// <summary>
        /// Decompiles the specified method body to MSIL
        /// </summary>
        /// <param name="body"></param>
        /// <returns></returns>
        public static string DecompileToIL(MethodBody body)
        {
            StringBuilder sb = new StringBuilder();
            if (body?.Instructions == null)
            {
                return null;
            }

            Collection<Instruction> instructions = body.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                Instruction inst = instructions[i];
                sb.AppendLine(inst.ToString().Replace("\n", "\\n"));
            }
            return sb.ToString();
        }

        public static SyntaxTree GetSyntaxTree(MethodDefinition methodDefinition, bool useInMemorySnapshot = false)
        {
            using (DecompilerWrapper decompiler = GetDecompiler(methodDefinition, useInMemorySnapshot: useInMemorySnapshot))
            {
                MethodDefinitionHandle handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodDefinition.MetadataToken.ToInt32());
                return decompiler.Decompile(handle);
            }
        }

        public static async Task<string> GetSourceCode(MethodDefinition methodDefinition, ILWeaver weaver = null, bool useInMemorySnapshot = false)
        {
            try
            {
                return await Task.Run(() =>
                {
                    EntityHandle handle = MetadataTokens.EntityHandle(methodDefinition.MetadataToken.ToInt32());

                    using (DecompilerWrapper decompiler = GetDecompiler(methodDefinition, weaver, useInMemorySnapshot: useInMemorySnapshot))
                    {
                        return decompiler.DecompileAsString(handle);
                    }
                });
            }
            catch (Exception ex)
            {
                return "Error in creating source code from IL: " + ex;
            }
            finally
            {
                if (weaver != null)
                {
                    methodDefinition.Body = null;
                }
            }
        }

        private static DecompilerWrapper GetDecompiler(MethodDefinition methodDefinition, ILWeaver weaver = null,
                                                                 bool writeToStream = false, bool useInMemorySnapshot = false)
        {
            string targetDirectory = PatcherForm.MainForm?.CurrentProject.TargetDirectory ?? Program.PatchProject.TargetDirectory;

            if (useInMemorySnapshot && weaver == null && !writeToStream)
            {
                AssemblyDefinition assembly = methodDefinition.Module.Assembly;
                if (!InMemorySnapshotCache.TryGetValue(assembly, out (PEFile PeFile, CSharpDecompiler Decompiler) snapshot))
                {
                    MemoryStream snapshotStream = new MemoryStream();
                    assembly.Write(snapshotStream);
                    snapshotStream.Position = 0;

                    string snapshotPath = Path.Combine(targetDirectory, assembly.Name.Name);
                    PEFile snapshotPeFile = new PEFile(assembly.Name.Name, snapshotStream);
                    UniversalAssemblyResolver snapshotResolver = new UniversalAssemblyResolver(snapshotPath, true, snapshotPeFile.DetectTargetFrameworkId(), snapshotPeFile.DetectRuntimePack());
                    snapshot = (snapshotPeFile, new CSharpDecompiler(snapshotPeFile, snapshotResolver, DecompilerSettings));
                    InMemorySnapshotCache[assembly] = snapshot;
                }

                return new DecompilerWrapper(snapshot.Decompiler, snapshot.PeFile, null, ownsPeFile: false);
            }

            if (weaver != null || writeToStream)
            {
                weaver?.Apply(methodDefinition.Body);

                MemoryStream assemblyStream = new MemoryStream();
                methodDefinition.Module.Assembly.Write(assemblyStream);
                assemblyStream.Position = 0;

                string tempPath = Path.Combine(targetDirectory, "temporary");
                PEFile tempPeFile = new PEFile("temporary", assemblyStream);
                UniversalAssemblyResolver tempResolver = new UniversalAssemblyResolver(tempPath, true, tempPeFile.DetectTargetFrameworkId(), tempPeFile.DetectRuntimePack());

                return new DecompilerWrapper(new CSharpDecompiler(tempPeFile, tempResolver, DecompilerSettings), tempPeFile, assemblyStream, ownsPeFile: true);
            }
            else
            {
                string path = Path.Combine(targetDirectory, $"{methodDefinition.Module.Assembly.Name.Name}.dll");

                if (!DecompilerCache.TryGetValue(path, out (PEFile PeFile, CSharpDecompiler Decompiler) cached))
                {
                    PEFile peFile = new PEFile(path);
                    UniversalAssemblyResolver resolver = new UniversalAssemblyResolver(path, true, peFile.DetectTargetFrameworkId(), peFile.DetectRuntimePack());
                    cached = (peFile, new CSharpDecompiler(peFile, resolver, DecompilerSettings));
                    DecompilerCache[path] = cached;
                }

                return new DecompilerWrapper(cached.Decompiler, cached.PeFile, null, ownsPeFile: false);
            }
        }

        private readonly struct DecompilerWrapper : IDisposable
        {
            private readonly CSharpDecompiler _decompiler;
            private readonly PEFile _peFile;
            private readonly MemoryStream _assemblyStream;
            private readonly bool _ownsPeFile;

            public DecompilerWrapper(CSharpDecompiler decompiler, PEFile peFile, MemoryStream assemblyStream, bool ownsPeFile)
            {
                _decompiler = decompiler;
                _peFile = peFile;
                _assemblyStream = assemblyStream;
                _ownsPeFile = ownsPeFile;
            }

            public string DecompileAsString(EntityHandle handle) => _decompiler.DecompileAsString(handle);
            public SyntaxTree Decompile(EntityHandle handle) => _decompiler.Decompile(handle);

            public void Dispose()
            {
                if (_ownsPeFile)
                {
                    _peFile.Dispose();
                }
                _assemblyStream?.Dispose();
            }
        }
    }
}
