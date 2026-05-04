using ICSharpCode.Decompiler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Mono.Cecil;

using Newtonsoft.Json;
using Oxide.Patcher.Common;
using Oxide.Patcher.Hooks;
using Oxide.Patcher.Patching;

namespace Oxide.Patcher.Docs
{
    public static class DocsGenerator
    {
        internal static AssemblyLoader AssemblyLoader;
        internal static string TargetDirectory;

        public static void GenerateFile(Project project, AssemblyLoader assemblyLoader, string outputFile = "docs.json")
        {
            AssemblyLoader = PatcherForm.MainForm != null ? PatcherForm.MainForm.AssemblyLoader : new AssemblyLoader(project, string.Empty);
            TargetDirectory = project.TargetDirectory;
            DocsData docsData = new DocsData();
            List<DocsHook> hooks = new List<DocsHook>();

            foreach (Manifest manifest in project.Manifests)
            {
                // Doc-gen loads its OWN AssemblyDefinition per manifest, completely separate from the
                // shared AssemblyLoader the UI uses. Patches applied here never reach the UI's instance,
                // so opening hook/method tabs after generating docs cannot show double-patched code.
                AssemblyDefinition docsAssembly = LoadDocsAssembly(project, manifest, out IAssemblyResolver docsResolver);
                if (docsAssembly == null)
                {
                    continue;
                }

                try
                {
                    List<(Hook Hook, MethodDefinition MethodDef)> patched = new List<(Hook, MethodDefinition)>();

                    // Pass 1: apply every hook's patch to the throwaway assembly. Doing this before any
                    // decompilation means the cached snapshot built lazily on the first decompile in pass 2
                    // already contains every hook's Interface.CallHook injection, so per-hook
                    // CodeAfterInjection lookups all see their own (and others') patches.
                    foreach (Hook hook in manifest.Hooks)
                    {
                        if (hook.Flagged)
                        {
                            Console.WriteLine($"Skipping flagged hook {hook.Name}");
                            continue;
                        }
                        try
                        {
                            MethodDefinition methodDef = GetMethod(docsAssembly, hook.TypeName, hook.Signature);
                            if (methodDef == null)
                            {
                                throw new Exception($"Failed to find method definition for hook {hook.Name}");
                            }

                            ILWeaver weaver = new ILWeaver(methodDef.Body) { Module = methodDef.Module };

                            hook.PreparePatch(methodDef, weaver);
                            hook.ApplyPatch(methodDef, weaver);

                            weaver.Apply(methodDef.Body);

                            patched.Add((hook, methodDef));
                        }
                        catch (Exception e)
                        {
                            ReportHookError(hook, e);
                        }
                    }

                    // Pass 2: build DocsHook entries. The first decompile per assembly serializes the
                    // AssemblyDefinition (now containing all patches) into a PEFile cached in
                    // InMemorySnapshotCache; every subsequent decompile reuses it.
                    foreach ((Hook hook, MethodDefinition methodDef) in patched)
                    {
                        try
                        {
                            DocsHook docsHook = new DocsHook(hook, methodDef, project.TargetDirectory);
                            hooks.Add(docsHook);
                        }
                        catch (NotSupportedException) { }
                        catch (DecompilerException ex)
                        {
                            Console.WriteLine($"Failed to decompile method for hook {hook.Name}: {ex.Message}{(ex.InnerException != null ? " | " + ex.InnerException.Message : string.Empty)}");
                        }
                        catch (Exception e)
                        {
                            ReportHookError(hook, e);
                        }
                    }
                }
                finally
                {
                    Decompiler.ClearInMemorySnapshotCache();
                    (docsResolver as IDisposable)?.Dispose();
                }
            }

            docsData.Hooks = hooks.ToArray();

            //Save file
            File.WriteAllText(outputFile, JsonConvert.SerializeObject(docsData, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented
            }));

            if (PatcherForm.MainForm != null)
            {
                MessageBox.Show("Successfully generated docs data file.", "Oxide Patcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                PatcherForm.MainForm.Invoke((MethodInvoker)delegate
                {
                    PatcherForm.MainForm.SetDocsButtonEnabled(true);
                });
            }
        }

        private static AssemblyDefinition LoadDocsAssembly(Project project, Manifest manifest, out IAssemblyResolver resolver)
        {
            resolver = null;
            string assemblyName = manifest.AssemblyName;
            string path = Path.Combine(project.TargetDirectory,
                $"{Path.GetFileNameWithoutExtension(assemblyName)}_Original{Path.GetExtension(assemblyName)}");
            if (!File.Exists(path))
            {
                path = Path.Combine(project.TargetDirectory, assemblyName);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"Failed to find assembly {assemblyName} for doc generation");
                    return null;
                }
            }

            resolver = new PatcherAssemblyResolver(project.TargetDirectory);
            return AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
        }

        private static void ReportHookError(Hook hook, Exception e)
        {
            if (PatcherForm.MainForm != null)
            {
                MessageBox.Show($"There was an error while generating docs data for '{hook.Name}'. ({e})", "Oxide Patcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                Console.WriteLine($"There was an error while generating docs data for '{hook.Name}'. ({e})");
            }
        }

        private static MethodDefinition GetMethod(AssemblyDefinition assemblyDefinition, string typeName, MethodSignature signature)
        {
            try
            {
                TypeDefinition type = assemblyDefinition.Modules.SelectMany(m => m.GetTypes()).Single(t => t.FullName == typeName);
                return type.Methods.Single(m => MethodSignatureMatches(Utility.GetMethodSignature(m), signature));
            }
            catch (Exception e)
            {
                return null;
            }
        }

        // Ignore exposure for now
        private static bool MethodSignatureMatches(MethodSignature obj1, MethodSignature othersig)
        {
            if (obj1.Name != othersig.Name)
            {
                return false;
            }

            if (obj1.Parameters.Length != othersig.Parameters.Length)
            {
                return false;
            }

            for (int i = 0; i < obj1.Parameters.Length; i++)
            {
                if (obj1.Parameters[i] != othersig.Parameters[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
